#pragma warning disable CS1591

namespace Codec.Bonk;

/// <summary>
/// MSB-first bit reader matching ffmpeg's <c>GetBitContext</c> semantics
/// (<c>get_bits1</c>, <c>get_bits</c>, <c>get_unary</c>): bits are consumed from
/// the most-significant bit of each byte forward. Reading past the end returns
/// zero bits, mirroring ffmpeg's padded buffers.
/// </summary>
internal sealed class BonkBitReader(ReadOnlyMemory<byte> data) {

  private readonly ReadOnlyMemory<byte> _data = data;
  private int _bitPos;

  public int Position => this._bitPos;
  public int BitsLeft => this._data.Length * 8 - this._bitPos;

  public void SkipBits(int count) => this._bitPos += count;

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

  /// <summary>
  /// ffmpeg <c>get_unary(gb, 1, limit)</c>: counts consecutive 1-bits until a 0
  /// (the stop bit) or the limit is reached.
  /// </summary>
  public uint GetUnary(int limit) {
    uint count = 0;
    while (count < (uint)limit && this.GetBit() != 0)
      ++count;
    return count;
  }
}
