using Compression.Core.BitIO;
using Compression.Core.Dictionary.Lzw;
using Compression.Core.Streams;

namespace FileFormat.Compress;

/// <summary>
/// Stream for reading and writing Unix compress (.Z) format data.
/// Uses LZW compression with variable-width codes (9-16 bits) in LSB-first order.
/// </summary>
public sealed class CompressStream : CompressionStream {
  private readonly int _maxBits;
  private readonly bool _blockMode;

  // Decompression state
  private byte[]? _decompressedData;
  private int _decompressPos;
  private bool _headerRead;
  private bool _finished;

  // Compression state
  private MemoryStream? _compressBuffer;

  /// <summary>
  /// Initializes a new <see cref="CompressStream"/>.
  /// </summary>
  /// <param name="stream">The underlying stream.</param>
  /// <param name="mode">Whether to compress or decompress.</param>
  /// <param name="maxBits">Maximum LZW code width (9-16). Defaults to 16.</param>
  /// <param name="blockMode">Whether to use block mode (clear codes). Defaults to true.</param>
  /// <param name="leaveOpen">Whether to leave the inner stream open.</param>
  public CompressStream(Stream stream, CompressionStreamMode mode,
    int maxBits = CompressConstants.DefaultMaxBits,
    bool blockMode = true, bool leaveOpen = false)
    : base(stream, mode, leaveOpen) {
    ArgumentOutOfRangeException.ThrowIfLessThan(maxBits, CompressConstants.MinBits);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBits, CompressConstants.DefaultMaxBits);

    this._maxBits = maxBits;
    this._blockMode = blockMode;

    if (mode == CompressionStreamMode.Compress)
      this._compressBuffer = new MemoryStream();
  }

  /// <inheritdoc />
  protected override int DecompressBlock(byte[] buffer, int offset, int count) {
    if (this._finished)
      return 0;

    if (!this._headerRead) {
      this.ReadAndDecompress();
      this._headerRead = true;
    }

    if (this._decompressedData == null || this._decompressPos >= this._decompressedData.Length) {
      this._finished = true;
      return 0;
    }

    var available = this._decompressedData.Length - this._decompressPos;
    var toCopy = Math.Min(available, count);
    this._decompressedData.AsSpan(this._decompressPos, toCopy).CopyTo(buffer.AsSpan(offset));
    this._decompressPos += toCopy;
    return toCopy;
  }

  /// <inheritdoc />
  protected override void CompressBlock(byte[] buffer, int offset, int count) {
    this._compressBuffer!.Write(buffer, offset, count);
  }

  /// <inheritdoc />
  protected override void FinishCompression() {
    var data = this._compressBuffer!.ToArray();

    // Write header
    InnerStream.WriteByte(CompressConstants.Magic1);
    InnerStream.WriteByte(CompressConstants.Magic2);

    var flags = (byte)(this._maxBits & CompressConstants.MaxBitsMask);
    if (this._blockMode)
      flags |= CompressConstants.BlockModeFlag;
    InnerStream.WriteByte(flags);

    // LZW compress
    // Unix compress uses: minBits=9, clear code at 256 (when blockMode), no stop code, LSB bit order
    var encoder = new LzwEncoder(
      InnerStream,
      minBits: CompressConstants.MinBits,
      maxBits: this._maxBits,
      useClearCode: this._blockMode,
      useStopCode: false,
      bitOrder: BitOrder.LsbFirst);
    encoder.Encode(data);
  }

  private void ReadAndDecompress() {
    // Read header
    var b1 = InnerStream.ReadByte();
    var b2 = InnerStream.ReadByte();
    var flags = InnerStream.ReadByte();

    if (b1 < 0 || b2 < 0 || flags < 0)
      throw new InvalidDataException("Truncated compress header.");

    if (b1 != CompressConstants.Magic1 || b2 != CompressConstants.Magic2)
      throw new InvalidDataException("Invalid compress magic bytes.");

    var maxBits = flags & CompressConstants.MaxBitsMask;
    var blockMode = (flags & CompressConstants.BlockModeFlag) != 0;

    if (maxBits < CompressConstants.MinBits || maxBits > CompressConstants.DefaultMaxBits)
      throw new InvalidDataException($"Invalid compress max bits: {maxBits}");

    // LZW decompress
    var decoder = new LzwDecoder(
      InnerStream,
      minBits: CompressConstants.MinBits,
      maxBits: maxBits,
      useClearCode: blockMode,
      useStopCode: false,
      bitOrder: BitOrder.LsbFirst);

    this._decompressedData = decoder.Decode();
    this._decompressPos = 0;
  }
}
