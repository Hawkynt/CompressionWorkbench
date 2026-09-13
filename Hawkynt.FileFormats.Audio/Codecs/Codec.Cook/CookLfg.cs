#pragma warning disable CS1591
using System.Security.Cryptography;

namespace Codec.Cook;

/// <summary>
/// Lagged Fibonacci PRNG, a faithful port of FFmpeg's <c>AVLFG</c> (<c>av_lfg_init</c> /
/// <c>av_lfg_get</c> in <c>libavutil/lfg.c</c>). Cook seeds it with 0 and uses
/// <c>av_lfg_get</c> only to pick the sign of dither (noise-coded) coefficients, so the
/// exact state evolution must match. The seeding writes the little-endian seed plus a
/// counter byte into a 16-byte block and runs it through MD5 in place for slots 8..63
/// (four <c>state</c> words per MD5 round), exactly as the reference does.
/// </summary>
internal sealed class CookLfg {
  private readonly uint[] _state = new uint[64];
  private int _index;

  public CookLfg(uint seed) {
    var tmp = new byte[16];
    for (var i = 8; i < 64; i += 4) {
      // AV_WL32(tmp, seed)
      tmp[0] = (byte)seed;
      tmp[1] = (byte)(seed >> 8);
      tmp[2] = (byte)(seed >> 16);
      tmp[3] = (byte)(seed >> 24);
      tmp[4] = (byte)i;
      // av_md5_sum(tmp, tmp, 16) — overwrites tmp with the 16-byte digest.
      tmp = MD5.HashData(tmp);
      this._state[i] = ReadLe32(tmp, 0);
      this._state[i + 1] = ReadLe32(tmp, 4);
      this._state[i + 2] = ReadLe32(tmp, 8);
      this._state[i + 3] = ReadLe32(tmp, 12);
    }
    this._index = 0;
  }

  /// <summary>Next 32-bit value (<c>av_lfg_get</c>).</summary>
  public uint Get() {
    var a = this._state[this._index & 63] =
      this._state[(this._index - 24) & 63] + this._state[(this._index - 55) & 63];
    this._index += 1;
    return a;
  }

  private static uint ReadLe32(byte[] b, int o)
    => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
}
