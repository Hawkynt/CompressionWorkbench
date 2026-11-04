using Compression.Core.Streams;

namespace FileFormat.Crunch;

/// <summary>
/// Stream for reading and writing CP/M CRUNCH (.?Z?) files: reads CRUNCH 1.x and 2.x, writes 2.x.
/// </summary>
/// <remarks>
/// A file is the magic <c>76 FE</c>, the original name and four header bytes, an RLE-then-LZW
/// code stream and a 16-bit checksum; see <c>docs/CRUNCH-ON-DISK.md</c>.
/// </remarks>
public sealed class CrunchStream : CompressionStream {
  private byte[]? _decompressedData;
  private int _decompressPos;
  private bool _headerRead;
  private bool _finished;
  private MemoryStream? _compressBuffer;
  private readonly string? _originalName;

  /// <summary>
  /// Gets the original filename stored in the Crunch header (only set during decompression).
  /// </summary>
  public string? OriginalName { get; private set; }

  /// <summary>
  /// Initializes a new <see cref="CrunchStream"/>.
  /// </summary>
  /// <param name="stream">The underlying stream.</param>
  /// <param name="mode">Whether to compress or decompress.</param>
  /// <param name="originalName">Original filename to store in the header (compression only).</param>
  /// <param name="leaveOpen">Whether to leave the inner stream open.</param>
  public CrunchStream(Stream stream, CompressionStreamMode mode,
    string? originalName = null, bool leaveOpen = false)
    : base(stream, mode, leaveOpen) {
    _originalName = originalName;
    if (mode == CompressionStreamMode.Compress)
      _compressBuffer = new MemoryStream();
  }

  /// <inheritdoc />
  protected override int DecompressBlock(byte[] buffer, int offset, int count) {
    if (_finished) return 0;

    if (!_headerRead) {
      ReadAndDecompress();
      _headerRead = true;
    }

    if (_decompressedData == null || _decompressPos >= _decompressedData.Length) {
      _finished = true;
      return 0;
    }

    var available = _decompressedData.Length - _decompressPos;
    var toCopy = Math.Min(available, count);
    _decompressedData.AsSpan(_decompressPos, toCopy).CopyTo(buffer.AsSpan(offset));
    _decompressPos += toCopy;
    return toCopy;
  }

  /// <inheritdoc />
  protected override void CompressBlock(byte[] buffer, int offset, int count) {
    _compressBuffer!.Write(buffer, offset, count);
  }

  /// <inheritdoc />
  protected override void FinishCompression()
    => InnerStream.Write(CrunchCodec.Encode(_compressBuffer!.ToArray(), _originalName));

  private void ReadAndDecompress() {
    using var file = new MemoryStream();
    InnerStream.CopyTo(file);
    if (file.Length < 2)
      throw new InvalidDataException("Truncated Crunch header.");
    (OriginalName, _decompressedData) = CrunchCodec.Decode(file.GetBuffer().AsSpan(0, (int)file.Length));
    _decompressPos = 0;
  }
}
