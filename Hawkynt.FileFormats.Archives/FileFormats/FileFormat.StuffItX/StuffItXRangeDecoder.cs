namespace FileFormat.StuffItX;

/// <summary>
/// The carry-less range decoder (Subbotin style) behind Brimstone, Cyanide and Iron. Brimstone and
/// Cyanide keep a low bound and renormalise once the range drops under 0x10000; Iron subtracts from
/// the code instead and only renormalises on the top byte.
/// </summary>
internal sealed class StuffItXRangeDecoder {
  private readonly StuffItXInput _input;
  private readonly bool _useLow;
  private readonly uint _bottom;
  private uint _low;
  private uint _code;
  private uint _range = 0xFFFFFFFF;

  public StuffItXRangeDecoder(StuffItXInput input, bool useLow, uint bottom) {
    this._input = input;
    this._useLow = useLow;
    this._bottom = bottom;
    for (var i = 0; i < 4; ++i) this._code = (this._code << 8) | (uint)input.ReadByte();
  }

  /// <summary>Scales the range to <paramref name="scale"/> and returns the count the code falls on.</summary>
  public uint CurrentCount(uint scale) {
    if (scale == 0) throw new InvalidDataException("StuffIt X range coder: empty frequency table.");
    this._range /= scale;
    if (this._range == 0) throw new InvalidDataException("StuffIt X range coder: range underflow.");
    return (this._code - this._low) / this._range;
  }

  /// <summary>Consumes the sub-range [<paramref name="lowCount"/>, <paramref name="highCount"/>).</summary>
  public void RemoveSubRange(uint lowCount, uint highCount) {
    if (this._useLow) this._low += this._range * lowCount;
    else this._code -= this._range * lowCount;
    this._range *= highCount - lowCount;
    this.Normalize();
  }

  /// <summary>Symbol index from a frequency table (cumulative search).</summary>
  public int NextSymbol(ReadOnlySpan<uint> frequencies) {
    var total = 0u;
    foreach (var f in frequencies) total += f;
    var count = this.CurrentCount(total);
    var cumulative = 0u;
    var n = 0;
    while (n < frequencies.Length - 1 && cumulative + frequencies[n] <= count) cumulative += frequencies[n++];
    this.RemoveSubRange(cumulative, cumulative + frequencies[n]);
    return n;
  }

  /// <summary>A bit that is 0 with probability <paramref name="weight"/>/<paramref name="size"/>.</summary>
  public int NextWeightedBit(uint weight, uint size) {
    var value = this.CurrentCount(size);
    if (value < weight) {
      this.RemoveSubRange(0, weight);
      return 0;
    }
    this.RemoveSubRange(weight, size);
    return 1;
  }

  private void Normalize() {
    for (;;) {
      if ((this._low ^ (this._low + this._range)) >= 0x1000000) {
        if (this._range >= this._bottom) break;
        this._range = (0u - this._low) & (this._bottom - 1);
      }
      this._code = (this._code << 8) | (uint)this._input.ReadByte();
      this._range <<= 8;
      this._low <<= 8;
    }
  }
}
