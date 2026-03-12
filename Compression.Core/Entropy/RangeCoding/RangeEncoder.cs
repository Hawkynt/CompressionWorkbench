namespace Compression.Core.Entropy.RangeCoding;

/// <summary>
/// LZMA-style byte-aligned range encoder with adaptive binary probabilities.
/// </summary>
public sealed class RangeEncoder {
  private const int NumBitModelTotalBits = 11;
  private const int BitModelTotal = 1 << RangeEncoder.NumBitModelTotalBits;
  private const int NumMoveBits = 5;
  private const uint TopValue = 1u << 24;

  private readonly Stream _output;
  private ulong _low;
  private uint _range;
  private int _cacheSize;
  private byte _cache;

  /// <summary>
  /// Initializes a new <see cref="RangeEncoder"/> writing to the specified stream.
  /// </summary>
  /// <param name="output">The output stream.</param>
  public RangeEncoder(Stream output) {
    this._output = output ?? throw new ArgumentNullException(nameof(output));
    this._range = 0xFFFFFFFF;
    this._cacheSize = 1;
  }

  /// <summary>
  /// Gets the number of bytes written to the output stream.
  /// </summary>
  public long BytesWritten { get; private set; }

  /// <summary>
  /// Probability midpoint for initializing probability variables (1024 out of 2048).
  /// </summary>
  public const int ProbInitValue = RangeEncoder.BitModelTotal / 2;

  /// <summary>
  /// Encodes a single bit using an adaptive probability model.
  /// </summary>
  /// <param name="prob">The probability variable (11-bit, 0..2047). Updated after encoding.</param>
  /// <param name="bit">The bit to encode (0 or 1).</param>
  public void EncodeBit(ref int prob, int bit) {
    var bound = (this._range >> RangeEncoder.NumBitModelTotalBits) * (uint)prob;

    if (bit == 0) {
      this._range = bound;
      prob += (RangeEncoder.BitModelTotal - prob) >> RangeEncoder.NumMoveBits;
    }
    else {
      this._low += bound;
      this._range -= bound;
      prob -= prob >> RangeEncoder.NumMoveBits;
    }

    this.Normalize();
  }

  /// <summary>
  /// Encodes bits without probability adaptation (fixed 50/50 split).
  /// Used for alignment bits and direct-coded distance extra bits.
  /// </summary>
  /// <param name="value">The value to encode.</param>
  /// <param name="count">The number of bits to encode (MSB first).</param>
  public void EncodeDirectBits(int value, int count) {
    for (var i = count - 1; i >= 0; --i) {
      this._range >>= 1;
      if (((value >> i) & 1) == 1)
        this._low += this._range;

      this.Normalize();
    }
  }

  /// <summary>
  /// Finishes encoding by flushing remaining state to the output.
  /// </summary>
  public void Finish() {
    for (var i = 0; i < 5; ++i)
      this.ShiftLow();
  }

  private void Normalize() {
    if (this._range >= RangeEncoder.TopValue)
      return;

    this._range <<= 8;
    this.ShiftLow();
  }

  private void ShiftLow() {
    if ((uint)this._low < 0xFF000000u || (this._low >> 32) != 0) {
      var temp = this._cache;
      do {
        this._output.WriteByte((byte)(temp + (byte)(this._low >> 32)));
        ++this.BytesWritten;
        temp = 0xFF;
      }
      while (--this._cacheSize > 0);

      this._cache = (byte)((uint)this._low >> 24);
    }

    ++this._cacheSize;
    this._low = (uint)(this._low << 8);
  }
}
