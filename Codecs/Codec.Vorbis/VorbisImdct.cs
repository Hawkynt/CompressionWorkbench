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
  /// The four boundaries of a Vorbis window: zero on <c>[0, LeftStart)</c>, rising over
  /// <c>[LeftStart, LeftEnd)</c>, unity on <c>[LeftEnd, RightStart)</c>, falling over
  /// <c>[RightStart, RightEnd)</c> and zero again on <c>[RightEnd, n)</c>.
  /// </summary>
  public readonly record struct WindowRegions(int LeftStart, int LeftEnd, int RightStart, int RightEnd);

  /// <summary>
  /// Boundaries for a block of <paramref name="n"/> samples. A long block that meets a short
  /// one laps over only <c>shortN / 2</c> samples, and that slope sits centred on the quarter
  /// point rather than filling the half — which is what makes the returned PCM run
  /// <c>(previous n + this n) / 4</c> samples long instead of a flat <c>n / 2</c>.
  /// </summary>
  public static WindowRegions Regions(int n, bool blockLong, bool prevLong, bool nextLong, int shortN) {
    if (!blockLong)
      return new WindowRegions(0, n / 2, n / 2, n);

    var lap = shortN / 4;
    var leftStart = prevLong ? 0 : n / 4 - lap;
    var leftEnd = prevLong ? n / 2 : n / 4 + lap;
    var rightStart = nextLong ? n / 2 : 3 * n / 4 - lap;
    var rightEnd = nextLong ? n : 3 * n / 4 + lap;
    return new WindowRegions(leftStart, leftEnd, rightStart, rightEnd);
  }

  /// <summary>
  /// Build the Vorbis sine window for the given <paramref name="regions"/>. Returns a freshly
  /// allocated array of length <paramref name="n"/>.
  /// </summary>
  public static float[] BuildWindow(int n, WindowRegions regions) {
    var w = new float[n];
    var (leftStart, leftEnd, rightStart, rightEnd) = regions;

    var leftN = leftEnd - leftStart;
    for (var i = 0; i < leftN; ++i)
      w[leftStart + i] = Slope(i, leftN);

    for (var i = leftEnd; i < rightStart; ++i) w[i] = 1f;

    var rightN = rightEnd - rightStart;
    for (var i = 0; i < rightN; ++i)
      w[rightStart + i] = Slope(rightN - 1 - i, rightN);

    return w;
  }

  // w(i) = sin(pi/2 * sin^2(pi/2 * (i + 0.5) / length)) - the Vorbis slope, which squares and
  // sums to one against its mirror so the overlap-add reconstructs exactly.
  private static float Slope(int i, int length) {
    var s = Math.Sin(Math.PI / 2.0 * (i + 0.5) / length);
    return (float)Math.Sin(Math.PI / 2.0 * s * s);
  }
}
