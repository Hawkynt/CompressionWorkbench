#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// Stereo joint-coding tools: M/S (mid/side) coupling and intensity stereo
/// (ISO/IEC 14496-3 §4.6.8). Both operate on per-scalefactor-band granularity.
/// </summary>
internal static class AacStereo {

  /// <summary>Inverts mid/side stereo for a single CPE (channel-pair element).</summary>
  public static void ApplyMidSide(
    float[] left, float[] right, bool[] msUsedPerSfb, int[] sfbOffsets, int maxSfb) {
    if (msUsedPerSfb is null || sfbOffsets is null) return;
    for (var sfb = 0; sfb < maxSfb && sfb < msUsedPerSfb.Length; ++sfb) {
      if (!msUsedPerSfb[sfb]) continue;
      var start = sfbOffsets[sfb];
      var end = sfbOffsets[sfb + 1];
      for (var k = start; k < end; ++k) {
        var l = left[k];
        var r = right[k];
        left[k] = l + r;
        right[k] = l - r;
      }
    }
  }

  /// <summary>
  /// Applies intensity stereo to the right channel using scalefactors signalled
  /// for codebooks 14 (IS_INTENSITY) and 15 (IS_INTENSITY with sign inversion).
  /// </summary>
  public static void ApplyIntensity(
    float[] left, float[] right, int[] codebooks, float[] scalefactors,
    int[] sfbOffsets, int maxSfb, bool msMaskPresent, bool[] msUsedPerSfb) {
    _ = left; _ = right; _ = codebooks; _ = scalefactors;
    _ = sfbOffsets; _ = maxSfb; _ = msMaskPresent; _ = msUsedPerSfb;
    throw new NotSupportedException(
      "AAC intensity stereo not yet implemented. ISO/IEC 14496-3 §4.6.8.2.3 " +
      "describes the cb=14/15 sign rule combined with the m/s mask for sign inversion.");
  }
}
