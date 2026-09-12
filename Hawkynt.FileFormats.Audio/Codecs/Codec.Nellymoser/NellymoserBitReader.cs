#pragma warning disable CS1591

namespace Codec.Nellymoser;

/// <summary>
/// Least-significant-bit-first bit reader matching FFmpeg's <c>get_bits.h</c> with
/// <c>BITSTREAM_READER_LE</c> (which <c>nellymoserdec.c</c> sets). Reads from a
/// 64-byte block; past-end reads return zero bits so a truncated final block still
/// decodes deterministically.
/// </summary>
internal sealed class NellymoserBitReader {
  private readonly byte[] _data;
  private int _pos;
  private uint _cache;
  private int _bits;

  public NellymoserBitReader(ReadOnlySpan<byte> data) => this._data = data.ToArray();

  /// <summary>Reads <paramref name="count"/> bits (0–32) LSB first; past EOF yields zeros.</summary>
  public uint GetBits(int count) {
    var result = 0u;
    var shift = 0;
    while (count > 0) {
      if (this._bits == 0) {
        this._cache = this._pos < this._data.Length ? this._data[this._pos] : 0u;
        ++this._pos;
        this._bits = 8;
      }
      var take = Math.Min(this._bits, count);
      var mask = take == 32 ? 0xFFFFFFFFu : (1u << take) - 1;
      result |= (this._cache & mask) << shift;
      this._cache >>= take;
      this._bits -= take;
      shift += take;
      count -= take;
    }
    return result;
  }

  /// <summary>Skips <paramref name="count"/> bits forward.</summary>
  public void SkipBits(int count) {
    while (count > 0) {
      var take = Math.Min(count, 24);
      this.GetBits(take);
      count -= take;
    }
  }
}
