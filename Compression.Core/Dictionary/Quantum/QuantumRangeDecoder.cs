namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// 16-bit byte-based range decoder for the Quantum compression format.
/// </summary>
/// <remarks>
/// <para>
/// Quantum uses a 16-bit range coder where normalization shifts in whole bytes
/// (not individual bits). The range [low, high] is maintained as unsigned 16-bit values,
/// and when (high - low) drops below 256 the coder shifts both endpoints left by 8 bits
/// and reads a new byte into the code value.
/// </para>
/// <para>
/// This decoder also supports reading raw bits (not probability-coded) from the
/// byte stream via <see cref="ReadRawBits"/>, which is used for match offsets.
/// </para>
/// </remarks>
internal sealed class QuantumRangeDecoder {
  private readonly ReadOnlyMemory<byte> _data;
  private int _pos;
  private int _high;
  private int _low;
  private int _code;

  /// <summary>
  /// Initializes a new <see cref="QuantumRangeDecoder"/> from compressed data.
  /// </summary>
  /// <param name="data">The compressed data.</param>
  public QuantumRangeDecoder(ReadOnlyMemory<byte> data) {
    this._data = data;
    this._pos = 0;
    this._low = 0;
    this._high = 0xFFFF;

    // Read initial 2 bytes as big-endian 16-bit code value
    this._code = this.ReadByte() << 8;
    this._code |= this.ReadByte();
  }

  /// <summary>
  /// Decodes a symbol from the given adaptive model.
  /// </summary>
  /// <param name="model">The adaptive frequency model.</param>
  /// <returns>The decoded symbol index.</returns>
  public int DecodeSymbol(QuantumModel model) {
    var range = this._high - this._low + 1;
    var scaledCount = (int)(((long)(this._code - this._low + 1) * model.TotalFrequency - 1) / range);

    var symbol = model.FindSymbol(scaledCount);

    var symLow = model.GetCumulativeFrequency(symbol);
    var symHigh = symLow + model.GetFrequency(symbol);

    this._high = this._low + (int)((long)range * symHigh / model.TotalFrequency) - 1;
    this._low += (int)((long)range * symLow / model.TotalFrequency);

    this.Normalize();

    model.Update(symbol);
    return symbol;
  }

  /// <summary>
  /// Reads raw (uncoded) bits from the range coder stream.
  /// Each bit is decoded as a 50/50 binary decision (mid-split of the range).
  /// </summary>
  /// <param name="numBits">The number of bits to read (MSB first).</param>
  /// <returns>The decoded value.</returns>
  public int ReadRawBits(int numBits) {
    var value = 0;
    for (var i = 0; i < numBits; ++i) {
      var range = this._high - this._low + 1;
      var mid = this._low + (range >> 1) - 1;
      int bit;
      if (this._code <= mid) {
        bit = 0;
        this._high = mid;
      } else {
        bit = 1;
        this._low = mid + 1;
      }
      value = (value << 1) | bit;

      this.Normalize();
    }
    return value;
  }

  private void Normalize() {
    while ((this._high - this._low) < 256) {
      this._high = ((this._high << 8) | 0xFF) & 0xFFFF;
      this._low = (this._low << 8) & 0xFFFF;
      this._code = ((this._code << 8) | this.ReadByte()) & 0xFFFF;
    }
  }

  private int ReadByte() {
    var span = this._data.Span;
    return this._pos < span.Length ? span[this._pos++] : 0; // Pad with zero bytes past end of input
  }
}
