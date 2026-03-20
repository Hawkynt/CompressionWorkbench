namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// 16-bit byte-based range encoder for the Quantum compression format.
/// </summary>
/// <remarks>
/// Mirrors <see cref="QuantumRangeDecoder"/>: the range [low, high] is maintained as
/// 16-bit values. When (high − low) drops below 256 the encoder shifts both endpoints
/// left by 8 bits and outputs the high byte of <c>low</c>.
/// </remarks>
internal sealed class QuantumRangeEncoder {
  private readonly Stream _output;
  private int _low;
  private int _high;

  /// <summary>
  /// Initializes a new <see cref="QuantumRangeEncoder"/>.
  /// </summary>
  /// <param name="output">The output stream.</param>
  public QuantumRangeEncoder(Stream output) {
    this._output = output;
    this._low = 0;
    this._high = 0xFFFF;
  }

  /// <summary>
  /// Encodes a symbol using the given adaptive model.
  /// </summary>
  /// <param name="model">The adaptive frequency model.</param>
  /// <param name="symbol">The symbol to encode.</param>
  public void EncodeSymbol(QuantumModel model, int symbol) {
    var range = this._high - this._low + 1;
    var symLow = model.GetCumulativeFrequency(symbol);
    var symHigh = symLow + model.GetFrequency(symbol);

    this._high = this._low + (int)((long)range * symHigh / model.TotalFrequency) - 1;
    this._low += (int)((long)range * symLow / model.TotalFrequency);

    this.Normalize();
    model.Update(symbol);
  }

  /// <summary>
  /// Writes raw (uncoded) bits to the stream as 50/50 binary decisions (MSB first).
  /// </summary>
  /// <param name="value">The value whose bits are written.</param>
  /// <param name="numBits">The number of bits to write.</param>
  public void WriteRawBits(int value, int numBits) {
    for (var i = numBits - 1; i >= 0; --i) {
      var bit = (value >> i) & 1;
      var range = this._high - this._low + 1;
      var mid = this._low + (range >> 1) - 1;
      if (bit == 0)
        this._high = mid;
      else
        this._low = mid + 1;
      this.Normalize();
    }
  }

  /// <summary>
  /// Flushes the final encoder state to the output. Must be called after all
  /// symbols have been encoded.
  /// </summary>
  public void Finish() {
    // Normalize any remaining narrow range
    this.Normalize();
    // Output the final 2 bytes of low to disambiguate the last symbol
    this._output.WriteByte((byte)((this._low >> 8) & 0xFF));
    this._output.WriteByte((byte)(this._low & 0xFF));
    // Padding: the decoder may read ahead during normalization.
    // Extra zero bytes prevent out-of-bounds reads.
    this._output.WriteByte(0);
    this._output.WriteByte(0);
  }

  private void Normalize() {
    while ((this._high - this._low) < 256) {
      this._output.WriteByte((byte)((this._low >> 8) & 0xFF));
      this._low = (this._low << 8) & 0xFFFF;
      this._high = ((this._high << 8) | 0xFF) & 0xFFFF;
    }
  }
}
