namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// MSB-first bit reader for the LArc/LHarc/PMarc decoders. Reads past the end of the data return
/// zero bits, as the reference decoders' zero-filled input buffers do; callers bound their work by the
/// member's declared size.
/// </summary>
internal sealed class LhaBitInput(byte[] data) {
  private readonly byte[] _data = data;
  private long _bitPos;

  /// <summary>Gets whether every bit of the input has been consumed.</summary>
  public bool Exhausted => this._bitPos >= (long)this._data.Length * 8;

  public int ReadBit() {
    var index = (int)(this._bitPos >> 3);
    var bit = index < this._data.Length ? (this._data[index] >> (7 - (int)(this._bitPos & 7))) & 1 : 0;
    ++this._bitPos;
    return bit;
  }

  /// <summary>Reads <paramref name="count"/> bits (0-24) as an unsigned value.</summary>
  public int ReadBits(int count) {
    var value = 0;
    for (var i = 0; i < count; ++i)
      value = (value << 1) | this.ReadBit();
    return value;
  }

  /// <summary>Returns the next <paramref name="count"/> bits without consuming them.</summary>
  public int PeekBits(int count) {
    var saved = this._bitPos;
    var value = this.ReadBits(count);
    this._bitPos = saved;
    return value;
  }

  public void Skip(int count) => this._bitPos += count;
}
