#pragma warning disable CS1591
namespace Codec.AdpcmX;

/// <summary>
/// DERF DPCM (ffmpeg <c>derf_dpcm</c> in <c>libavcodec/dpcm.c</c>, the audio in Xilam DERF
/// <c>.adp</c> streams). Each payload byte splits into a sign bit (0x80) and a 7-bit magnitude that
/// indexes a 96-entry step table (clamped to 95); the signed step is added to the running
/// per-channel predictor. Channels are interleaved byte-by-byte.
/// </summary>
public static class Derf {

  /// <summary>The 96-entry step table (ffmpeg <c>derf_steps</c>), ported verbatim.</summary>
  public static readonly int[] Steps = [
    0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 16,
    17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73,
    80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337,
    371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552,
    1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132,
    7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
  ];

  /// <summary>
  /// Decodes a DERF payload into interleaved PCM16. <paramref name="channels"/> is 1 or 2; each
  /// channel keeps its own predictor and they are interleaved byte-by-byte.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data, int channels) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("DERF supports 1 or 2 channels.", nameof(channels));

    var predictor = new int[channels];
    var output = new short[data.Length];
    var ch = 0;
    for (var i = 0; i < data.Length; ++i) {
      var n = data[i];
      var index = Math.Min(n & 0x7F, 95);
      var step = (n & 0x80) != 0 ? -Steps[index] : Steps[index];
      predictor[ch] = ImaCore.Clamp16(predictor[ch] + step);
      output[i] = (short)predictor[ch];
      ch = channels == 1 ? 0 : ch ^ 1;
    }

    return output;
  }
}
