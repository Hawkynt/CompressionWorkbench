namespace Compression.Core.Entropy.Arithmetic;

/// <summary>
/// Binary arithmetic decoder that decodes symbols bit-by-bit using
/// adaptive probability estimation.
/// </summary>
/// <remarks>
/// Counterpart to <see cref="ArithmeticEncoder"/>. Reads bits from
/// a compressed stream and reconstructs symbols given the same
/// probability model used during encoding.
/// </remarks>
public sealed class ArithmeticDecoder {
  private const int PrecisionBits = 30;
  private const uint FullRange = 1u << ArithmeticDecoder.PrecisionBits;
  private const uint HalfRange = 1u << (ArithmeticDecoder.PrecisionBits - 1);
  private const uint QuarterRange = 1u << (ArithmeticDecoder.PrecisionBits - 2);

  private readonly Stream _input;
  private uint _low;
  private uint _high;
  private uint _code;

  private int _bitBuffer;
  private int _bitsRemaining;

  /// <summary>
  /// Initializes a new <see cref="ArithmeticDecoder"/> reading from the given stream.
  /// </summary>
  /// <param name="input">The input stream of compressed data.</param>
  public ArithmeticDecoder(Stream input) {
    this._input = input;
    this._low = 0;
    this._high = ArithmeticDecoder.FullRange - 1;

    // Read initial bits into code
    this._code = 0;
    for (var i = 0; i < ArithmeticDecoder.PrecisionBits; ++i)
      this._code = (this._code << 1) | this.ReadBit();
  }

  /// <summary>
  /// Decodes a single bit given the probability of the bit being 0.
  /// </summary>
  /// <param name="prob0">Probability of 0, scaled to [1, 65535] out of 65536.</param>
  /// <returns>The decoded bit (0 or 1).</returns>
  public int DecodeBit(int prob0) {
    var range = this._high - this._low + 1;
    var mid = this._low + (uint)((range * (ulong)prob0) >> 16) - 1;

    int bit;
    if (this._code <= mid) {
      bit = 0;
      this._high = mid;
    } else {
      bit = 1;
      this._low = mid + 1;
    }

    this.Normalize();
    return bit;
  }

  /// <summary>
  /// Gets the current cumulative count for symbol decoding.
  /// </summary>
  /// <param name="totalFreq">Total frequency of all symbols.</param>
  /// <returns>The cumulative count value for symbol lookup.</returns>
  public uint GetCumulativeCount(uint totalFreq) {
    var range = this._high - this._low + 1;
    return (uint)(((ulong)(this._code - this._low + 1) * totalFreq - 1) / range);
  }

  /// <summary>
  /// Updates the decoder state after looking up a symbol.
  /// </summary>
  /// <param name="cumFreq">Cumulative frequency of the decoded symbol (lower bound).</param>
  /// <param name="symFreq">Frequency of the decoded symbol.</param>
  /// <param name="totalFreq">Total frequency of all symbols.</param>
  public void UpdateSymbol(uint cumFreq, uint symFreq, uint totalFreq) {
    var range = this._high - this._low + 1;
    this._high = this._low + (uint)((range * (ulong)(cumFreq + symFreq)) / totalFreq) - 1;
    this._low = this._low + (uint)((range * (ulong)cumFreq) / totalFreq);

    this.Normalize();
  }

  private void Normalize() {
    while (true) {
      if (this._high < ArithmeticDecoder.HalfRange) {
        // Both in lower half — just shift
      } else if (this._low >= ArithmeticDecoder.HalfRange) {
        // Both in upper half
        this._low -= ArithmeticDecoder.HalfRange;
        this._high -= ArithmeticDecoder.HalfRange;
        this._code -= ArithmeticDecoder.HalfRange;
      } else if (this._low >= ArithmeticDecoder.QuarterRange && this._high < 3 * ArithmeticDecoder.QuarterRange) {
        // Underflow
        this._low -= ArithmeticDecoder.QuarterRange;
        this._high -= ArithmeticDecoder.QuarterRange;
        this._code -= ArithmeticDecoder.QuarterRange;
      } else
        break;

      this._low <<= 1;
      this._high = (this._high << 1) | 1;
      this._code = (this._code << 1) | this.ReadBit();
    }
  }

  private uint ReadBit() {
    if (this._bitsRemaining == 0) {
      var b = this._input.ReadByte();
      this._bitBuffer = b >= 0 ? b : 0;
      this._bitsRemaining = 8;
    }

    --this._bitsRemaining;
    return (uint)((this._bitBuffer >> this._bitsRemaining) & 1);
  }
}
