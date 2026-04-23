#pragma warning disable CS1591

namespace Codec.Mp3;

/// <summary>
/// MSB-first bit reader over an in-memory buffer. Used for both the per-frame side
/// info and the (reservoir-joined) main data. Ported from minimp3's bs_t.
/// </summary>
internal sealed class Mp3BitReader {

  public readonly byte[] Buf;
  public int Pos;      // bit position within Buf (byte offset = Pos/8)
  public int Limit;    // total bit count (exclusive)

  public Mp3BitReader(byte[] buf, int byteCount) {
    this.Buf = buf;
    this.Pos = 0;
    this.Limit = byteCount * 8;
  }

  /// <summary>
  /// Reads the next <paramref name="n"/> bits (n ≤ 25 safe, n ≤ 32 OK for the main
  /// decoding paths) as an unsigned integer. Returns 0 when reading past the limit;
  /// callers detect truncation by comparing <see cref="Pos"/> against <see cref="Limit"/>.
  /// </summary>
  public uint GetBits(int n) {
    var s = this.Pos & 7;
    var shl = n + s;
    var bytePos = this.Pos >> 3;
    this.Pos += n;
    if (this.Pos > this.Limit) return 0;

    uint cache = 0;
    var b = (uint)this.Buf[bytePos++] & (uint)(255 >> s);
    while ((shl -= 8) > 0) {
      cache |= b << shl;
      b = this.Buf[bytePos++];
    }
    return cache | (b >> -shl);
  }
}
