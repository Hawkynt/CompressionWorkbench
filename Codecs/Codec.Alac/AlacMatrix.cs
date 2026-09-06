#pragma warning disable CS1591

namespace Codec.Alac;

/// <summary>
/// Inter-channel decorrelation for stereo pairs, following the reference
/// <c>matrix_dec.c</c> (<c>unmix16</c>/<c>unmix24</c>) and <c>matrix_enc.c</c>
/// (<c>mix16</c>/<c>mix24</c>).
/// <para>
/// There is no plain mid/side mode; instead a generalised weighted transform with the
/// weight <c>mixRes</c> over a denominator of <c>2^mixBits</c>:
/// <c>u = (mixRes*L + (2^mixBits - mixRes)*R) &gt;&gt; mixBits</c>, <c>v = L - R</c>, whose
/// exact inverse is <c>L = u + v - ((mixRes*v) &gt;&gt; mixBits)</c>, <c>R = L - v</c>.
/// A <c>mixRes</c> of zero does <em>not</em> mean "weight zero" — it selects a separate
/// path that leaves the two channels alone, which is also the path uncompressed
/// (escape) frames take.
/// </para>
/// </summary>
internal static class AlacMatrix {

  /// <summary>
  /// Un-mixes decorrelated channels <paramref name="u"/> and <paramref name="v"/> into
  /// <paramref name="left"/>/<paramref name="right"/>.
  /// </summary>
  public static void Unmix(
      int[] u, int[] v, int[] left, int[] right, int numSamples, int mixBits, int mixRes) {
    if (mixRes == 0) {
      Array.Copy(u, left, numSamples);
      Array.Copy(v, right, numSamples);
      return;
    }

    unchecked {
      for (var i = 0; i < numSamples; ++i) {
        var l = u[i] + v[i] - ((mixRes * v[i]) >> mixBits);
        left[i] = l;
        right[i] = l - v[i];
      }
    }
  }

  /// <summary>
  /// Mixes <paramref name="left"/>/<paramref name="right"/> into decorrelated channels
  /// <paramref name="u"/> and <paramref name="v"/>. The exact inverse of <see cref="Unmix"/>.
  /// </summary>
  public static void Mix(
      int[] left, int[] right, int[] u, int[] v, int numSamples, int mixBits, int mixRes) {
    if (mixRes == 0) {
      Array.Copy(left, u, numSamples);
      Array.Copy(right, v, numSamples);
      return;
    }

    var complement = (1 << mixBits) - mixRes;
    unchecked {
      for (var i = 0; i < numSamples; ++i) {
        var l = left[i];
        var r = right[i];
        u[i] = (mixRes * l + complement * r) >> mixBits;
        v[i] = l - r;
      }
    }
  }
}
