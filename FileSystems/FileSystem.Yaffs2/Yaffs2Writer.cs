#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Yaffs2;

/// <summary>
/// Builds a YAFFS2 raw-NAND image from scratch, compatible with mkyaffs2image.
/// Layout: 2048-byte chunks + 64-byte packed_tags2 spare areas.
/// Object headers for root dir, file inodes, then data chunks.
/// All data stored uncompressed (YAFFS2 is a flash filesystem, not a compressor).
/// </summary>
internal sealed class Yaffs2Writer {

  private readonly List<(string[] Segments, byte[] Data)> _files = [];

  /// <summary>Chunk and spare sizes (mkyaffs2image default).</summary>
  internal const int ChunkSize = 2048;
  internal const int SpareSize = 64;
  internal const int Stride = ChunkSize + SpareSize;

  /// <summary>YAFFS2 object types.</summary>
  private const int TypeFile = 1;
  private const int TypeDirectory = 3;

  /// <summary>Root directory object ID (YAFFS2 convention).</summary>
  private const int RootObjectId = 1;

  /// <summary>Highest reserved object id (root=1, lost+found and friends up to 4).
  /// Freshly allocated objects start above this range.</summary>
  private const int ReservedObjectIdCeiling = 4;

  /// <summary>Adds a file to the image. The name may contain '/' separators,
  /// in which case the leading segments become real YAFFS2 directory objects.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    // Normalise separators and drop empty / "." segments so a path such as
    // "docs/api/reference.txt" yields the segments ["docs", "api", "reference.txt"].
    var segments = name.Replace('\\', '/')
      .Split('/', StringSplitOptions.RemoveEmptyEntries)
      .Where(s => s != ".")
      .ToArray();
    if (segments.Length == 0)
      throw new ArgumentException("File name must not be empty.", nameof(name));
    _files.Add((segments, data));
  }

  /// <summary>
  /// Builds a complete YAFFS2 image. Layout:
  /// 1. Root directory object header (object ID 1, type=directory, parent=1)
  /// 2. Directory object headers for every intermediate path segment
  /// 3. For each file: object header (type=file, parent=its directory) + data chunks
  /// </summary>
  public byte[] Build() => MaterialiseChunks(this.BuildChunks());

  /// <summary>Lays the image out as its ordered list of NAND chunks.</summary>
  private List<byte[]> BuildChunks() {
    var chunks = new List<byte[]>();
    uint seqNumber = 0x1000; // Sequence numbers start at a conventional value.
    var nextObjectId = ReservedObjectIdCeiling + 1; // Allocate fresh ids above the reserved range.

    // 1. Root directory object header
    chunks.Add(BuildChunkWithSpare(
      BuildObjectHeader(TypeDirectory, RootObjectId, "", 0),
      seqNumber, RootObjectId, chunkId: 0, nBytes: 0));

    // Maps a directory path (e.g. "docs/api") to the object id of its directory.
    // The empty path maps to the root directory.
    var dirIds = new Dictionary<string, int> { [""] = RootObjectId };

    // 2. + 3. Per-file: ensure parent directories exist, then write the file.
    foreach (var (segments, data) in _files) {
      // Ensure every directory along the path (all segments except the leaf) exists.
      var parentId = RootObjectId;
      var prefix = "";
      for (var i = 0; i < segments.Length - 1; i++) {
        prefix = prefix.Length == 0 ? segments[i] : prefix + "/" + segments[i];
        if (dirIds.TryGetValue(prefix, out var existing)) {
          parentId = existing;
          continue;
        }

        var dirObjId = nextObjectId++;
        seqNumber++;
        chunks.Add(BuildChunkWithSpare(
          BuildObjectHeader(TypeDirectory, parentId, segments[i], 0),
          seqNumber, dirObjId, chunkId: 0, nBytes: 0));
        dirIds[prefix] = dirObjId;
        parentId = dirObjId;
      }

      var leaf = segments[^1];
      var fileObjId = nextObjectId++;
      seqNumber++;

      // Object header for this file
      chunks.Add(BuildChunkWithSpare(
        BuildObjectHeader(TypeFile, parentId, leaf, data.Length),
        seqNumber, fileObjId, chunkId: 0, nBytes: 0));

      // Data chunks (each carries up to ChunkSize bytes)
      var offset = 0;
      var chunkIdx = 1;
      while (offset < data.Length) {
        var remaining = data.Length - offset;
        var thisChunkBytes = Math.Min(remaining, ChunkSize);
        var chunkData = new byte[ChunkSize];
        Buffer.BlockCopy(data, offset, chunkData, 0, thisChunkBytes);

        chunks.Add(BuildChunkWithSpare(
          chunkData, seqNumber, fileObjId, chunkId: chunkIdx, nBytes: (uint)thisChunkBytes));

        offset += thisChunkBytes;
        chunkIdx++;
      }
    }

    return chunks;
  }

  /// <summary>Concatenates the chunk list into one image.</summary>
  private static byte[] MaterialiseChunks(List<byte[]> chunks) {
    var totalBytes = (long)chunks.Count * Stride;
    if (totalBytes > Array.MaxLength)
      throw new InvalidOperationException(
        $"YAFFS2: a {totalBytes:N0}-byte image exceeds the array limit; write it to a seekable stream instead.");
    var image = new byte[totalBytes];
    for (var i = 0; i < chunks.Count; i++)
      Buffer.BlockCopy(chunks[i], 0, image, i * Stride, Stride);

    return image;
  }

  /// <summary>
  /// Writes the image to a stream, one chunk at a time. Concatenating them into
  /// a single array first is what capped the image at what a byte[] can address.
  /// </summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    foreach (var chunk in this.BuildChunks())
      output.Write(chunk, 0, Stride);
    output.Flush();
  }

  /// <summary>
  /// Builds a YAFFS2 object header (512 bytes minimum, padded to ChunkSize).
  /// Layout matches yaffs_obj_hdr:
  ///   type           i32   offset 0
  ///   parent_obj_id  i32   offset 4
  ///   checksum       u16   offset 8 (name checksum)
  ///   unused2        u16   offset 10
  ///   name[256]            offset 12
  ///   yst_mode       u32   offset 272
  ///   yst_uid        u32   offset 276
  ///   yst_gid        u32   offset 280
  ///   yst_atime      u32   offset 284
  ///   yst_mtime      u32   offset 288
  ///   yst_ctime      u32   offset 292
  ///   file_size_low  i32   offset 296
  /// </summary>
  private static byte[] BuildObjectHeader(int type, int parentId, string name, int fileSize) {
    var chunk = new byte[ChunkSize];

    BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(0, 4), type);
    BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4, 4), parentId);

    // Name at offset 12 (max 256 bytes, UTF-8, NUL-terminated)
    if (!string.IsNullOrEmpty(name)) {
      var nameBytes = Encoding.UTF8.GetBytes(name);
      var copyLen = Math.Min(nameBytes.Length, 255);
      nameBytes.AsSpan(0, copyLen).CopyTo(chunk.AsSpan(12, copyLen));
      // NUL terminator is already there from zeroed array
    }

    // Simple name checksum (sum of name bytes, stored as u16)
    ushort checksum = 0;
    for (var i = 12; i < 12 + 256 && chunk[i] != 0; i++)
      checksum += chunk[i];
    BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(8, 2), checksum);

    // Mode: 0755 for dirs, 0644 for files
    var mode = type == TypeDirectory ? 0x41EDu : 0x81A4u;
    BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(272, 4), mode);

    // uid, gid = 0 (already zeroed)
    // Timestamps (leave as 0 for deterministic output)
    // file_size_low at offset 296
    BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(296, 4), fileSize);

    return chunk;
  }

  /// <summary>
  /// Combines a chunk with a packed_tags2 spare area into a single Stride-sized buffer.
  /// Packed tags layout (mkyaffs2image format):
  ///   seq_number  u32  offset 0
  ///   obj_id      u32  offset 4
  ///   chunk_id    u32  offset 8
  ///   n_bytes     u32  offset 12
  /// Remaining spare bytes are 0xFF (erased flash state).
  /// </summary>
  private static byte[] BuildChunkWithSpare(byte[] chunkData, uint seqNumber, int objId, int chunkId, uint nBytes) {
    var result = new byte[Stride];

    // Copy chunk data
    Buffer.BlockCopy(chunkData, 0, result, 0, Math.Min(chunkData.Length, ChunkSize));

    // Spare area: fill with 0xFF first (erased flash)
    for (var i = ChunkSize; i < Stride; i++)
      result[i] = 0xFF;

    // Write packed tags
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(ChunkSize, 4), seqNumber);
    BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(ChunkSize + 4, 4), objId);
    BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(ChunkSize + 8, 4), chunkId);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(ChunkSize + 12, 4), nBytes);

    return result;
  }
}
