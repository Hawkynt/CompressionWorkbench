using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileFormat.Psarc;

/// <summary>
/// Reads entries from a Sony PlayStation archive (PSARC) v1.3/1.4. Supports zlib block compression.
/// </summary>
public sealed class PsarcReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly int _blockSize;
  private readonly int _blockSizeWidth;
  private readonly string _compression;
  private readonly uint[] _blockSizes;
  private bool _disposed;

  /// <summary>Gets all entries in the archive (entry 0 is the path manifest itself, omitted from this list).</summary>
  public IReadOnlyList<PsarcEntry> Entries { get; }

  /// <summary>Gets the block size used by the archive.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>Gets the compression algorithm name ("zlib" or "lzma") declared in the header.</summary>
  public string Compression => this._compression;

  /// <summary>
  /// Initializes a new <see cref="PsarcReader"/> from a stream.
  /// </summary>
  /// <param name="stream">A seekable stream containing the PSARC archive.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open on dispose.</param>
  public PsarcReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (!stream.CanSeek)
      throw new ArgumentException("PSARC requires a seekable stream.", nameof(stream));
    if (stream.Length < PsarcConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to contain a PSARC header.");

    stream.Position = 0;
    Span<byte> header = stackalloc byte[PsarcConstants.HeaderSize];
    ReadExact(header);

    if (Encoding.ASCII.GetString(header[..4]) != PsarcConstants.MagicString)
      throw new InvalidDataException("Invalid PSARC magic.");

    var major = BinaryPrimitives.ReadUInt16BigEndian(header[4..6]);
    var minor = BinaryPrimitives.ReadUInt16BigEndian(header[6..8]);
    if (major != PsarcConstants.SupportedMajor ||
        minor < PsarcConstants.SupportedMinorMin || minor > PsarcConstants.SupportedMinorMax)
      throw new InvalidDataException($"Unsupported PSARC version {major}.{minor}.");

    this._compression = Encoding.ASCII.GetString(header[8..12]);
    var tocLength    = BinaryPrimitives.ReadUInt32BigEndian(header[12..16]);
    var tocEntrySize = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
    var tocEntryCount = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
    this._blockSize  = (int)BinaryPrimitives.ReadUInt32BigEndian(header[24..28]);
    var archiveFlags = BinaryPrimitives.ReadUInt32BigEndian(header[28..32]);

    if (tocEntrySize != PsarcConstants.TocEntrySize)
      throw new InvalidDataException($"Unexpected PSARC TOC entry size {tocEntrySize}, expected {PsarcConstants.TocEntrySize}.");
    if (tocEntryCount == 0)
      throw new InvalidDataException("PSARC has no TOC entries (manifest required).");
    if (this._blockSize <= 0)
      throw new InvalidDataException($"Invalid PSARC block size {this._blockSize}.");
    if ((archiveFlags & PsarcConstants.FlagEncryptedToc) != 0)
      throw new NotSupportedException("PSARC archives with encrypted TOC are not supported.");

    this._blockSizeWidth = ComputeBlockSizeWidth(this._blockSize);

    var tocBytes = new byte[(int)tocLength - PsarcConstants.HeaderSize];
    ReadExact(tocBytes);

    var rawEntries = ParseTocEntries(tocBytes.AsSpan(0, (int)(tocEntrySize * tocEntryCount)), (int)tocEntryCount);
    var blockSizesBytes = tocBytes.AsSpan((int)(tocEntrySize * tocEntryCount));
    if (blockSizesBytes.Length % this._blockSizeWidth != 0)
      throw new InvalidDataException("PSARC block-sizes table length is not a multiple of the block-size-entry width.");

    this._blockSizes = ParseBlockSizes(blockSizesBytes, this._blockSizeWidth);

    var manifestRaw = this.ExtractInternal(rawEntries[0], "");
    var paths = ParseManifest(manifestRaw);
    if (paths.Count != rawEntries.Count - 1)
      throw new InvalidDataException($"PSARC manifest path count ({paths.Count}) does not match TOC entry count - 1 ({rawEntries.Count - 1}).");

    var entries = new List<PsarcEntry>(rawEntries.Count - 1);
    for (var i = 1; i < rawEntries.Count; ++i) {
      var raw = rawEntries[i];
      entries.Add(new PsarcEntry {
        Name = paths[i - 1],
        OriginalSize = raw.OriginalSize,
        CompressedSize = ComputeCompressedSize(raw),
        StartBlockIndex = raw.StartBlockIndex,
        StartOffset = raw.StartOffset
      });
    }
    this.Entries = entries;
  }

  /// <summary>
  /// Extracts and decompresses the contents of the given entry.
  /// </summary>
  /// <param name="entry">An entry obtained from <see cref="Entries"/>.</param>
  /// <returns>The original (uncompressed) file content.</returns>
  public byte[] Extract(PsarcEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var raw = new RawTocEntry(entry.StartBlockIndex, entry.OriginalSize, entry.StartOffset);
    return this.ExtractInternal(raw, entry.Name);
  }

  private byte[] ExtractInternal(RawTocEntry raw, string nameForErrors) {
    if (raw.OriginalSize == 0)
      return [];
    if (this._compression != PsarcConstants.CompressionZlib)
      throw new NotSupportedException($"PSARC compression '{this._compression}' is not supported (zlib only).");

    var blockCount = (int)((raw.OriginalSize + this._blockSize - 1) / this._blockSize);
    var output = new byte[raw.OriginalSize];
    var written = 0;
    var fileOffset = raw.StartOffset;

    for (var b = 0; b < blockCount; ++b) {
      var compressedSize = (int)this._blockSizes[raw.StartBlockIndex + b];
      var remaining = (int)raw.OriginalSize - written;
      var expected = Math.Min(this._blockSize, remaining);

      this._stream.Position = fileOffset;

      // PSARC stored-block sentinels: 0 means "raw, full block_size on disk"; compressedSize == block_size
      // is the alternate convention used by some tools. Both decode to a raw block_size payload.
      var stored = compressedSize == 0 || compressedSize == this._blockSize;
      var actualCompressed = stored ? this._blockSize : compressedSize;
      var blockBytes = new byte[actualCompressed];
      ReadExact(blockBytes);
      fileOffset += actualCompressed;

      if (stored) {
        if (actualCompressed < expected)
          throw new InvalidDataException($"PSARC stored block for entry '{nameForErrors}' is smaller than expected payload.");
        Buffer.BlockCopy(blockBytes, 0, output, written, expected);
        written += expected;
        continue;
      }

      // The encoder may emit a raw last block whose size equals "expected" (< block_size) and whose first byte
      // is not a zlib header — community PSARC tools accept this. Detect by zlib magic.
      var looksLikeZlib = blockBytes.Length >= 2 && IsZlibHeader(blockBytes[0], blockBytes[1]);
      if (!looksLikeZlib) {
        if (blockBytes.Length != expected)
          throw new InvalidDataException($"PSARC raw block for entry '{nameForErrors}' has size {blockBytes.Length} but expected {expected}.");
        Buffer.BlockCopy(blockBytes, 0, output, written, expected);
        written += expected;
        continue;
      }

      using var src = new MemoryStream(blockBytes, writable: false);
      using var z = new ZLibStream(src, CompressionMode.Decompress);
      var blockWritten = 0;
      while (blockWritten < expected) {
        var read = z.Read(output, written + blockWritten, expected - blockWritten);
        if (read == 0)
          throw new InvalidDataException($"PSARC zlib block for entry '{nameForErrors}' ended before producing {expected} bytes.");
        blockWritten += read;
      }
      written += blockWritten;
    }

    if (written != raw.OriginalSize)
      throw new InvalidDataException($"PSARC entry '{nameForErrors}' produced {written} bytes, expected {raw.OriginalSize}.");

    return output;
  }

  private long ComputeCompressedSize(RawTocEntry raw) {
    var blockCount = (int)((raw.OriginalSize + this._blockSize - 1) / this._blockSize);
    long total = 0;
    for (var b = 0; b < blockCount; ++b) {
      var size = this._blockSizes[raw.StartBlockIndex + b];
      total += size == 0 ? this._blockSize : size;  // size == block_size case naturally falls through
    }
    return total;
  }

  private static List<RawTocEntry> ParseTocEntries(ReadOnlySpan<byte> data, int count) {
    var list = new List<RawTocEntry>(count);
    for (var i = 0; i < count; ++i) {
      var offset = i * PsarcConstants.TocEntrySize;
      // bytes [0..16) are the path MD5; we discard it because the manifest authoritatively lists paths in order.
      var startBlockIndex = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 16, 4));
      var originalSize = ReadUInt40BigEndian(data.Slice(offset + 20, 5));
      var startOffset = ReadUInt40BigEndian(data.Slice(offset + 25, 5));
      list.Add(new RawTocEntry(startBlockIndex, originalSize, startOffset));
    }
    return list;
  }

  private static uint[] ParseBlockSizes(ReadOnlySpan<byte> data, int width) {
    var n = data.Length / width;
    var result = new uint[n];
    for (var i = 0; i < n; ++i) {
      var s = data.Slice(i * width, width);
      uint v = 0;
      for (var k = 0; k < width; ++k)
        v = (v << 8) | s[k];
      result[i] = v;
    }
    return result;
  }

  private static List<string> ParseManifest(byte[] data) {
    var text = Encoding.UTF8.GetString(data);
    return new List<string>(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
  }

  internal static int ComputeBlockSizeWidth(int blockSize) {
    if (blockSize <= 0x10000) return 2;
    if (blockSize <= 0x1000000) return 3;
    return 4;
  }

  internal static long ReadUInt40BigEndian(ReadOnlySpan<byte> data) {
    long v = 0;
    for (var i = 0; i < 5; ++i)
      v = (v << 8) | data[i];
    return v;
  }

  // zlib RFC 1950: first byte is CMF where low nibble (CM) must be 8 (deflate); valid CMF/FLG combos satisfy
  // (CMF*256+FLG) % 31 == 0. PSARC stored blocks won't satisfy this for arbitrary content.
  private static bool IsZlibHeader(byte b0, byte b1)
    => (b0 & 0x0F) == 0x08 && ((b0 * 256 + b1) % 31) == 0;

  private void ReadExact(Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var n = this._stream.Read(buffer[total..]);
      if (n == 0)
        throw new EndOfStreamException("Unexpected end of PSARC stream.");
      total += n;
    }
  }

  private void ReadExact(byte[] buffer) => ReadExact(buffer.AsSpan());

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  private readonly record struct RawTocEntry(int StartBlockIndex, long OriginalSize, long StartOffset);
}
