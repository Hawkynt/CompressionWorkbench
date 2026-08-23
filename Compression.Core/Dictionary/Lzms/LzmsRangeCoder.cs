namespace Compression.Core.Dictionary.Lzms;

/// <summary>
/// The range coder, which runs forwards from the start of a chunk in sixteen-bit
/// little-endian units.
/// </summary>
internal sealed class LzmsRangeDecoder {
  private readonly ReadOnlyMemory<byte> _data;
  private int _position;
  private uint _range = uint.MaxValue;
  private uint _code;

  public LzmsRangeDecoder(ReadOnlyMemory<byte> data) {
    this._data = data;
    this._code = (uint)((this.NextUnit() << 16) | this.NextUnit());
  }

  private int NextUnit() {
    var span = this._data.Span;
    var value = this._position + 2 <= span.Length
      ? span[this._position] | (span[this._position + 1] << 8)
      : 0;
    this._position += 2;
    return value;
  }

  public int ReadBit(LzmsProbability probability) {
    // Only the bound is scaled; narrowing the range before the subtraction wraps.
    var bound = (this._range >> LzmsConstants.NumProbBits) * (uint)probability.Probability;
    int bit;
    if (this._code < bound) {
      bit = 0;
      this._range = bound;
    } else {
      bit = 1;
      this._code -= bound;
      this._range -= bound;
    }

    probability.Update(bit);
    while (this._range <= 0xFFFF) {
      this._range <<= 16;
      this._code = (this._code << 16) | (uint)this.NextUnit();
    }

    return bit;
  }
}

/// <summary>
/// The mirror of <see cref="LzmsRangeDecoder"/>.
/// </summary>
/// <remarks>
/// Units are buffered rather than streamed, so a carry is simply added back into
/// what has already been produced. At chunk sizes that costs nothing and removes
/// the usual cache-and-pending bookkeeping.
/// </remarks>
internal sealed class LzmsRangeEncoder {
  /// <summary>
  /// Words of slack after the flush. The decoder keeps reading forwards after the
  /// last item, and the two streams share one buffer, so without this it reads into
  /// the backward stream. One is what wimlib emits: taking wimlib's own chunk, the
  /// items it decodes to, and writing them back gives its bytes exactly with one
  /// word here and two bytes too many with two.
  /// </summary>
  private const int TrailingWords = 1;

  private readonly List<ushort> _units = [];
  private ulong _low;
  private uint _range = uint.MaxValue;

  private void Carry() {
    for (var i = this._units.Count - 1; i >= 0; --i) {
      this._units[i] = (ushort)(this._units[i] + 1);
      if (this._units[i] != 0) return;
    }
  }

  private void ShiftOut() {
    this._units.Add((ushort)(this._low >> 16));
    this._low = (this._low << 16) & uint.MaxValue;
  }

  public void WriteBit(LzmsProbability probability, int bit) {
    var bound = (this._range >> LzmsConstants.NumProbBits) * (uint)probability.Probability;
    if (bit == 0)
      this._range = bound;
    else {
      this._low += bound;
      if (this._low > uint.MaxValue) {
        this._low &= uint.MaxValue;
        this.Carry();
      }
      this._range -= bound;
    }

    probability.Update(bit);
    while (this._range <= 0xFFFF) {
      this.ShiftOut();
      this._range <<= 16;
    }
  }

  public IReadOnlyList<ushort> Finish() {
    this.ShiftOut();
    this.ShiftOut();
    for (var i = 0; i < TrailingWords; ++i) this._units.Add(0);
    return this._units;
  }
}
