#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Codec.AmrWb;

/// <summary>
/// Faithful port of ffmpeg's lagged-Fibonacci generator <c>AVLFG</c> (libavutil/lfg.c). The
/// AMR-WB decoder seeds it with 1 and uses <see cref="Get"/> to build the high-band white-noise
/// excitation; reproducing it exactly keeps the high band bit-compatible with ffmpeg. The state is
/// initialised by MD5-hashing the seed into slots 8..63 (slots 0..7 stay zero, matching the
/// reference loop <c>for(i=8; i&lt;64; i+=4)</c>).
/// </summary>
internal sealed class AmrWbLfg {
  private readonly uint[] _state = new uint[64];
  private uint _index;

  public AmrWbLfg(uint seed) {
    // tmp is NOT reset between iterations: the reference (av_lfg_init) overwrites only the first
    // five bytes each pass and lets the rest carry the previous MD5 digest.
    var tmp = new byte[16];
    for (var i = 8; i < 64; i += 4) {
      BinaryPrimitives.WriteUInt32LittleEndian(tmp, seed);
      tmp[4] = (byte)i;
      var hash = MD5.HashData(tmp);
      Array.Copy(hash, tmp, 16);
      this._state[i] = BinaryPrimitives.ReadUInt32LittleEndian(tmp.AsSpan(0));
      this._state[i + 1] = BinaryPrimitives.ReadUInt32LittleEndian(tmp.AsSpan(4));
      this._state[i + 2] = BinaryPrimitives.ReadUInt32LittleEndian(tmp.AsSpan(8));
      this._state[i + 3] = BinaryPrimitives.ReadUInt32LittleEndian(tmp.AsSpan(12));
    }
    this._index = 0;
  }

  /// <summary>Next 32-bit value (ffmpeg <c>av_lfg_get</c>).</summary>
  public uint Get() {
    var a = this._state[this._index & 63] =
      this._state[(this._index - 24) & 63] + this._state[(this._index - 55) & 63];
    this._index += 1U;
    return a;
  }
}
