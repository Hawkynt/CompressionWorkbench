#pragma warning disable CS1591

using System.Security.Cryptography;

namespace Codec.Musepack;

/// <summary>
/// Lagged-Fibonacci pseudo-random generator, a faithful port of FFmpeg's
/// <c>AVLFG</c> (<c>libavutil/lfg.c</c>/<c>lfg.h</c>). The Musepack SV8 decoder
/// seeds one with <c>0xDEADBEEF</c> and uses it to fill the noise (<c>res == -1</c>)
/// subbands, so the generator's exact sequence — including the MD5-based state
/// init — must match for those bands to decode identically.
/// </summary>
internal sealed class MpcRandom {
  private readonly uint[] _state = new uint[64];
  private uint _index;

  public MpcRandom(uint seed) {
    var tmp = new byte[16];
    for (var i = 8; i < 64; i += 4) {
      Array.Clear(tmp);
      tmp[0] = (byte)(seed & 0xFF);
      tmp[1] = (byte)((seed >> 8) & 0xFF);
      tmp[2] = (byte)((seed >> 16) & 0xFF);
      tmp[3] = (byte)((seed >> 24) & 0xFF);
      tmp[4] = (byte)i;
      var digest = MD5.HashData(tmp);
      this._state[i + 0] = ReadLe32(digest, 0);
      this._state[i + 1] = ReadLe32(digest, 4);
      this._state[i + 2] = ReadLe32(digest, 8);
      this._state[i + 3] = ReadLe32(digest, 12);
    }
    this._index = 0;
  }

  /// <summary>Returns the next 32-bit value, equivalent to <c>av_lfg_get</c>.</summary>
  public uint Next() {
    var a = this._state[this._index & 63] =
      this._state[(this._index - 24) & 63] + this._state[(this._index - 55) & 63];
    this._index += 1U;
    return a;
  }

  private static uint ReadLe32(byte[] data, int offset)
    => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
}
