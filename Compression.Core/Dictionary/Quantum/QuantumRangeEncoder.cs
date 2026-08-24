namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Quantum's arithmetic encoder: sixteen-bit <c>low</c> and <c>high</c>, in the shape
/// the CACM87 paper describes, with the underflow case carried as pending bits.
/// </summary>
/// <remarks>
/// <para>The awkward part is not the coder but the bits that bypass it. A slot's
/// extra bits are written raw, and the decoder reads them at the point <em>its</em>
/// reading has reached — which is sixteen bits ahead of what this coder has emitted,
/// because the decoder swallowed sixteen priming bits before it decoded anything. So
/// raw bits cannot simply be appended: they belong sixteen bits further on, inside
/// coder output this encoder has not produced yet. They are therefore remembered with
/// the position they belong at and spliced in when the stream is finished.</para>
///
/// <para>Appending them where they are written happens to work whenever the coder has
/// no bits in flight, which is why getting this wrong looks like a fault that depends
/// on the data.</para>
/// </remarks>
internal sealed class QuantumRangeEncoder {
  private readonly List<int> _bits = [];
  private readonly List<(int At, int Value, int Count)> _rawBits = [];
  private uint _low;
  private uint _high = 0xFFFF;
  private int _pending;
  private int _shifts;

  /// <summary>Codes one symbol of a model, then lets the model learn from it.</summary>
  /// <param name="model">The model to code against.</param>
  /// <param name="index">The position of the symbol in that model.</param>
  public void Encode(QuantumModel model, int index) {
    var total = (uint)model.TotalFrequency;
    var range = this._high - this._low + 1;
    var above = (uint)model.CumulativeFrom(index + 1);
    var atOrAbove = above + (uint)model.FrequencyAt(index);

    this._high = this._low + range * atOrAbove / total - 1;
    this._low += range * above / total;

    for (;;) {
      if ((this._low & 0x8000) == (this._high & 0x8000))
        this.Emit((int)(this._low >> 15));
      else if ((this._low & 0x4000) != 0 && (this._high & 0x4000) == 0) {
        ++this._pending;
        this._low &= 0x3FFF;
        this._high |= 0x4000;
      } else
        break;

      this._low = (this._low << 1) & 0xFFFF;
      this._high = ((this._high << 1) | 1) & 0xFFFF;
      ++this._shifts;
    }

    model.Update(index);
  }

  /// <summary>
  /// Notes bits that bypass the coder, to be placed where the decoder will read them.
  /// </summary>
  /// <param name="value">The value to spell out.</param>
  /// <param name="count">How many bits to spell it in.</param>
  public void EncodeRaw(int value, int count) {
    if (count > 0)
      this._rawBits.Add((16 + this._shifts, value, count));
  }

  /// <summary>Closes the stream and returns the block a cabinet should carry.</summary>
  /// <returns>The compressed bytes.</returns>
  public byte[] Finish() {
    ++this._pending;
    this.Emit(this._low < 0x4000 ? 0 : 1);

    var bits = new List<int>(this._bits);
    var shift = 0;
    foreach (var (at, value, count) in this._rawBits) {
      var index = at + shift;

      // early on the coder has emitted fewer bits than the decoder has already read,
      // so the slot may lie past everything written so far and has to be made
      while (bits.Count < index)
        bits.Add(0);

      for (var k = count - 1; k >= 0; --k)
        bits.Insert(index + (count - 1 - k), (value >> k) & 1);

      shift += count;
    }

    while (bits.Count % 8 != 0)
      bits.Add(0);

    var data = new byte[bits.Count / 8 + QuantumConstants.TrailingSlackBytes];
    for (var i = 0; i < bits.Count; ++i)
      if (bits[i] != 0)
        data[i >> 3] |= (byte)(0x80 >> (i & 7));

    return data;
  }

  private void Emit(int bit) {
    this._bits.Add(bit);
    while (this._pending > 0) {
      this._bits.Add(bit ^ 1);
      --this._pending;
    }
  }
}
