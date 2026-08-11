namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Bit-oriented 32-bit arithmetic decoder used by the Quantum decompressor.
/// </summary>
/// <remarks>
/// The exact mirror of <see cref="QuantumRangeEncoder"/>: it tracks the same
/// [low, high] interval and performs the same renormalisation, shifting one code bit
/// in from the stream each time the encoder shifted one out. Bits are read
/// most-significant-bit first, and reads past the end of the data yield zero bits so
/// that the encoder's zero padding of the final byte needs no separate handling.
/// </remarks>
internal sealed class QuantumRangeDecoder {
  private const uint Top = 0xFFFFFFFFu;
  private const uint Half = 0x80000000u;
  private const uint Quarter = 0x40000000u;
  private const uint ThreeQuarters = 0xC0000000u;

  private readonly ReadOnlyMemory<byte> _data;
  private int _bitPosition;
  private uint _low;
  private uint _high = Top;
  private uint _value;

  /// <summary>
  /// Initializes a new <see cref="QuantumRangeDecoder"/> from compressed data.
  /// </summary>
  /// <param name="data">The packed code bits produced by <see cref="QuantumRangeEncoder"/>.</param>
  public QuantumRangeDecoder(ReadOnlyMemory<byte> data) {
    this._data = data;
    for (var i = 0; i < 32; ++i)
      this._value = (this._value << 1) | this.NextBit();
  }

  /// <summary>Decodes a symbol from the given adaptive model and updates the model.</summary>
  /// <param name="model">The adaptive frequency model.</param>
  /// <returns>The decoded symbol index.</returns>
  public int DecodeSymbol(QuantumModel model) {
    var range = (ulong)this._high - this._low + 1;
    var total = (ulong)model.TotalFrequency;
    var scaled = (int)((((ulong)(this._value - this._low) + 1) * total - 1) / range);
    var symbol = model.FindSymbol(scaled, out var cumulativeLow);
    var cumulativeHigh = (ulong)(cumulativeLow + model.GetFrequency(symbol));

    var low = this._low;
    this._high = (uint)(low + range * cumulativeHigh / total - 1);
    this._low = (uint)(low + range * (ulong)cumulativeLow / total);

    this.Renormalize();
    model.Update(symbol);
    return symbol;
  }

  /// <summary>Decodes one bit that was written with a fixed 50/50 probability.</summary>
  /// <returns>The decoded bit (0 or 1).</returns>
  public int DecodeEqualProbabilityBit() {
    var range = (ulong)this._high - this._low + 1;
    var mid = (uint)(this._low + range / 2 - 1);

    int bit;
    if (this._value <= mid) {
      bit = 0;
      this._high = mid;
    } else {
      bit = 1;
      this._low = mid + 1;
    }

    this.Renormalize();
    return bit;
  }

  private void Renormalize() {
    for (;;) {
      if (this._high < Half) {
        // Interval sits in the lower half: nothing to subtract, just double it.
      } else if (this._low >= Half) {
        this._low -= Half;
        this._high -= Half;
        this._value -= Half;
      } else if (this._low >= Quarter && this._high < ThreeQuarters) {
        this._low -= Quarter;
        this._high -= Quarter;
        this._value -= Quarter;
      } else
        break;

      this._low <<= 1;
      this._high = (this._high << 1) | 1;
      this._value = (this._value << 1) | this.NextBit();
    }
  }

  private uint NextBit() {
    var span = this._data.Span;
    var index = this._bitPosition >> 3;
    if (index >= span.Length)
      return 0;

    var bit = (uint)(span[index] >> (7 - (this._bitPosition & 7))) & 1u;
    ++this._bitPosition;
    return bit;
  }
}
