#pragma warning disable CS1591

namespace Codec.Vorbis;

/// <summary>
/// Vorbis IMDCT (inverse modified discrete cosine transform), windowing and
/// overlap-add. The Vorbis window is the canonical "Vorbis sin window":
/// w(n) = sin(π/2 · sin²(π·(n+0.5)/N)). Window edges adapt to the previous and
/// next block sizes when adjacent short/long block boundaries cross.
/// <para>
/// This implementation uses a direct O(N²) IMDCT for simplicity. Block sizes
/// in mainstream Vorbis files are 256 (short) and 2048 (long), so a single
/// long-block transform costs ~4M multiplies — fine for a reference decoder.
/// </para>
/// </summary>
internal static class VorbisImdct {

  /// <summary>
  /// Inverse MDCT: reads <paramref name="n"/>/2 frequency coefficients from
  /// <paramref name="freq"/> and writes <paramref name="n"/> time samples
  /// into <paramref name="time"/>.
  /// </summary>
  public static void Inverse(ReadOnlySpan<float> freq, Span<float> time, int n) {
    var half = n / 2;
    var scale = 2.0 / n;
    for (var k = 0; k < n; ++k) {
      double sum = 0;
      for (var i = 0; i < half; ++i) {
        var phase = Math.PI / n * ((k + 0.5 + half * 0.5) * (i * 2 + 1));
        sum += freq[i] * Math.Cos(phase);
      }
      time[k] = (float)(sum * scale);
    }
  }

  /// <summary>
  /// Build a Vorbis sine window of length <paramref name="n"/>, optionally
  /// shrinking the left and/or right halves when the surrounding blocks are
  /// short. Returns a freshly allocated array.
  /// </summary>
  public static float[] BuildWindow(int n, bool prevLong, bool nextLong, int shortN, int longN) {
    var w = new float[n];
    var half = n / 2;
    var leftWindowN = prevLong ? longN : shortN;
    var rightWindowN = nextLong ? longN : shortN;
    var leftN = leftWindowN / 2;
    var rightN = rightWindowN / 2;

    // Pre-window: zero from start to (half - leftN), then sin² ramp over leftN samples.
    var leftStart = half - leftN;
    for (var i = 0; i < leftStart; ++i) w[i] = 0f;
    for (var i = 0; i < leftN; ++i) {
      var arg = Math.PI / 2.0 * (i + 0.5) / leftN;
      var s = Math.Sin(arg);
      w[leftStart + i] = (float)Math.Sin(Math.PI / 2.0 * s * s);
    }

    // Centre — full amplitude across the constant section.
    for (var i = half; i < half + (half - rightN); ++i) w[i] = 1f;

    // Post-window: mirror sin² ramp over rightN, then zero to end.
    var rightStart = n - rightN;
    for (var i = 0; i < rightN; ++i) {
      var arg = Math.PI / 2.0 * (rightN - 0.5 - i) / rightN;
      var s = Math.Sin(arg);
      w[rightStart + i] = (float)Math.Sin(Math.PI / 2.0 * s * s);
    }
    return w;
  }
}
