#pragma warning disable CS1591

namespace Codec.Bonk;

/// <summary>
/// MSB-first bit writer — the exact inverse of <see cref="BonkBitReader"/>. Bits
/// are packed into the most-significant bit of each byte forward, matching
/// ffmpeg's <c>PutBitContext</c> / <c>GetBitContext</c> ordering so a written
/// stream reads back identically.
/// </summary>
internal sealed class BonkBitWriter {

  private readonly List<byte> _bytes = [];
  private int _current;
  private int _bitsInCurrent;

  public int BitPosition => this._bytes.Count * 8 + this._bitsInCurrent;

  public void PutBit(int bit) {
    this._current = (this._current << 1) | (bit & 1);
    if (++this._bitsInCurrent == 8) {
      this._bytes.Add((byte)this._current);
      this._current = 0;
      this._bitsInCurrent = 0;
    }
  }

  public void PutBits(int count, uint value) {
    for (var i = count - 1; i >= 0; --i)
      this.PutBit((int)((value >> i) & 1));
  }

  /// <summary>Writes <paramref name="count"/> one-bits followed by a zero stop-bit (matching <c>get_unary(gb,1,..)</c>).</summary>
  public void PutUnary(uint count) {
    for (uint i = 0; i < count; ++i)
      this.PutBit(1);
    this.PutBit(0);
  }

  public byte[] ToArray() {
    var result = new List<byte>(this._bytes);
    if (this._bitsInCurrent > 0)
      result.Add((byte)(this._current << (8 - this._bitsInCurrent)));
    return result.ToArray();
  }
}
