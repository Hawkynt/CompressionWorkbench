namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// The 16-bit look-ahead bit buffer of the LHarc/LHa decoders (<c>fillbuf</c>/<c>getbits</c>):
/// MSB-first, with the next 16 bits always visible in <see cref="BitBuf"/>. Reads past the end yield
/// zero bits, as the reference decoders do.
/// </summary>
internal sealed class LhaLegacyBitReader {
  private readonly byte[] _data;
  private int _pos;
  private byte _subBitBuf;
  private int _bitCount;

  /// <summary>The next 16 bits of the stream, most significant first.</summary>
  public ushort BitBuf { get; private set; }

  public LhaLegacyBitReader(byte[] data) {
    this._data = data;
    this.FillBuf(16);
  }

  /// <summary>Drops <paramref name="n"/> bits (0-16) from <see cref="BitBuf"/> and shifts new ones in.</summary>
  public void FillBuf(int n) {
    while (n > this._bitCount) {
      n -= this._bitCount;
      this.BitBuf = (ushort)((this.BitBuf << this._bitCount) + (this._subBitBuf >> (8 - this._bitCount)));
      this._subBitBuf = this._pos < this._data.Length ? this._data[this._pos++] : (byte)0;
      this._bitCount = 8;
    }
    this._bitCount -= n;
    this.BitBuf = (ushort)((this.BitBuf << n) + (this._subBitBuf >> (8 - n)));
    this._subBitBuf = (byte)(this._subBitBuf << n);
  }

  /// <summary>Reads <paramref name="n"/> bits (0-16) as an unsigned value.</summary>
  public int GetBits(int n) {
    var x = this.BitBuf >> (16 - n);
    this.FillBuf(n);
    return x;
  }
}
