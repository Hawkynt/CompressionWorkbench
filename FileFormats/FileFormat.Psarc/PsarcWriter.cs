using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.Psarc;

/// <summary>
/// Creates a Sony PlayStation archive (PSARC) v1.4 with zlib block compression.
/// </summary>
public sealed class PsarcWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly int _blockSize;
  private readonly string _compression;
  private readonly List<(string Name, byte[] Data)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="PsarcWriter"/>.
  /// </summary>
  /// <param name="stream">A seekable, writable output stream.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open on dispose.</param>
  /// <param name="blockSize">Block size in bytes (default 65536).</param>
  /// <param name="compression">Compression algorithm; only "zlib" is supported on write.</param>
  public PsarcWriter(Stream stream, bool leaveOpen = false, int blockSize = PsarcConstants.DefaultBlockSize, string compression = PsarcConstants.CompressionZlib) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("PSARC writer requires a seekable stream.", nameof(stream));
    if (blockSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(blockSize));
    if (compression != PsarcConstants.CompressionZlib)
      throw new NotSupportedException($"PSARC writer only supports zlib compression (got '{compression}').");
    this._leaveOpen   = leaveOpen;
    this._blockSize   = blockSize;
    this._compression = compression;
  }

  /// <summary>
  /// Adds an entry to the archive. The data is buffered in memory until <see cref="Finish"/> (or <see cref="Dispose"/>) is called.
  /// </summary>
  public void AddEntry(string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._entries.Add((NormalizePath(name), data));
  }

  /// <summary>
  /// Finalizes the archive: emits header, TOC, block-sizes table, and compressed data.
  /// </summary>
  public void Finish() {
    if (this._finished) return;
    this._finished = true;

    var manifestBytes = BuildManifest();

    // The manifest is always TOC entry 0; user entries follow in declaration order.
    var allFiles = new List<(string Name, byte[] Data)>(this._entries.Count + 1) {
      ("", manifestBytes)
    };
    allFiles.AddRange(this._entries);

    var blockSizeWidth = PsarcReader.ComputeBlockSizeWidth(this._blockSize);

    var compressedBlocks = new List<List<byte[]>>(allFiles.Count);
    var blockSizes = new List<uint>();
    var startBlockIndices = new int[allFiles.Count];

    for (var i = 0; i < allFiles.Count; ++i) {
      startBlockIndices[i] = blockSizes.Count;
      var data = allFiles[i].Data;
      var fileBlocks = new List<byte[]>();
      for (var off = 0; off < data.Length; off += this._blockSize) {
        var len = Math.Min(this._blockSize, data.Length - off);
        var compressed = CompressBlock(data, off, len);
        var isFullBlock = len == this._blockSize;
        // The 0-sentinel "stored, full block_size on disk" path is only safe for FULL blocks. For partial
        // last blocks we must always emit real zlib bytes — otherwise the reader cannot tell stored-partial
        // from zlib-partial without a magic check that has false positives on random payloads.
        if (isFullBlock && compressed.Length >= len) {
          var raw = new byte[len];
          Buffer.BlockCopy(data, off, raw, 0, len);
          fileBlocks.Add(raw);
          blockSizes.Add(0u);
        } else {
          fileBlocks.Add(compressed);
          blockSizes.Add((uint)compressed.Length);
        }
      }
      if (data.Length == 0)
        startBlockIndices[i] = blockSizes.Count;
      compressedBlocks.Add(fileBlocks);
    }

    var tocLength = PsarcConstants.HeaderSize
                    + PsarcConstants.TocEntrySize * allFiles.Count
                    + blockSizeWidth * blockSizes.Count;

    var startOffsets = new long[allFiles.Count];
    long cursor = tocLength;
    for (var i = 0; i < allFiles.Count; ++i) {
      startOffsets[i] = cursor;
      foreach (var b in compressedBlocks[i])
        cursor += b.Length;
    }

    this.WriteHeader(tocLength, allFiles.Count, this._blockSize);
    this.WriteTocEntries(allFiles, startBlockIndices, startOffsets);
    this.WriteBlockSizesTable(blockSizes, blockSizeWidth);
    foreach (var fileBlocks in compressedBlocks)
      foreach (var b in fileBlocks)
        this._stream.Write(b);
  }

  private byte[] BuildManifest() {
    var sb = new StringBuilder();
    for (var i = 0; i < this._entries.Count; ++i) {
      if (i > 0) sb.Append('\n');
      sb.Append(this._entries[i].Name);
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private void WriteHeader(int tocLength, int entryCount, int blockSize) {
    Span<byte> header = stackalloc byte[PsarcConstants.HeaderSize];
    Encoding.ASCII.GetBytes(PsarcConstants.MagicString).CopyTo(header[..4]);
    BinaryPrimitives.WriteUInt16BigEndian(header[4..6], 1);
    BinaryPrimitives.WriteUInt16BigEndian(header[6..8], 4);
    Encoding.ASCII.GetBytes(PsarcConstants.CompressionZlib).CopyTo(header[8..12]);
    BinaryPrimitives.WriteUInt32BigEndian(header[12..16], (uint)tocLength);
    BinaryPrimitives.WriteUInt32BigEndian(header[16..20], PsarcConstants.TocEntrySize);
    BinaryPrimitives.WriteUInt32BigEndian(header[20..24], (uint)entryCount);
    BinaryPrimitives.WriteUInt32BigEndian(header[24..28], (uint)blockSize);
    BinaryPrimitives.WriteUInt32BigEndian(header[28..32], PsarcConstants.FlagRelativePaths);
    this._stream.Write(header);
  }

  private void WriteTocEntries(List<(string Name, byte[] Data)> files, int[] startBlockIndices, long[] startOffsets) {
    Span<byte> entry = stackalloc byte[PsarcConstants.TocEntrySize];
    for (var i = 0; i < files.Count; ++i) {
      entry.Clear();
      var md5 = ComputePathMd5(files[i].Name);
      md5.CopyTo(entry[..16]);
      BinaryPrimitives.WriteUInt32BigEndian(entry[16..20], (uint)startBlockIndices[i]);
      WriteUInt40BigEndian(entry[20..25], files[i].Data.LongLength);
      WriteUInt40BigEndian(entry[25..30], startOffsets[i]);
      this._stream.Write(entry);
    }
  }

  private void WriteBlockSizesTable(List<uint> sizes, int width) {
    Span<byte> buf = stackalloc byte[4];
    foreach (var s in sizes) {
      buf.Clear();
      for (var k = 0; k < width; ++k)
        buf[k] = (byte)(s >> (8 * (width - 1 - k)));
      this._stream.Write(buf[..width]);
    }
  }

  private byte[] CompressBlock(byte[] data, int offset, int length) {
    using var ms = new MemoryStream();
    // SmallestSize gives best ratio; PSARC blocks are independent so per-block overhead is acceptable.
    using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
      z.Write(data, offset, length);
    return ms.ToArray();
  }

  private static byte[] ComputePathMd5(string name) {
    // Manifest entry (index 0) hashes the empty string per PSARC convention.
    return MD5.HashData(Encoding.UTF8.GetBytes(name));
  }

  internal static void WriteUInt40BigEndian(Span<byte> dest, long value) {
    if (value < 0 || value > 0xFF_FFFF_FFFFL)
      throw new ArgumentOutOfRangeException(nameof(value), "PSARC 40-bit field overflow.");
    for (var i = 0; i < 5; ++i)
      dest[i] = (byte)(value >> (8 * (4 - i)));
  }

  private static string NormalizePath(string name)
    => name.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._finished)
      this.Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
