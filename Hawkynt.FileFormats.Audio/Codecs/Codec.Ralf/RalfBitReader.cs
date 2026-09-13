#pragma warning disable CS1591
namespace Codec.Ralf;

/// <summary>
/// Big-endian MSB-first bit reader matching FFmpeg's default <c>get_bits</c> reader, plus the
/// Exp-Golomb and unary readers RALF needs (<c>get_ue_golomb</c>, <c>get_unary</c>).
/// </summary>
internal sealed class RalfBitReader {
  private readonly byte[] _data;
  private readonly int _bitLength;
  private int _bitPos;

  public RalfBitReader(byte[] data, int offset, int bitLength) {
    this._data = data;
    this.Offset = offset;
    this._bitLength = bitLength;
    this._bitPos = 0;
  }

  public int Offset { get; }

  /// <summary>Bits remaining (may go negative once a read overruns the declared length).</summary>
  public int BitsLeft => this._bitLength - this._bitPos;

  public int GetBit() {
    var absolute = this.Offset * 8 + this._bitPos;
    var byteIndex = absolute >> 3;
    var bit = byteIndex < this._data.Length
      ? (this._data[byteIndex] >> (7 - (absolute & 7))) & 1
      : 0;
    ++this._bitPos;
    return bit;
  }

  public int GetBits(int n) {
    var value = 0;
    for (var i = 0; i < n; ++i)
      value = (value << 1) | this.GetBit();
    return value;
  }

  /// <summary>Unsigned Exp-Golomb (0..8190): leading-zero run <c>k</c>, then <c>k</c> bits.</summary>
  public int GetUeGolomb() {
    var leadingZeros = 0;
    while (this.GetBit() == 0 && leadingZeros < 32)
      ++leadingZeros;
    var value = 1;
    for (var i = 0; i < leadingZeros; ++i)
      value = (value << 1) | this.GetBit();
    return value - 1;
  }

  /// <summary>
  /// <c>get_unary(gb, 0, len)</c>: counts leading one bits, stopping at the first zero bit (which
  /// is consumed) or after <paramref name="len"/> bits.
  /// </summary>
  public int GetUnary(int len) {
    var i = 0;
    while (i < len && this.GetBit() != 0)
      ++i;
    return i;
  }
}
