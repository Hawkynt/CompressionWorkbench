#pragma warning disable CS1591
namespace Codec.Cook;

/// <summary>
/// MSB-first bit reader mirroring FFmpeg's <c>GetBitContext</c> (<c>get_bits</c>,
/// <c>get_bits1</c>, <c>get_bits_count</c>, <c>get_bits_left</c>). The context is
/// initialised with an explicit bit length (<c>init_get_bits</c> with a byte buffer and
/// a bit count), so reads past the declared end return zero bits while still advancing the
/// position — matching how the reference tolerates an over-read at the tail of a subpacket.
/// </summary>
internal sealed class CookBitReader {
  private readonly byte[] _data;
  private readonly int _offset;
  private readonly int _sizeInBits;
  private int _index;

  public CookBitReader(byte[] data, int offset, int sizeInBits) {
    this._data = data;
    this._offset = offset;
    this._sizeInBits = sizeInBits;
    this._index = 0;
  }

  /// <summary>Number of bits consumed so far (<c>get_bits_count</c>).</summary>
  public int BitsCount => this._index;

  /// <summary>Number of bits still available (<c>get_bits_left</c>).</summary>
  public int BitsLeft => this._sizeInBits - this._index;

  /// <summary>Reads a single bit (<c>get_bits1</c>).</summary>
  public int GetBit() {
    var bit = 0;
    var bytePos = this._offset + (this._index >> 3);
    if (bytePos < this._data.Length)
      bit = (this._data[bytePos] >> (7 - (this._index & 7))) & 1;
    ++this._index;
    return bit;
  }

  /// <summary>Reads <paramref name="n"/> bits MSB-first (<c>get_bits</c>).</summary>
  public int GetBits(int n) {
    var value = 0;
    for (var i = 0; i < n; ++i)
      value = (value << 1) | this.GetBit();
    return value;
  }
}
