using Compression.Core.Dictionary.Lzw;
using Compression.Core.Streams;

namespace FileFormat.Compress;

/// <summary>
/// Stream for reading and writing Unix compress (.Z) format data.
/// Uses LZC/LZW compression with variable-width codes (9-16 bits) and the format's
/// required eight-code packing/alignment rules.
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
  /// <param name="blockMode">Whether to reserve the block CLEAR code. Defaults to true.</param>
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
  /// <summary>
  /// Performs the decompress block operation.
  /// </summary>
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
  /// <summary>
  /// Performs the compress block operation.
  /// </summary>
  protected override void CompressBlock(byte[] buffer, int offset, int count) {
    this._compressBuffer!.Write(buffer, offset, count);
  }

  /// <inheritdoc />
  /// <summary>Writes the compress header and the LZW-coded payload for the buffered input.</summary>
  protected override void FinishCompression() {
    var compressed = LzcCodec.Compress(this._compressBuffer!.ToArray(), this._maxBits, this._blockMode);
    InnerStream.Write(compressed);
  }

  private void ReadAndDecompress() {
    using var input = new MemoryStream();
    InnerStream.CopyTo(input);
    this._decompressedData = LzcCodec.Decompress(input.ToArray());
    this._decompressPos = 0;
  }
}
