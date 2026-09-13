#pragma warning disable CS1591

namespace Codec.WmaLossless;

/// <summary>
/// MSB-first bit reader mirroring FFmpeg's <c>GetBitContext</c> (<c>get_bits.h</c>) as
/// used by the WMA Lossless decoder. Bits are consumed from the most-significant bit of
/// each byte; reads past the configured limit return zero-extended values while
/// <see cref="BitsLeft"/> goes negative, which the decode loop uses to detect overread.
/// Exposes the buffer-origin bit count (<see cref="BitsCount"/>), signed reads
/// (<see cref="GetSignedBits"/>/<see cref="GetSignedBitsLong"/>), wide reads
/// (<see cref="GetBitsLong"/>) and non-advancing look-ahead (<see cref="ShowBits"/>).
/// </summary>
internal sealed class WmallBitReader {

  private readonly byte[] _data;
  private int _index;
  private readonly int _limitBits;

  public WmallBitReader(byte[] data, int byteOffset, int bitLength) {
    this._data = data;
    this._index = byteOffset * 8;
    this._limitBits = byteOffset * 8 + bitLength;
  }

  /// <summary>Absolute bit position from the start of the backing buffer.</summary>
  public int BitsCount {
    get => this._index;
    set => this._index = value;
  }

  /// <summary>Signed count of bits remaining before the limit (negative once overread).</summary>
  public int BitsLeft => this._limitBits - this._index;

  /// <summary>Reads <paramref name="n"/> bits (0..32) MSB-first, zero-extending past the limit.</summary>
  public uint GetBits(int n) {
    if (n == 0) return 0;
    uint result = 0;
    for (var i = 0; i < n; ++i) {
      uint bit = 0;
      if (this._index < this._limitBits) {
        var bytePos = this._index >> 3;
        if (bytePos < this._data.Length)
          bit = (uint)((this._data[bytePos] >> (7 - (this._index & 7))) & 1);
      }
      result = (result << 1) | bit;
      ++this._index;
    }
    return result;
  }

  /// <summary>Reads a single bit.</summary>
  public uint GetBit() => this.GetBits(1);

  /// <summary>FFmpeg <c>get_bitsz</c>: returns 0 for n == 0, else <see cref="GetBits"/>.</summary>
  public uint GetBitsZ(int n) => n == 0 ? 0 : this.GetBits(n);

  /// <summary>Reads up to 32 bits (FFmpeg <c>get_bits_long</c>).</summary>
  public uint GetBitsLong(int n) => this.GetBits(n);

  /// <summary>Reads <paramref name="n"/> bits as a sign-extended value (FFmpeg <c>get_sbits</c>).</summary>
  public int GetSignedBits(int n) {
    if (n == 0) return 0;
    var v = (int)this.GetBits(n);
    var shift = 32 - n;
    return (v << shift) >> shift;
  }

  /// <summary>FFmpeg <c>get_sbits_long</c>: sign-extends an n-bit value read as up to 33 bits.</summary>
  public int GetSignedBitsLong(int n) {
    if (n == 0) return 0;
    if (n <= 32) {
      var v = (long)this.GetBits(n);
      var shift = 64 - n;
      return (int)((v << shift) >> shift);
    }
    // n == bits_per_sample + 1 can reach 33 for 32-bit samples; read in two halves.
    var hi = this.GetBits(n - 32);
    var lo = this.GetBits(32);
    var combined = ((long)hi << 32) | lo;
    var sh = 64 - n;
    return (int)((combined << sh) >> sh);
  }

  /// <summary>Peeks <paramref name="n"/> bits without advancing (FFmpeg <c>show_bits</c>).</summary>
  public uint ShowBits(int n) {
    var save = this._index;
    var v = this.GetBits(n);
    this._index = save;
    return v;
  }

  /// <summary>Skips <paramref name="n"/> bits (n may exceed 32; FFmpeg <c>skip_bits_long</c>).</summary>
  public void SkipBits(int n) => this._index += n;
}
