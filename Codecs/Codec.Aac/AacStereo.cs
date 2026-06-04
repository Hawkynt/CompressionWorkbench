#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// Stereo joint-coding tools: M/S (mid/side) coupling and intensity stereo
/// (ISO/IEC 14496-3 §4.6.8). Both operate on per-scalefactor-band granularity and
/// run over the grouped/windowed layout of a CPE.
/// </summary>
internal static class AacStereo {

  /// <summary>
  /// Inverts mid/side stereo for a CPE: where <c>ms_used</c> is set for a band and
  /// neither channel uses intensity, replace (L,R) = (M+S, M−S).
  /// </summary>
  public static void ApplyMidSide(
    float[] left, float[] right, IcsInfo ics, bool msMaskAllOn, bool[][]? msUsed, int[][] rightCodebooks) {
    var groupWindowStart = 0;
    var groupBins = ics.IsEightShort ? AacFilterBank.ShortFrameSize : AacFilterBank.LongFrameSize;
    for (var g = 0; g < ics.WindowGroupCount; ++g) {
      var windowsInGroup = ics.WindowGroupLength[g];
      for (var sfb = 0; sfb < ics.MaxSfb; ++sfb) {
        var rcb = rightCodebooks[g][sfb];
        if (rcb is AacHuffmanTables.IntensityHcb or AacHuffmanTables.IntensityHcb2)
          continue; // intensity bands are not M/S coded
        var on = msMaskAllOn || (msUsed is not null && msUsed[g][sfb]);
        if (!on) continue;
        var sfbStart = ics.SwbOffset[sfb];
        var sfbEnd = ics.SwbOffset[sfb + 1];
        for (var w = 0; w < windowsInGroup; ++w) {
          var baseBin = (groupWindowStart + w) * groupBins;
          for (var k = sfbStart; k < sfbEnd; ++k) {
            var m = left[baseBin + k];
            var s = right[baseBin + k];
            left[baseBin + k] = m + s;
            right[baseBin + k] = m - s;
          }
        }
      }
      groupWindowStart += windowsInGroup;
    }
  }

  /// <summary>
  /// Applies intensity stereo to the right channel. For bands coded with cb 14/15
  /// the right spectrum is a scaled copy of the left, the scale being
  /// <c>0.5^(is_position/4)</c> from the right channel's "scale factors" stream;
  /// cb 15 (and the ms mask, when present) flips the sign (ISO/IEC 14496-3 §4.6.8.2.3).
  /// </summary>
  public static void ApplyIntensity(
    float[] left, float[] right, IcsInfo ics,
    int[][] rightCodebooks, int[][] rightScaleFactors,
    bool msMaskPresent, bool[][]? msUsed) {
    var groupWindowStart = 0;
    var groupBins = ics.IsEightShort ? AacFilterBank.ShortFrameSize : AacFilterBank.LongFrameSize;
    for (var g = 0; g < ics.WindowGroupCount; ++g) {
      var windowsInGroup = ics.WindowGroupLength[g];
      for (var sfb = 0; sfb < ics.MaxSfb; ++sfb) {
        var cb = rightCodebooks[g][sfb];
        if (cb is not (AacHuffmanTables.IntensityHcb or AacHuffmanTables.IntensityHcb2))
          continue;
        // cb 15 = positive intensity, cb 14 = sign-inverted; the ms mask flips it again.
        var sign = cb == AacHuffmanTables.IntensityHcb ? 1.0f : -1.0f;
        if (msMaskPresent && msUsed is not null && msUsed[g][sfb])
          sign = -sign;
        var scale = sign * MathF.Pow(0.5f, rightScaleFactors[g][sfb] / 4f);
        var sfbStart = ics.SwbOffset[sfb];
        var sfbEnd = ics.SwbOffset[sfb + 1];
        for (var w = 0; w < windowsInGroup; ++w) {
          var baseBin = (groupWindowStart + w) * groupBins;
          for (var k = sfbStart; k < sfbEnd; ++k)
            right[baseBin + k] = scale * left[baseBin + k];
        }
      }
      groupWindowStart += windowsInGroup;
    }
  }
}
