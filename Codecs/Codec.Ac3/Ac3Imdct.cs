#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// Inverse modified discrete cosine transform for AC-3 (ATSC A/52 §7.9). Each audio block carries
/// 256 frequency coefficients per channel. With <c>blksw=0</c> a single 512-point IMDCT is applied;
/// with <c>blksw=1</c> the 256 coefficients split into two interleaved 256-point IMDCTs. The
/// transform output is windowed with the A/52 window and overlap-added against the previous block's
/// tail to yield 256 PCM time samples per block per channel. This implementation uses the direct
/// O(N²) summation form of the IMDCT, which is exact and matches the spec equations; performance is
/// not a concern for the per-channel extraction use case.
/// </summary>
public static class Ac3Imdct {

  private const int N = 256;     // coefficients per block
  private const double Pi = Math.PI;

  /// <summary>
  /// Long-block (512-point) IMDCT + window + overlap-add. <paramref name="coeffs"/> holds the 256
  /// transform coefficients; <paramref name="delay"/> is the 256-sample overlap memory (updated in
  /// place); the 256 reconstructed PCM samples are written to <paramref name="output"/>.
  /// </summary>
  public static void Long(float[] coeffs, float[] delay, float[] output) {
    // 512-point IMDCT: x[n] = sum_{k=0}^{255} X[k] cos( (pi/512)(2n+1+256)(2k+1) ), n=0..511.
    // Split the 512 outputs into the first half (windowed + delay → PCM) and the second half
    // (windowed → next delay), exactly as A/52 §7.9.4 specifies.
    var tmp = new float[2 * N];
    for (var n = 0; n < 2 * N; ++n) {
      double sum = 0;
      for (var k = 0; k < N; ++k)
        sum += coeffs[k] * Math.Cos(Pi / (2 * N) * (2 * n + 1 + N) * (2 * k + 1));
      tmp[n] = (float)sum;
    }

    var w = Ac3Tables.Window;
    for (var n = 0; n < N; ++n) {
      // First half windowed by w, second half windowed by w reversed.
      var a = tmp[n] * w[n];
      output[n] = a + delay[n];
      delay[n] = tmp[N + n] * w[N - 1 - n];
    }
  }

  /// <summary>
  /// Short-block (dual 256-point) IMDCT + window + overlap-add. The 256 coefficients are
  /// de-interleaved into two 128-coefficient sub-blocks; each drives a 256-point IMDCT. The two
  /// 256-sample windowed outputs are concatenated and overlap-added with <paramref name="delay"/>.
  /// </summary>
  public static void Short(float[] coeffs, float[] delay, float[] output) {
    const int half = N / 2;       // 128
    // De-interleave: even-indexed coefficients form sub-block 0, odd form sub-block 1.
    var c0 = new float[half];
    var c1 = new float[half];
    for (var k = 0; k < half; ++k) {
      c0[k] = coeffs[2 * k];
      c1[k] = coeffs[2 * k + 1];
    }

    var x0 = Imdct256(c0);
    var x1 = Imdct256(c1);

    var w = Ac3Tables.Window;
    // First 256 windowed output samples come from sub-block 0 windowed + delay.
    for (var n = 0; n < N; ++n)
      output[n] = x0[n] * w[n] + delay[n];

    // Build next delay: tail of sub-block 0 plus head of sub-block 1, per A/52 short-block layout.
    var newDelay = new float[N];
    for (var n = 0; n < N; ++n)
      newDelay[n] = x1[n] * w[N - 1 - n];
    Array.Copy(newDelay, delay, N);
  }

  // 256-point IMDCT producing 256 time samples from 128 coefficients (direct form).
  private static float[] Imdct256(float[] coeffs) {
    const int half = N / 2;       // 128
    var outp = new float[N];
    for (var n = 0; n < N; ++n) {
      double sum = 0;
      for (var k = 0; k < half; ++k)
        sum += coeffs[k] * Math.Cos(Pi / N * (2 * n + 1 + half) * (2 * k + 1));
      outp[n] = (float)sum;
    }
    return outp;
  }
}
