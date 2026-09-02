using Compression.Core.BitIO;
using Compression.Core.Dictionary.Lzw;
using Compression.Core.Streams;

namespace FileFormat.Crunch;

/// <summary>
/// Stream for reading and writing CP/M Crunch (.?Z?) format data.
/// Uses LZW compression with variable-width codes (9-12 bits) in MSB-first order.
/// </summary>
/// <remarks>
/// The Crunch format header is: magic (0x76 0xFE), followed by a null-terminated
/// original filename. The LZW bitstream follows with clear codes and stop codes enabled.
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
  protected override void FinishCompression() {
    var data = _compressBuffer!.ToArray();

    // Write magic
    InnerStream.WriteByte(CrunchConstants.Magic1);
    InnerStream.WriteByte(CrunchConstants.Magic2);

    // Write original filename (null-terminated)
    var name = _originalName ?? "";
    foreach (var c in name)
      InnerStream.WriteByte((byte)c);
    InnerStream.WriteByte(0); // null terminator

    // LZW compress: 9-12 bits, MSB-first, clear+stop codes
    var encoder = new LzwEncoder(
      InnerStream,
      minBits: CrunchConstants.MinBits,
      maxBits: CrunchConstants.MaxBits,
      useClearCode: true,
      useStopCode: true,
      bitOrder: BitOrder.MsbFirst);
    encoder.Encode(data);
  }

  private void ReadAndDecompress() {
    var b1 = InnerStream.ReadByte();
    var b2 = InnerStream.ReadByte();

    if (b1 < 0 || b2 < 0)
      throw new InvalidDataException("Truncated Crunch header.");

    if (b1 != CrunchConstants.Magic1 || b2 != CrunchConstants.Magic2)
      throw new InvalidDataException("Invalid Crunch magic bytes.");

    // Read null-terminated original filename
    var nameBytes = new List<byte>();
    int b;
    while ((b = InnerStream.ReadByte()) > 0)
      nameBytes.Add((byte)b);

    OriginalName = nameBytes.Count > 0
      ? System.Text.Encoding.ASCII.GetString([.. nameBytes])
      : null;

    // LZW decompress: 9-12 bits, MSB-first, clear+stop codes
    var decoder = new LzwDecoder(
      InnerStream,
      minBits: CrunchConstants.MinBits,
      maxBits: CrunchConstants.MaxBits,
      useClearCode: true,
      useStopCode: true,
      bitOrder: BitOrder.MsbFirst);

    _decompressedData = decoder.Decode();
    _decompressPos = 0;
  }
}
