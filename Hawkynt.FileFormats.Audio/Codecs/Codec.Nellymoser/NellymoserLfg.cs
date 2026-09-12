#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Codec.Nellymoser;

/// <summary>
/// Lagged-Fibonacci generator, a faithful port of FFmpeg's <c>AVLFG</c>
/// (<c>libavutil/lfg.c</c>). The decoder seeds it with 0 and uses one bit of each
/// draw to randomise the sign of noise-filled spectral lines, so reproducing the
/// generator exactly is what makes a zero/seeded decode deterministic across runs.
/// The 64-word state is initialised by MD5-hashing the seed in blocks, matching
/// <c>av_lfg_init</c>.
/// </summary>
internal sealed class NellymoserLfg {
  private readonly uint[] _state = new uint[64];
  private int _index;

  public NellymoserLfg(uint seed) {
    var tmp = new byte[16];
    for (var i = 8; i < 64; i += 4) {
      Array.Clear(tmp);
      BinaryPrimitives.WriteUInt32LittleEndian(tmp, seed);
      tmp[4] = (byte)i;
      tmp = MD5.HashData(tmp);
      this._state[i] = BinaryPrimitives.ReadUInt32LittleEndian(tmp);
      this._state[i + 1] = BinaryPrimitives.ReadUInt32LittleEndian(tmp.AsSpan(4));
      this._state[i + 2] = BinaryPrimitives.ReadUInt32LittleEndian(tmp.AsSpan(8));
      this._state[i + 3] = BinaryPrimitives.ReadUInt32LittleEndian(tmp.AsSpan(12));
    }
    this._index = 0;
  }

  /// <summary>av_lfg_get: state[i] = state[i-24] + state[i-55] (mod 2^32), then advance.</summary>
  public uint Get() {
    var a = this._state[this._index & 63] =
      this._state[(this._index - 24) & 63] + this._state[(this._index - 55) & 63];
    this._index += 1;
    return a;
  }
}
