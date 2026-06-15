#pragma warning disable CS1591

namespace Codec.InterplayAcm;

/// <summary>
/// Least-significant-bit-first bit reader matching FFmpeg's <c>get_bits.h</c> when
/// compiled with <c>BITSTREAM_READER_LE</c> (which <c>interplayacm.c</c> does):
/// bits are consumed from bit 0 of each byte upward, low byte first. The reader is
/// tolerant of running past the end of the buffer — exhausted reads return zero
/// bits, mirroring the truncation tolerance the container needs for partial files.
/// </summary>
internal sealed class InterplayAcmBitReader {
  private readonly byte[] _data;
  private int _pos;
  private uint _cache;
  private int _bits;

  public InterplayAcmBitReader(byte[] data) => this._data = data;

  /// <summary>How many input bytes have been consumed (including any past-EOF reads).</summary>
  public int BytePosition => this._pos;

  /// <summary>Reads <paramref name="count"/> bits (0–32) LSB first; past EOF yields zeros.</summary>
  public uint GetBits(int count) {
    var result = 0u;
    var shift = 0;
    while (count > 0) {
      if (this._bits == 0) {
        if (this._pos >= this._data.Length) {
          // Truncated stream: behave as if the tail were zero-filled.
          ++this._pos;
          this._cache = 0;
        } else
          this._cache = this._data[this._pos++];
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

  /// <summary>Reads a single bit (LSB first).</summary>
  public uint GetBit() => this.GetBits(1);
}
