namespace FileFormat.StuffItX;

/// <summary>
/// The carry-less range encoder (Subbotin style) whose output <see cref="StuffItXRangeDecoder"/>
/// reads in its low-bound mode: the same scaling, sub-range and renormalisation steps, writing the
/// top byte of the low bound where the decoder reads one.
/// </summary>
internal sealed class StuffItXRangeEncoder(uint bottom) {
  private readonly MemoryStream _out = new();
  private uint _low;
  private uint _range = 0xFFFFFFFF;

  /// <summary>Scales the range to a total of <paramref name="scale"/> counts.</summary>
  public void Scale(uint scale) {
    if (scale == 0) throw new InvalidOperationException("StuffIt X range coder: empty frequency table.");
    this._range /= scale;
    if (this._range == 0) throw new InvalidOperationException("StuffIt X range coder: range underflow.");
  }

  /// <summary>Codes the sub-range [<paramref name="lowCount"/>, <paramref name="highCount"/>) of the scaled range.</summary>
  public void Encode(uint lowCount, uint highCount) {
    this._low += this._range * lowCount;
    this._range *= highCount - lowCount;
    for (;;) {
      if ((this._low ^ (this._low + this._range)) >= 0x1000000) {
        if (this._range >= bottom) break;
        this._range = (0u - this._low) & (bottom - 1);
      }
      this._out.WriteByte((byte)(this._low >> 24));
      this._range <<= 8;
      this._low <<= 8;
    }
  }

  /// <summary>Flushes the low bound (four bytes) and returns the coded bytes.</summary>
  public byte[] Finish() {
    for (var i = 0; i < 4; ++i) {
      this._out.WriteByte((byte)(this._low >> 24));
      this._low <<= 8;
    }
    return this._out.ToArray();
  }
}
