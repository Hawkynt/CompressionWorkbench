using Compression.Core.BitIO;
using Compression.Core.Streams;

namespace FileFormat.Bzip2;

/// <summary>
/// Stream for reading and writing bzip2 format data.
/// </summary>
public sealed class Bzip2Stream : CompressionStream {
  private readonly int _blockSize100k;

  // Compression state
  private Bzip2Compressor? _compressor;
  private BitWriter<MsbBitOrder>? _bitWriter;
  private bool _headerWritten;

  // Decompression state
  private Bzip2Decompressor? _decompressor;
  private bool _headerRead;

  /// <summary>
  /// Initializes a new <see cref="Bzip2Stream"/>.
  /// </summary>
  /// <param name="stream">The underlying stream.</param>
  /// <param name="mode">Whether to compress or decompress.</param>
  /// <param name="blockSize100k">Block size multiplier (1-9). Default 9.</param>
  /// <param name="leaveOpen">Whether to leave the inner stream open.</param>
  public Bzip2Stream(Stream stream, CompressionStreamMode mode,
    int blockSize100k = 9, bool leaveOpen = false)
    : base(stream, mode, leaveOpen) {
    ArgumentOutOfRangeException.ThrowIfLessThan(blockSize100k, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSize100k, 9);
    this._blockSize100k = blockSize100k;
  }

  /// <inheritdoc />
  protected override int DecompressBlock(byte[] buffer, int offset, int count) {
    if (!this._headerRead) {
      ReadHeader();
      this._headerRead = true;
    }

    if (this._decompressor == null || this._decompressor.IsFinished)
      return 0;

    return this._decompressor.Read(buffer, offset, count);
  }

  /// <inheritdoc />
  protected override void CompressBlock(byte[] buffer, int offset, int count) {
    if (!this._headerWritten) {
      WriteHeader();
      this._headerWritten = true;
    }

    this._compressor!.Write(buffer.AsSpan(offset, count));
  }

  /// <inheritdoc />
  protected override void FinishCompression() {
    if (!this._headerWritten) {
      WriteHeader();
      this._headerWritten = true;
    }

    this._compressor!.Finish();
  }

  private void WriteHeader() {
    // "BZ" + 'h' + block size digit
    InnerStream.WriteByte((byte)'B');
    InnerStream.WriteByte((byte)'Z');
    InnerStream.WriteByte(Bzip2Constants.VersionByte);
    InnerStream.WriteByte((byte)('0' + this._blockSize100k));

    this._bitWriter = new BitWriter<MsbBitOrder>(InnerStream);
    this._compressor = new Bzip2Compressor(this._bitWriter, this._blockSize100k);
  }

  private void ReadHeader() {
    var b1 = InnerStream.ReadByte();
    var b2 = InnerStream.ReadByte();
    var version = InnerStream.ReadByte();
    var level = InnerStream.ReadByte();

    if (b1 < 0 || b2 < 0 || version < 0 || level < 0)
      throw new InvalidDataException("Truncated bzip2 header.");

    if (b1 != 'B' || b2 != 'Z')
      throw new InvalidDataException("Invalid bzip2 magic bytes.");

    if (version != Bzip2Constants.VersionByte)
      throw new InvalidDataException($"Unsupported bzip2 version: 0x{version:X2}");

    if (level < '1' || level > '9')
      throw new InvalidDataException($"Invalid bzip2 block size: {(char)level}");

    var bits = new BitBuffer<MsbBitOrder>(InnerStream);
    this._decompressor = new Bzip2Decompressor(bits);
  }
}
