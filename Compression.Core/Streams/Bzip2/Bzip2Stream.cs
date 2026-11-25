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

  private bool _nsisVariant;

  /// <summary>
  /// Opens a decoder for the header-less bzip2 dialect embedded in NSIS installers: no "BZh"
  /// stream header, a single 0x31 byte before each block, 0x17 at the end, no CRCs.
  /// </summary>
  /// <param name="stream">The stream positioned at the first block marker.</param>
  /// <param name="leaveOpen">Whether to leave the inner stream open.</param>
  /// <returns>A read-only decompressing stream.</returns>
  public static Bzip2Stream OpenNsisDecoder(Stream stream, bool leaveOpen = false)
    => new(stream, CompressionStreamMode.Decompress, 9, leaveOpen) { _nsisVariant = true };

  /// <summary>
  /// Opens an encoder for the NSIS dialect <see cref="OpenNsisDecoder"/> reads: no stream header,
  /// 0x31 before each 900k block, 0x17 after the last, no CRCs and no randomised blocks.
  /// </summary>
  /// <param name="stream">The stream to write to.</param>
  /// <param name="leaveOpen">Whether to leave the inner stream open.</param>
  /// <returns>A write-only compressing stream.</returns>
  public static Bzip2Stream OpenNsisEncoder(Stream stream, bool leaveOpen = false)
    => new(stream, CompressionStreamMode.Compress, 9, leaveOpen) { _nsisVariant = true };

  /// <inheritdoc />
  protected override int DecompressBlock(byte[] buffer, int offset, int count) {
    if (!this._headerRead && this._nsisVariant) {
      this._decompressor = new Bzip2Decompressor(new BitBuffer<MsbBitOrder>(InnerStream), nsisVariant: true);
      this._headerRead = true;
    }

    if (!this._headerRead) {
      this.TryReadHeader(required: true);
      this._headerRead = true;
    }

    while (this._decompressor != null) {
      var read = this._decompressor.IsFinished ? 0 : this._decompressor.Read(buffer, offset, count);
      // A .bz2 file may hold several streams back to back (`cat a.bz2 b.bz2`, pbzip2, lbzip2);
      // bzip2 -d decodes them all, and ignores anything after the last that is not a stream.
      if (read > 0 || this._nsisVariant || !this.TryReadHeader(required: false))
        return read;
    }

    return 0;
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
    if (this._nsisVariant) {
      this._bitWriter = new BitWriter<MsbBitOrder>(InnerStream);
      this._compressor = new Bzip2Compressor(this._bitWriter, this._blockSize100k, nsisVariant: true);
      return;
    }

    // "BZ" + 'h' + block size digit
    InnerStream.WriteByte((byte)'B');
    InnerStream.WriteByte((byte)'Z');
    InnerStream.WriteByte(Bzip2Constants.VersionByte);
    InnerStream.WriteByte((byte)('0' + this._blockSize100k));

    this._bitWriter = new BitWriter<MsbBitOrder>(InnerStream);
    this._compressor = new Bzip2Compressor(this._bitWriter, this._blockSize100k);
  }

  /// <summary>
  /// Reads a "BZh1".."BZh9" stream header and starts decoding that stream.
  /// </summary>
  /// <param name="required">
  /// True for the first stream, whose header must be valid. False for a following stream: anything
  /// that is not a header then ends the file (bzip2 warns about such trailing garbage and ignores it).
  /// </param>
  /// <returns>True when a stream was started; false (only when not required) when none follows.</returns>
  private bool TryReadHeader(bool required) {
    var start = !required && InnerStream.CanSeek ? InnerStream.Position : -1;
    var b1 = InnerStream.ReadByte();
    var b2 = InnerStream.ReadByte();
    var version = InnerStream.ReadByte();
    var level = InnerStream.ReadByte();

    if (!required) {
      if (b1 != 'B' || b2 != 'Z' || version != Bzip2Constants.VersionByte || level < '1' || level > '9') {
        this._decompressor = null;
        // Leave whatever follows (a container's next record, say) where it was.
        if (start >= 0)
          InnerStream.Position = start;
        return false;
      }
      this._decompressor = new Bzip2Decompressor(new BitBuffer<MsbBitOrder>(InnerStream));
      return true;
    }

    if (b1 < 0 || b2 < 0 || version < 0 || level < 0)
      throw new InvalidDataException("Truncated bzip2 header.");

    if (b1 != 'B' || b2 != 'Z')
      throw new InvalidDataException("Invalid bzip2 magic bytes.");

    if (version != Bzip2Constants.VersionByte)
      throw new InvalidDataException($"Unsupported bzip2 version: 0x{version:X2}");

    if (level < '1' || level > '9')
      throw new InvalidDataException($"Invalid bzip2 block size: {(char)level}");

    this._decompressor = new Bzip2Decompressor(new BitBuffer<MsbBitOrder>(InnerStream));
    return true;
  }
}
