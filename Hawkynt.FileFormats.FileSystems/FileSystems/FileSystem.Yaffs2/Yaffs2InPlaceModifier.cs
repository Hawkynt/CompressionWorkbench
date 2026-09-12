#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Yaffs2;

/// <summary>
/// True in-place log-structured mutator for YAFFS2 raw-NAND images.
/// <para>
/// YAFFS2 is a log-structured flash filesystem by spec: "modifying" a file does
/// NOT rewrite the chunk on the medium. Instead, fresh chunks are appended at
/// the next free slot carrying the same objectId/chunkId but a higher seqNumber,
/// and the old chunks stay byte-identical at their original offsets until
/// garbage collection coalesces a block. The reader resolves the live view by
/// keeping the chunk with the highest seqNumber per (objectId, chunkId).
/// </para>
/// <para>
/// This mutator honors that invariant:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Add"/> appends a new header chunk + data chunks at
///   the image tail with a fresh objectId. Existing chunks untouched.</description></item>
///   <item><description><see cref="Replace"/> appends fresh data chunks at the tail with
///   the SAME objectId/chunkId but a higher seqNumber, plus a fresh header with
///   updated <c>file_size_low</c>. Old chunks stay byte-identical at their
///   original offsets — the reader's seqNumber-max filter resolves the live
///   bytes.</description></item>
///   <item><description><see cref="Remove"/> appends a tombstone header chunk
///   (parent_obj_id = <c>0xFFFFFFFE</c>) at the tail; old chunks remain.</description></item>
/// </list>
/// <para>
/// Image growth happens implicitly: we always append at <see cref="Stream.Length"/>.
/// Existing bytes in [0, oldLength) are never overwritten.
/// </para>
/// <para>
/// Honest scope: only flat (root-directory) operations are implemented. Nested
/// directory creation on Add, name rename, and truncation-with-shrink garbage
/// collection are deferred — see the per-method <c>NotSupportedException</c>
/// messages for the exact deferred sub-features.
/// </para>
/// </summary>
internal static class Yaffs2InPlaceModifier {

  internal const int ChunkSize = 2048;
  internal const int SpareSize = 64;
  internal const int Stride = ChunkSize + SpareSize;

  private const int TypeFile = 1;
  private const int TypeDirectory = 3;

  /// <summary>Root directory object id (YAFFS2 convention).</summary>
  private const int RootObjectId = 1;

  /// <summary>Reserved object id ceiling (root=1, lost+found and friends up to 4).
  /// Freshly allocated objects start above this range.</summary>
  private const int ReservedObjectIdCeiling = 4;

  /// <summary>
  /// Tombstone parent_obj_id marker. Real Yaffs uses an unlinked-directory parent
  /// id; we use a sentinel that the scanner recognises as "this object has been
  /// deleted" without conflicting with any normal directory id.
  /// </summary>
  internal const int TombstoneParentId = unchecked((int)0xFFFFFFFE);

  // ────────────────────────────────────────────────────────────────────────
  // Public API
  // ────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Appends new files at the image tail. For each input whose name matches an
  /// existing live object, performs a log-structured <see cref="Replace"/>;
  /// otherwise allocates a fresh objectId and appends header + data chunks.
  /// Existing bytes in the image are never overwritten.
  /// </summary>
  public static void Add(Stream image, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = Path.GetFileName(input.ArchiveName);
      if (string.IsNullOrEmpty(name)) continue;
      // Yaffs2InPlaceModifier intentionally only supports flat (root) entries.
      // Nested directory creation on Add is deferred — the rebuild path is the
      // right tool for adding files into subdirectories.
      if (input.ArchiveName.Replace('\\', '/').Contains('/'))
        throw new NotSupportedException(
          "Yaffs2InPlaceModifier.Add: nested path entries (containing '/' or '\\') are deferred — " +
          "use the rebuild path for adding files into subdirectories.");

      var data = input.ReadContent();
      var state = ScanState(image);
      var existing = FindLiveFileByName(state, name);
      if (existing is { } live) {
        ReplaceCore(image, state, live.ObjectId, name, data);
      } else {
        AddCore(image, state, name, data);
      }
    }
  }

  /// <summary>
  /// Log-structured replace: write fresh data chunks at the tail with the same
  /// objectId, same chunkId, higher seqNumber. Old chunks stay byte-identical
  /// at their original offsets. The file's header is re-emitted with the new
  /// size so the reader can bound the live chunkId range.
  /// </summary>
  public static void Replace(Stream image, string name, byte[] newData) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(newData);

    var state = ScanState(image);
    var existing = FindLiveFileByName(state, name)
      ?? throw new InvalidOperationException(
        $"Yaffs2InPlaceModifier.Replace: no live file named '{name}' in image.");
    ReplaceCore(image, state, existing.ObjectId, name, newData);
  }

  /// <summary>
  /// Log-structured remove: write a tombstone header chunk (parent_obj_id =
  /// <see cref="TombstoneParentId"/>) at the image tail. Old chunks for the
  /// object stay byte-identical at their original offsets.
  /// </summary>
  public static void Remove(Stream image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var state = ScanState(image);
    var existing = FindLiveFileByName(state, name)
      ?? throw new InvalidOperationException(
        $"Yaffs2InPlaceModifier.Remove: no live file named '{name}' in image.");

    var seq = state.AllocateSeq();
    var headerChunk = BuildObjectHeader(TypeFile, TombstoneParentId, name, 0);
    var tail = BuildChunkWithSpare(headerChunk, seq, existing.ObjectId, chunkId: 0, nBytes: 0);
    AppendTail(image, tail);
  }

  // ────────────────────────────────────────────────────────────────────────
  // Core mutators
  // ────────────────────────────────────────────────────────────────────────

  private static void AddCore(Stream image, State state, string name, byte[] data) {
    var objectId = state.AllocateObjectId();
    var seq = state.AllocateSeq();

    var headerChunk = BuildObjectHeader(TypeFile, RootObjectId, name, data.Length);
    AppendTail(image, BuildChunkWithSpare(headerChunk, seq, objectId, chunkId: 0, nBytes: 0));

    var offset = 0;
    var chunkIdx = 1;
    while (offset < data.Length) {
      var remaining = data.Length - offset;
      var thisChunkBytes = Math.Min(remaining, ChunkSize);
      var dataChunk = new byte[ChunkSize];
      Buffer.BlockCopy(data, offset, dataChunk, 0, thisChunkBytes);
      AppendTail(image, BuildChunkWithSpare(
        dataChunk, state.AllocateSeq(), objectId, chunkId: chunkIdx, nBytes: (uint)thisChunkBytes));
      offset += thisChunkBytes;
      chunkIdx++;
    }
  }

  private static void ReplaceCore(Stream image, State state, int objectId, string name, byte[] newData) {
    // Header chunk first with updated file_size_low — the scanner uses this to
    // bound the live chunkId range so stale tail chunks of the prior version
    // (chunkId > ceil(newSize/ChunkSize)) are correctly ignored.
    var headerChunk = BuildObjectHeader(TypeFile, RootObjectId, name, newData.Length);
    AppendTail(image, BuildChunkWithSpare(
      headerChunk, state.AllocateSeq(), objectId, chunkId: 0, nBytes: 0));

    var offset = 0;
    var chunkIdx = 1;
    while (offset < newData.Length) {
      var remaining = newData.Length - offset;
      var thisChunkBytes = Math.Min(remaining, ChunkSize);
      var dataChunk = new byte[ChunkSize];
      Buffer.BlockCopy(newData, offset, dataChunk, 0, thisChunkBytes);
      AppendTail(image, BuildChunkWithSpare(
        dataChunk, state.AllocateSeq(), objectId, chunkId: chunkIdx, nBytes: (uint)thisChunkBytes));
      offset += thisChunkBytes;
      chunkIdx++;
    }
  }

  // ────────────────────────────────────────────────────────────────────────
  // State: scan once to learn the highest seqNumber + live object ids.
  // ────────────────────────────────────────────────────────────────────────

  private sealed class State {
    public uint MaxSeq;
    public int MaxObjectId;
    public required Yaffs2Scanner.ScanResult Scan;

    public uint AllocateSeq() => ++this.MaxSeq;

    public int AllocateObjectId() {
      this.MaxObjectId = Math.Max(this.MaxObjectId + 1, ReservedObjectIdCeiling + 1);
      return this.MaxObjectId;
    }
  }

  private readonly record struct LiveFile(int ObjectId, string Name);

  private static State ScanState(Stream image) {
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var bytes = ms.ToArray();
    var scan = Yaffs2Scanner.Scan(bytes);

    uint maxSeq = 0x1000; // writer seeds at 0x1000; keep modifier monotonically above.
    var maxObjectId = ReservedObjectIdCeiling;

    var stride = Stride;
    for (var off = 0; off + stride <= bytes.Length; off += stride) {
      var spare = bytes.AsSpan(off + ChunkSize, SpareSize);
      var seq = BinaryPrimitives.ReadUInt32LittleEndian(spare.Slice(0, 4));
      var objId = BinaryPrimitives.ReadInt32LittleEndian(spare.Slice(4, 4));
      if (seq > maxSeq && seq < 0xFFFF_FFF0u) maxSeq = seq;
      if (objId > maxObjectId && objId < 1_000_000) maxObjectId = objId;
    }
    return new State { MaxSeq = maxSeq, MaxObjectId = maxObjectId, Scan = scan };
  }

  /// <summary>Finds a live file by name (root-level lookup). Returns null if absent.</summary>
  private static LiveFile? FindLiveFileByName(State state, string name) {
    if (!state.Scan.ParseOk) return null;
    foreach (var obj in state.Scan.Objects) {
      if (obj.Type != Yaffs2Scanner.YObjectType.File) continue;
      if (!string.Equals(obj.Name, name, StringComparison.Ordinal)) continue;
      return new LiveFile(obj.ObjectId, obj.Name);
    }
    return null;
  }

  // ────────────────────────────────────────────────────────────────────────
  // Low-level chunk emission (matches Yaffs2Writer exactly so the on-disk
  // bytes the modifier writes are byte-equivalent to what the writer emits).
  // ────────────────────────────────────────────────────────────────────────

  private static byte[] BuildObjectHeader(int type, int parentId, string name, long fileSize) {
    var chunk = new byte[ChunkSize];

    BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(0, 4), type);
    BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4, 4), parentId);

    if (!string.IsNullOrEmpty(name)) {
      var nameBytes = Encoding.UTF8.GetBytes(name);
      var copyLen = Math.Min(nameBytes.Length, 255);
      nameBytes.AsSpan(0, copyLen).CopyTo(chunk.AsSpan(12, copyLen));
    }

    ushort checksum = 0;
    for (var i = 12; i < 12 + 256 && chunk[i] != 0; i++)
      checksum += chunk[i];
    BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(8, 2), checksum);

    var mode = type == TypeDirectory ? 0x41EDu : 0x81A4u;
    BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(272, 4), mode);

    BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(296, 4), (int)Math.Min(int.MaxValue, fileSize));

    return chunk;
  }

  private static byte[] BuildChunkWithSpare(byte[] chunkData, uint seqNumber, int objId, int chunkId, uint nBytes) {
    var result = new byte[Stride];
    Buffer.BlockCopy(chunkData, 0, result, 0, Math.Min(chunkData.Length, ChunkSize));

    for (var i = ChunkSize; i < Stride; i++)
      result[i] = 0xFF;

    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(ChunkSize, 4), seqNumber);
    BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(ChunkSize + 4, 4), objId);
    BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(ChunkSize + 8, 4), chunkId);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(ChunkSize + 12, 4), nBytes);

    return result;
  }

  private static void AppendTail(Stream image, byte[] chunk) {
    image.Position = image.Length;
    image.Write(chunk, 0, chunk.Length);
  }
}
