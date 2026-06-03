#pragma warning disable CS1591

namespace Codec.Alac;

/// <summary>
/// Inter-channel decorrelation matrix for stereo pairs — a port of Apple's
/// <c>matrix_dec.c</c> (<c>unmix20</c>/<c>unmix24</c> family) and
/// <c>matrix_enc.c</c> (<c>mix16</c>/<c>mix20</c>) from the open-source ALAC
/// reference. A pair is coded as a weighted mid/side: the left channel carries
/// <c>l = u + ((v * mixRes) &gt;&gt; mixBits)</c> rounded toward the side channel, and the
/// right channel is <c>r = l - v</c>. With <c>mixRes == 0</c> the pair is plain
/// left/right (the encoder's default). The transform is integer-exact and its own
/// inverse, so a CPE round-trips losslessly.
/// </summary>
internal static class AlacMatrix {

  /// <summary>
  /// Un-mixes decorrelated channels <paramref name="u"/> (mid) and <paramref name="v"/> (side)
  /// into interleaved left/right written to <paramref name="left"/>/<paramref name="right"/>.
  /// </summary>
  public static void Unmix(int[] u, int[] v, int[] left, int[] right, int numSamples, int mixBits, int mixRes) {
    if (mixRes == 0) {
      Array.Copy(u, left, numSamples);
      Array.Copy(v, right, numSamples);
      return;
    }

    for (var i = 0; i < numSamples; ++i) {
      var l = u[i] + v[i] - ((v[i] * mixRes) >> mixBits);
      var r = l - v[i];
      left[i] = l;
      right[i] = r;
    }
  }

  /// <summary>
  /// Mixes interleaved left/right into decorrelated channels <paramref name="u"/> (mid) and
  /// <paramref name="v"/> (side). The exact inverse of <see cref="Unmix"/>.
  /// </summary>
  public static void Mix(int[] left, int[] right, int[] u, int[] v, int numSamples, int mixBits, int mixRes) {
    if (mixRes == 0) {
      Array.Copy(left, u, numSamples);
      Array.Copy(right, v, numSamples);
      return;
    }

    for (var i = 0; i < numSamples; ++i) {
      var l = left[i];
      var r = right[i];
      v[i] = l - r;
      u[i] = r + ((v[i] * mixRes) >> mixBits);
    }
  }
}
