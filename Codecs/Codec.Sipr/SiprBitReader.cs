#pragma warning disable CS1591
namespace Codec.Sipr;

/// <summary>
/// Little-endian (LSB-first) bit reader mirroring FFmpeg's <c>get_bits</c> when the
/// translation unit defines <c>BITSTREAM_READER_LE</c> (which <c>sipr.c</c> does). At bit
/// position <c>idx</c> the reference forms a 32-bit little-endian word from
/// <c>buffer[idx/8 ..]</c>, shifts it right by <c>idx &amp; 7</c> and returns the low
/// requested bits. Equivalently bits are consumed LSB-first within each byte and
/// the first-consumed bit becomes the least-significant bit of the returned value. Reads past
/// the declared bit length return zero while still advancing the position, matching how the
/// reference tolerates a short tail (the decoder pre-checks the packet size).
/// </summary>
internal sealed class SiprBitReader {
  private readonly byte[] _data;
  private readonly int _offset;
  private readonly int _sizeInBits;
  private int _index;

  public SiprBitReader(byte[] data, int offset, int sizeInBits) {
    this._data = data;
    this._offset = offset;
    this._sizeInBits = sizeInBits;
    this._index = 0;
  }

  /// <summary>Reads <paramref name="n"/> bits (<c>get_bits</c>, LE convention).</summary>
  public int GetBits(int n) {
    var value = 0;
    for (var i = 0; i < n; ++i) {
      var pos = this._index;
      var bit = 0;
      if (pos < this._sizeInBits) {
        var bytePos = this._offset + (pos >> 3);
        if (bytePos < this._data.Length)
          bit = (this._data[bytePos] >> (pos & 7)) & 1;
      }
      value |= bit << i;
      ++this._index;
    }
    return value;
  }
}
