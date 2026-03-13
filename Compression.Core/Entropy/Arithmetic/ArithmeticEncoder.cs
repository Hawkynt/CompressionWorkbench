namespace Compression.Core.Entropy.Arithmetic;

/// <summary>
/// Binary arithmetic encoder that encodes symbols bit-by-bit using
/// adaptive probability estimation.
/// </summary>
/// <remarks>
/// This is a bit-level arithmetic coder (as opposed to the byte-aligned
/// range coder in <see cref="RangeCoding.RangeEncoder"/>). It uses 32-bit
/// precision with a safe range to avoid overflow.
/// </remarks>
public sealed class ArithmeticEncoder {
  // Use 30-bit precision to avoid overflow: range fits in uint32
  private const int PrecisionBits = 30;
  private const uint FullRange = 1u << ArithmeticEncoder.PrecisionBits;       // 2^30
  private const uint HalfRange = 1u << (ArithmeticEncoder.PrecisionBits - 1); // 2^29
  private const uint QuarterRange = 1u << (ArithmeticEncoder.PrecisionBits - 2); // 2^28

  private readonly Stream _output;
  private uint _low;
  private uint _high;
  private int _pendingBits;
  private int _bitBuffer;
  private int _bitsInBuffer;

  /// <summary>
  /// Initializes a new <see cref="ArithmeticEncoder"/> writing to the given stream.
  /// </summary>
  /// <param name="output">The output stream for compressed data.</param>
  public ArithmeticEncoder(Stream output) {
    this._output = output;
    this._low = 0;
    this._high = ArithmeticEncoder.FullRange - 1; // 2^30 - 1
    this._pendingBits = 0;
  }

  /// <summary>
  /// Encodes a single bit with the given probability of the bit being 0.
  /// </summary>
  /// <param name="bit">The bit to encode (0 or 1).</param>
  /// <param name="prob0">Probability of 0, scaled to [1, 65535] out of 65536.</param>
  public void EncodeBit(int bit, int prob0) {
    var range = this._high - this._low + 1;
    var mid = this._low + (uint)((range * (ulong)prob0) >> 16) - 1;

    if (bit == 0)
      this._high = mid;
    else
      this._low = mid + 1;

    this.Normalize();
  }

  /// <summary>
  /// Encodes a symbol using a cumulative frequency table.
  /// </summary>
  /// <param name="cumFreq">Cumulative frequency of the symbol (lower bound).</param>
  /// <param name="symFreq">Frequency of the symbol.</param>
  /// <param name="totalFreq">Total frequency of all symbols.</param>
  public void EncodeSymbol(uint cumFreq, uint symFreq, uint totalFreq) {
    var range = this._high - this._low + 1;
    this._high = this._low + (uint)((range * (ulong)(cumFreq + symFreq)) / totalFreq) - 1;
    this._low += (uint)((range * (ulong)cumFreq) / totalFreq);

    this.Normalize();
  }

  /// <summary>
  /// Finalises the encoding and flushes remaining bits.
  /// </summary>
  public void Finish() {
    ++this._pendingBits;
    this.WriteBitAndPending(this._low >= ArithmeticEncoder.QuarterRange ? 1 : 0);

    // Flush partial byte
    if (this._bitsInBuffer > 0) {
      this._bitBuffer <<= (8 - this._bitsInBuffer);
      this._output.WriteByte((byte)this._bitBuffer);
    }

    this._output.Flush();
  }

  private void Normalize() {
    while (true) {
      if (this._high < ArithmeticEncoder.HalfRange)
        // Both in lower half
        this.WriteBitAndPending(0);
      else if (this._low >= ArithmeticEncoder.HalfRange) {
        // Both in upper half
        this.WriteBitAndPending(1);
        this._low -= ArithmeticEncoder.HalfRange;
        this._high -= ArithmeticEncoder.HalfRange;
      } else if (this._low >= ArithmeticEncoder.QuarterRange && this._high < 3 * ArithmeticEncoder.QuarterRange) {
        // Underflow
        ++this._pendingBits;
        this._low -= ArithmeticEncoder.QuarterRange;
        this._high -= ArithmeticEncoder.QuarterRange;
      } else
        break;

      this._low <<= 1;
      this._high = (this._high << 1) | 1;
    }
  }

  private void WriteBitAndPending(int bit) {
    this.WriteBit(bit);
    var opposite = bit ^ 1;
    while (this._pendingBits > 0) {
      this.WriteBit(opposite);
      --this._pendingBits;
    }
  }

  private void WriteBit(int bit) {
    this._bitBuffer = (this._bitBuffer << 1) | bit;
    ++this._bitsInBuffer;
    if (this._bitsInBuffer != 8)
      return;

    this._output.WriteByte((byte)this._bitBuffer);
    this._bitBuffer = 0;
    this._bitsInBuffer = 0;
  }
}
