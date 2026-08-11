namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Bit-oriented 32-bit arithmetic encoder used by the Quantum compressor.
/// </summary>
/// <remarks>
/// <para>
/// The interval [low, high] is held in 32-bit registers. After each symbol the
/// interval is renormalised: while it lies entirely in the lower or upper half a
/// bit is emitted and the interval is doubled, and while it straddles the midpoint
/// but sits inside the middle half a pending "follow" bit is counted instead. That
/// follow-bit handling is what keeps the interval from collapsing when low and high
/// converge on the midpoint from opposite sides — the classic underflow case.
/// </para>
/// <para>
/// Emitted bits are packed most-significant-bit first; the final byte is padded
/// with zero bits. Mirrors <see cref="QuantumRangeDecoder"/>.
/// </para>
/// </remarks>
internal sealed class QuantumRangeEncoder {
  private const uint Top = 0xFFFFFFFFu;
  private const uint Half = 0x80000000u;
  private const uint Quarter = 0x40000000u;
  private const uint ThreeQuarters = 0xC0000000u;

  private readonly Stream _output;
  private uint _low;
  private uint _high = Top;
  private long _followBits;
  private int _bitBuffer;
  private int _bitCount;

  /// <summary>Initializes a new <see cref="QuantumRangeEncoder"/>.</summary>
  /// <param name="output">The stream that receives the packed code bits.</param>
  public QuantumRangeEncoder(Stream output) => this._output = output;

  /// <summary>Encodes a symbol and updates the model with it.</summary>
  /// <param name="model">The adaptive frequency model.</param>
  /// <param name="symbol">The symbol to encode.</param>
  public void EncodeSymbol(QuantumModel model, int symbol) {
    var range = (ulong)this._high - this._low + 1;
    var total = (ulong)model.TotalFrequency;
    var cumulativeLow = (ulong)model.CumulativeBelow(symbol);
    var cumulativeHigh = cumulativeLow + (ulong)model.GetFrequency(symbol);

    var low = this._low;
    this._high = (uint)(low + range * cumulativeHigh / total - 1);
    this._low = (uint)(low + range * cumulativeLow / total);

    this.Renormalize();
    model.Update(symbol);
  }

  /// <summary>Encodes one bit with a fixed 50/50 probability, carrying no model state.</summary>
  /// <param name="bit">The bit to encode (0 or 1).</param>
  public void EncodeEqualProbabilityBit(int bit) {
    var range = (ulong)this._high - this._low + 1;
    var half = range / 2;

    if (bit != 0)
      this._low = (uint)(this._low + half);
    else
      this._high = (uint)(this._low + half - 1);

    this.Renormalize();
  }

  /// <summary>
  /// Flushes the coder state and any partial byte. Must be called once, after every
  /// symbol has been encoded.
  /// </summary>
  public void Finish() {
    // Two bits are enough to name a point inside the final interval: emit the one that
    // says which quarter it starts in, preceded by the pending follow bits.
    ++this._followBits;
    this.OutputBit(this._low < Quarter ? 0 : 1);

    if (this._bitCount == 0)
      return;

    this._output.WriteByte((byte)(this._bitBuffer << (8 - this._bitCount)));
    this._bitBuffer = 0;
    this._bitCount = 0;
  }

  private void Renormalize() {
    for (;;) {
      if (this._high < Half)
        this.OutputBit(0);
      else if (this._low >= Half) {
        this.OutputBit(1);
        this._low -= Half;
        this._high -= Half;
      } else if (this._low >= Quarter && this._high < ThreeQuarters) {
        ++this._followBits;
        this._low -= Quarter;
        this._high -= Quarter;
      } else
        break;

      this._low <<= 1;
      this._high = (this._high << 1) | 1;
    }
  }

  private void OutputBit(int bit) {
    this.WriteBit(bit);
    for (; this._followBits > 0; --this._followBits)
      this.WriteBit(1 - bit);
  }

  private void WriteBit(int bit) {
    this._bitBuffer = (this._bitBuffer << 1) | bit;
    if (++this._bitCount < 8)
      return;

    this._output.WriteByte((byte)this._bitBuffer);
    this._bitBuffer = 0;
    this._bitCount = 0;
  }
}
