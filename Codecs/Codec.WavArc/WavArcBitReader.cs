#pragma warning disable CS1591

namespace Codec.WavArc;

/// <summary>
/// MSB-first bit reader matching ffmpeg's <c>GetBitContext</c> used by the WavArc
/// decoder. Bits are consumed from the most-significant bit of each byte forward;
/// reading past the end yields zero bits.
/// </summary>
internal sealed class WavArcBitReader(ReadOnlyMemory<byte> data) {

  private readonly ReadOnlyMemory<byte> _data = data;
  private int _bitPos;

  public int BitsLeft => this._data.Length * 8 - this._bitPos;

  public int GetBit() {
    var span = this._data.Span;
    var bytePos = this._bitPos >> 3;
    if (bytePos >= span.Length) {
      ++this._bitPos;
      return 0;
    }
    var bit = (span[bytePos] >> (7 - (this._bitPos & 7))) & 1;
    ++this._bitPos;
    return bit;
  }

  public uint GetBits(int count) {
    uint value = 0;
    for (var i = 0; i < count; ++i)
      value = (value << 1) | (uint)this.GetBit();
    return value;
  }

  /// <summary>ffmpeg <c>get_unary(gb, 1, limit)</c>: count consecutive 1-bits until a 0 or the limit.</summary>
  public uint GetUnary(int limit) {
    uint count = 0;
    while (count < (uint)limit && this.GetBit() != 0)
      ++count;
    return count;
  }

  /// <summary>ffmpeg <c>get_urice(gb, k)</c>: unary high bits then <paramref name="k"/> raw low bits.</summary>
  public uint GetURice(int k) {
    var x = this.GetUnary(this.BitsLeft);
    var y = this.GetBits(k);
    return (x << k) | y;
  }

  /// <summary>ffmpeg <c>get_srice</c>: zig-zag mapping of <see cref="GetURice"/>.</summary>
  public int GetSRice(int k) {
    var z = this.GetURice(k);
    return (z & 1) != 0 ? ~(int)(z >> 1) : (int)(z >> 1);
  }
}
