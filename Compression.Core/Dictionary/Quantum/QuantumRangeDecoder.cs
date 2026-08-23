namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Quantum's arithmetic decoder, the mirror of <see cref="QuantumRangeEncoder"/>.
/// </summary>
/// <remarks>
/// It primes itself with the first sixteen bits and then shifts one bit in for each
/// one the encoder shifted out. A slot's extra bits are taken raw from the stream at
/// whatever point the reading has reached, which is exactly why the encoder has to
/// place them sixteen bits ahead of its own output. Reads past the end yield zeros so
/// that a block's padding needs no special case.
/// </remarks>
internal sealed class QuantumRangeDecoder {
  private readonly ReadOnlyMemory<byte> _data;
  private int _bitPosition;
  private uint _low;
  private uint _high = 0xFFFF;
  private uint _code;

  /// <summary>Initializes a decoder over one cabinet block.</summary>
  /// <param name="data">The compressed bytes.</param>
  public QuantumRangeDecoder(ReadOnlyMemory<byte> data) {
    this._data = data;
    for (var i = 0; i < 16; ++i)
      this._code = (this._code << 1) | this.NextBit();
  }

  /// <summary>Decodes one symbol of a model, then lets the model learn from it.</summary>
  /// <param name="model">The model to decode against.</param>
  /// <returns>The symbol, not its position.</returns>
  public int Decode(QuantumModel model) {
    var total = (uint)model.TotalFrequency;
    var range = this._high - this._low + 1;
    var scaled = (int)(((this._code - this._low + 1) * total - 1) / range);
    var index = model.FindIndex(scaled, out var above);
    var atOrAbove = (uint)(above + model.FrequencyAt(index));

    this._high = this._low + range * atOrAbove / total - 1;
    this._low += range * (uint)above / total;

    for (;;) {
      if ((this._low & 0x8000) == (this._high & 0x8000)) {
        // the top bits agree: nothing to fold away, just widen
      } else if ((this._low & 0x4000) != 0 && (this._high & 0x4000) == 0) {
        this._code ^= 0x4000;
        this._low &= 0x3FFF;
        this._high |= 0x4000;
      } else
        break;

      this._low = (this._low << 1) & 0xFFFF;
      this._high = ((this._high << 1) | 1) & 0xFFFF;
      this._code = ((this._code << 1) | this.NextBit()) & 0xFFFF;
    }

    // the symbol has to be read before the model updates itself
    var symbol = model.SymbolAt(index);
    model.Update(index);
    return symbol;
  }

  /// <summary>Reads bits that bypass the coder.</summary>
  /// <param name="count">How many bits to read.</param>
  /// <returns>The value they spell, most significant bit first.</returns>
  public int DecodeRaw(int count) {
    var value = 0;
    for (var i = 0; i < count; ++i)
      value = (value << 1) | (int)this.NextBit();

    return value;
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
