using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Compression.Core.Dictionary.Lzms;
using Compression.Core.Dictionary.Lzx;
using Compression.Core.Dictionary.Xpress;

namespace FileFormat.Wim;

/// <summary>
/// Reads resources from a WIM (Windows Imaging) file.
/// </summary>
/// <remarks>
/// <para>
/// Construct a <see cref="WimReader"/> by passing a seekable stream containing
/// a well-formed WIM file. After construction the header and resource table are
/// parsed. Call <see cref="ReadResource"/> to extract individual resources by index.
/// </para>
/// </remarks>
public sealed class WimReader : IDisposable {
  private readonly Stream _stream;
  private readonly WimHeader _header;
  private readonly IReadOnlyList<WimResourceEntry> _resourceTable;
  private bool _disposed;

  /// <summary>Gets the parsed WIM file header.</summary>
  public WimHeader Header => this._header;

  /// <summary>Gets the list of resource entries from the resource table.</summary>
  public IReadOnlyList<WimResourceEntry> Resources => this._resourceTable;

  /// <summary>
  /// Opens a WIM file from a seekable stream.
  /// </summary>
  /// <param name="stream">A seekable stream positioned at the start of the WIM data.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="InvalidDataException">Thrown when the WIM data is malformed.</exception>
  public WimReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);

    this._stream = stream;
    this._header = WimHeader.Read(stream);
    this._resourceTable = this.ReadResourceTable();
  }

  /// <summary>
  /// Reads and decompresses the resource at the given index.
  /// </summary>
  /// <param name="index">Zero-based index into <see cref="Resources"/>.</param>
  /// <returns>The decompressed resource bytes.</returns>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="index"/> is negative or out of range.
  /// </exception>
  /// <exception cref="InvalidDataException">Thrown when the resource data is malformed.</exception>
  /// <exception cref="ObjectDisposedException">Thrown when this reader has been disposed.</exception>
  public byte[] ReadResource(int index) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    ArgumentOutOfRangeException.ThrowIfNegative(index);
    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, this._resourceTable.Count);

    var entry = this._resourceTable[index];
    return this.ReadResourceEntry(entry);
  }

  /// <summary>
  /// Represents a named file entry extracted from WIM image metadata.
  /// </summary>
  /// <param name="FileName">The file name (leaf name, no path).</param>
  /// <param name="ResourceIndex">Index into <see cref="Resources"/> for this file's data, or -1 if not found.</param>
  /// <param name="FileSize">Uncompressed file size from the directory entry.</param>
  public sealed record WimFileEntry(string FileName, int ResourceIndex, long FileSize);

  /// <summary>
  /// Parses image metadata resources and returns named file entries.
  /// For WIM files created by external tools (e.g., 7-Zip) that embed
  /// directory metadata, this resolves file names to resource indices.
  /// </summary>
  /// <returns>A list of named file entries with their resource indices.</returns>
  public List<WimFileEntry> GetNamedFiles() {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    var result = new List<WimFileEntry>();

    // Find metadata resources (flag bit 1 set).
    for (var i = 0; i < this._resourceTable.Count; ++i) {
      if (!this._resourceTable[i].IsMetadata)
        continue;

      var metadataBytes = this.ReadResourceEntry(this._resourceTable[i]);
      ParseMetadataResource(metadataBytes, result);
    }

    return result;
  }

  private void ParseMetadataResource(byte[] metadata, List<WimFileEntry> result) {
    if (metadata.Length < 8)
      return;

    // SECURITY_DATA: first 4 bytes = total length, next 4 bytes = number of entries.
    var securitySize = BinaryPrimitives.ReadInt32LittleEndian(metadata);
    if (securitySize < 8)
      securitySize = 8;

    // Align to 8 bytes.
    var pos = (securitySize + 7) & ~7;

    // Parse DIRENTRY structures.
    this.ParseDirectoryEntries(metadata, pos, "", result);
  }

  private void ParseDirectoryEntries(byte[] metadata, int offset, string parentPath, List<WimFileEntry> result) {
    var pos = offset;

    // DIRENTRY on-disk layout (minimum 102 bytes fixed, padded to 8-byte boundary):
    //   +0:  length (uint64)
    //   +8:  attributes (uint32)
    //   +12: security_id (int32)
    //   +16: subdir_offset (uint64) — for directories, offset to child entries within metadata
    //   +24: unused1 (uint64)
    //   +32: unused2 (uint64)
    //   +40: creation_time (uint64)
    //   +48: last_access_time (uint64)
    //   +56: last_write_time (uint64)
    //   +64: hash (20 bytes) — SHA-1 of the resource data (zero for directories)
    //   +84: reparse_tag (uint32)
    //   +88: hard_link_group_id (uint64)
    //   +96: num_alternate_data_streams (uint16)
    //   +98: short_name_nbytes (uint16)
    //   +100: file_name_nbytes (uint16)
    //   +102: file_name (UTF-16LE), then short_name (UTF-16LE)
    const int MinEntrySize = 102;

    while (pos + MinEntrySize <= metadata.Length) {
      var entryLength = BinaryPrimitives.ReadInt64LittleEndian(metadata.AsSpan(pos));
      if (entryLength == 0)
        break; // end-of-directory sentinel

      var attributes   = BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(pos + 8));
      var subdirOffset = BinaryPrimitives.ReadInt64LittleEndian(metadata.AsSpan(pos + 16));
      var hash         = metadata.AsSpan(pos + 64, 20);

      var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(pos + 100));
      var fileName = "";
      if (fileNameLength > 0 && pos + 102 + fileNameLength <= metadata.Length)
        fileName = Encoding.Unicode.GetString(metadata, pos + 102, fileNameLength);

      // Remove null terminators.
      fileName = fileName.TrimEnd('\0');

      var isDirectory = (attributes & 0x10) != 0; // FILE_ATTRIBUTE_DIRECTORY

      if (!isDirectory && fileName.Length > 0 && !IsZeroHash(hash)) {
        // Find the resource by hash.
        var resourceIndex = this.FindResourceByHash(hash);
        var fileSize = resourceIndex >= 0 ? this._resourceTable[resourceIndex].OriginalSize : 0;
        var fullPath = parentPath.Length > 0 ? $"{parentPath}/{fileName}" : fileName;
        result.Add(new WimFileEntry(fullPath, resourceIndex, fileSize));
      }

      if (isDirectory && subdirOffset > 0) {
        // Root directory has an empty name; subdirectories have names.
        var dirPath = fileName.Length > 0
          ? (parentPath.Length > 0 ? $"{parentPath}/{fileName}" : fileName)
          : parentPath;
        this.ParseDirectoryEntries(metadata, (int)subdirOffset, dirPath, result);
      }

      pos += (int)entryLength;
    }
  }

  private static bool IsZeroHash(ReadOnlySpan<byte> hash) {
    for (var i = 0; i < hash.Length; ++i)
      if (hash[i] != 0) return false;
    return true;
  }

  private int FindResourceByHash(ReadOnlySpan<byte> hash) {
    for (var i = 0; i < this._resourceTable.Count; ++i) {
      var entry = this._resourceTable[i];
      if (entry.Hash is not null && hash.SequenceEqual(entry.Hash))
        return i;
    }
    return -1;
  }

  /// <summary>
  /// Releases all resources used by this <see cref="WimReader"/>.
  /// Does not close the underlying stream.
  /// </summary>
  public void Dispose() {
    this._disposed = true;
  }

  // -------------------------------------------------------------------------
  // Resource table reading
  // -------------------------------------------------------------------------

  private List<WimResourceEntry> ReadResourceTable() {
    var tableInfo = this._header.OffsetTableResource;
    if (tableInfo is null)
      return [];

    this._stream.Seek(tableInfo.Offset, SeekOrigin.Begin);

    var tableSize  = tableInfo.CompressedSize; // offset table is always stored uncompressed
    var entryCount = (int)(tableSize / WimConstants.LookupTableEntrySize);
    var entries    = new List<WimResourceEntry>(Math.Max(entryCount, 0));

    Span<byte> buf = stackalloc byte[WimConstants.LookupTableEntrySize];
    for (var i = 0; i < entryCount; ++i) {
      this._stream.ReadExactly(buf);

      // RESHDR_DISK_SHORT: packed size+flags (8), offset (8), original size (8)
      var sizeAndFlags   = BinaryPrimitives.ReadUInt64LittleEndian(buf);
      var compressedSize = (long)(sizeAndFlags & 0x00FFFFFFFFFFFFFF);
      var flags          = (uint)(sizeAndFlags >> 56);
      var offset         = BinaryPrimitives.ReadInt64LittleEndian(buf[8..]);
      var originalSize   = BinaryPrimitives.ReadInt64LittleEndian(buf[16..]);
      // Bytes 24-25: part number, 26-29: ref count, 30-49: SHA-1 hash
      var hash = buf[30..50].ToArray();

      entries.Add(new WimResourceEntry(compressedSize, originalSize, offset, flags, hash));
    }

    return entries;
  }

  // -------------------------------------------------------------------------
  // Resource data reading
  // -------------------------------------------------------------------------

  private byte[] ReadResourceEntry(WimResourceEntry entry) {
    if (entry.OriginalSize == 0)
      return [];

    this._stream.Seek(entry.Offset, SeekOrigin.Begin);

    if (!entry.IsCompressed) {
      // Uncompressed: read directly.
      var raw = new byte[entry.OriginalSize];
      this._stream.ReadExactly(raw);
      return raw;
    }

    // Compressed: read the chunk table then decompress chunk-by-chunk.
    var chunkSize  = (int)this._header.ChunkSize;
    var origSize  = entry.OriginalSize;
    var chunkCount = (int)((origSize + chunkSize - 1) / chunkSize);

    // Chunk table: (chunkCount - 1) cumulative offsets.
    // Entry width: 4 bytes (uint32) for resources < 4 GB, 8 bytes (uint64) for >= 4 GB.
    var entryWidth     = origSize >= 0xFFFFFFFF ? 8 : 4;
    var chunkTableBytes = (chunkCount - 1) * entryWidth;
    var compressedSizes = new long[chunkCount];

    if (chunkTableBytes > 0) {
      var chunkTableBuf = new byte[chunkTableBytes];
      this._stream.ReadExactly(chunkTableBuf);

      // Read cumulative offsets (relative to end of chunk table = start of chunk data).
      var offsets = new long[chunkCount - 1];
      for (var i = 0; i < chunkCount - 1; ++i) {
        offsets[i] = entryWidth == 4
          ? BinaryPrimitives.ReadUInt32LittleEndian(chunkTableBuf.AsSpan(i * entryWidth, entryWidth))
          : BinaryPrimitives.ReadInt64LittleEndian(chunkTableBuf.AsSpan(i * entryWidth, entryWidth));
      }

      // Convert cumulative offsets to per-chunk compressed sizes.
      // First chunk: from 0 to offsets[0].
      compressedSizes[0] = offsets[0];
      for (var i = 1; i < chunkCount - 1; ++i)
        compressedSizes[i] = offsets[i] - offsets[i - 1];

      // Last chunk: from last offset to end of compressed data.
      var totalChunkData = entry.CompressedSize - chunkTableBytes;
      compressedSizes[chunkCount - 1] = totalChunkData - offsets[chunkCount - 2];
    } else {
      // Single chunk: its compressed size is the entire payload.
      compressedSizes[0] = entry.CompressedSize;
    }

    // Decompress each chunk and assemble the output.
    var output = new byte[origSize];
    var outPos = 0;

    for (var i = 0; i < chunkCount; ++i) {
      var uncompressedChunkSize = (int)Math.Min(chunkSize, origSize - (long)i * chunkSize);
      var compSize = compressedSizes[i];

      if (compSize <= 0)
        ThrowInvalidChunkSize(i);

      var compBuf = new byte[compSize];
      this._stream.ReadExactly(compBuf);

      // If compressed size equals the uncompressed chunk size, the chunk is stored raw.
      byte[] decompressed;
      if (compSize == uncompressedChunkSize)
        decompressed = compBuf;
      else
        decompressed = this.DecompressChunk(compBuf, uncompressedChunkSize);

      decompressed.CopyTo(output, outPos);
      outPos += decompressed.Length;
    }

    return output;
  }

  // -------------------------------------------------------------------------
  // Decompression dispatch
  // -------------------------------------------------------------------------

  private byte[] DecompressChunk(byte[] compressedData, int uncompressedSize) =>
    this._header.CompressionType switch {
      WimConstants.CompressionXpress => XpressDecompressor.Decompress(
        compressedData.AsSpan(), uncompressedSize),

      WimConstants.CompressionXpressHuffman => XpressHuffmanDecompressor.Decompress(
        compressedData.AsSpan(), uncompressedSize),

      WimConstants.CompressionLzx => DecompressLzx(compressedData, uncompressedSize),

      WimConstants.CompressionLzms => new LzmsDecompressor().Decompress(compressedData, uncompressedSize),

      WimConstants.CompressionNone => compressedData,

      _ => throw new NotSupportedException(
        $"Unsupported WIM compression type: {this._header.CompressionType}.")
    };

  private static byte[] DecompressLzx(byte[] compressedData, int uncompressedSize) {
    using var ms = new MemoryStream(compressedData);
    var decompressor = new LzxDecompressor(ms, WimConstants.LzxWindowBits);
    return decompressor.Decompress(uncompressedSize);
  }

  // -------------------------------------------------------------------------
  // Throw helpers
  // -------------------------------------------------------------------------

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidChunkSize(int chunkIndex) =>
    throw new InvalidDataException(
      $"WIM resource chunk {chunkIndex} has an invalid compressed size.");
}
