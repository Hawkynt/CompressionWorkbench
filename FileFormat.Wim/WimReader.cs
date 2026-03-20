using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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

    long tableSize  = tableInfo.CompressedSize; // offset table is always stored uncompressed
    int entryCount  = (int)(tableSize / WimConstants.ResourceEntrySize);
    var entries     = new List<WimResourceEntry>(entryCount);

    Span<byte> buf = stackalloc byte[WimConstants.ResourceEntrySize];
    for (int i = 0; i < entryCount; ++i) {
      this._stream.ReadExactly(buf);
      var compressedSize = BinaryPrimitives.ReadInt64LittleEndian(buf);
      var originalSize   = BinaryPrimitives.ReadInt64LittleEndian(buf[8..]);
      var offset         = BinaryPrimitives.ReadInt64LittleEndian(buf[16..]);
      var flags          = BinaryPrimitives.ReadUInt32LittleEndian(buf[24..]);
      entries.Add(new WimResourceEntry(compressedSize, originalSize, offset, flags));
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
    int chunkSize  = (int)this._header.ChunkSize;
    long origSize  = entry.OriginalSize;
    int chunkCount = (int)((origSize + chunkSize - 1) / chunkSize);

    // Read chunk table: (chunkCount - 1) × 8-byte compressed sizes.
    // The chunk table is stored at the beginning of the resource payload.
    int chunkTableBytes = (chunkCount - 1) * 8;
    long[] compressedSizes = new long[chunkCount];

    if (chunkTableBytes > 0) {
      var chunkTableBuf = new byte[chunkTableBytes];
      this._stream.ReadExactly(chunkTableBuf);

      for (int i = 0; i < chunkCount - 1; ++i)
        compressedSizes[i] = BinaryPrimitives.ReadInt64LittleEndian(
          chunkTableBuf.AsSpan(i * 8, 8));

      // Compute the last chunk's compressed size from the total.
      long sumOfOthers = 0;
      for (int i = 0; i < chunkCount - 1; ++i)
        sumOfOthers += compressedSizes[i];

      long totalChunkData = entry.CompressedSize - chunkTableBytes;
      compressedSizes[chunkCount - 1] = totalChunkData - sumOfOthers;
    } else {
      // Single chunk: its compressed size is the entire payload.
      compressedSizes[0] = entry.CompressedSize;
    }

    // Decompress each chunk and assemble the output.
    var output = new byte[origSize];
    int outPos = 0;

    for (int i = 0; i < chunkCount; ++i) {
      int uncompressedChunkSize = (int)Math.Min(chunkSize, origSize - (long)i * chunkSize);
      long compSize = compressedSizes[i];

      if (compSize <= 0)
        ThrowInvalidChunkSize(i);

      var compBuf = new byte[compSize];
      this._stream.ReadExactly(compBuf);

      byte[] decompressed = this.DecompressChunk(compBuf, uncompressedChunkSize);
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
