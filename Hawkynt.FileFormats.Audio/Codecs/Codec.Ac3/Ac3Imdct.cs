#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// Inverse modified discrete cosine transform for AC-3 (ATSC A/52 §7.9). Each audio block carries
/// 256 frequency coefficients per channel. With <c>blksw=0</c> a single 512-point IMDCT is applied
/// (§7.9.4.1); with <c>blksw=1</c> the coefficients de-interleave into two 128-coefficient sets, each
/// driving a 256-point IMDCT (§7.9.4.2). Either way the result is a 512-sample windowed sequence
/// <c>x[]</c> whose first half overlap-adds with the previous block's second half:
/// <c>pcm[n] = 2 * (x[n] + delay[n])</c>, the factor of two undoing the encoder's headroom scaling.
/// <para>
/// The spec states the transform as a pre-twiddle / complex IFFT / post-twiddle / de-interleave
/// chain; this implementation evaluates the equivalent direct cosine sum, which was checked term by
/// term against the spec's factorisation for both block lengths. The direct form is O(N²) but the
/// per-channel extraction use case is not performance critical.
/// </para>
/// </summary>
public static class Ac3Imdct {

  private const int Coefficients = 256;   // transform coefficients per block
  private const int BlockSamples = 256;   // new PCM samples produced per block
  private const int WindowSamples = 512;  // full windowed sequence length

  // Long block (§7.9.4.1): x[n] = -sum_k X[k] * cos(pi/1024 * (2n + 1 + 256) * (2k + 1)).
  private static readonly float[] LongBasis = BuildLongBasis();

  // Short block (§7.9.4.2): both 128-coefficient halves use one kernel,
  // S(C, m) = -sum_k C[k] * cos(pi/512 * (2m + 1) * (2k + 1)); the even-indexed coefficients are
  // evaluated over m = 0..255 and the odd-indexed ones over m = 128..383, so the table runs to 384.
  private const int ShortRows = 384;
  private static readonly float[] ShortBasis = BuildShortBasis();

  private static float[] BuildLongBasis() {
    var table = new float[WindowSamples * Coefficients];
    for (var n = 0; n < WindowSamples; ++n)
      for (var k = 0; k < Coefficients; ++k)
        table[n * Coefficients + k] =
          (float)-Math.Cos(Math.PI / 1024.0 * (2 * n + 1 + 256) * (2 * k + 1));
    return table;
  }

  private static float[] BuildShortBasis() {
    const int half = Coefficients / 2;
    var table = new float[ShortRows * half];
    for (var m = 0; m < ShortRows; ++m)
      for (var k = 0; k < half; ++k)
        table[m * half + k] = (float)-Math.Cos(Math.PI / 512.0 * (2 * m + 1) * (2 * k + 1));
    return table;
  }

  /// <summary>
  /// Long-block (512-point) IMDCT + window + overlap-add. <paramref name="coeffs"/> holds the 256
  /// transform coefficients; <paramref name="delay"/> is the 256-sample overlap memory (updated in
  /// place); the 256 reconstructed samples are written to <paramref name="output"/>.
  /// </summary>
  public static void Long(float[] coeffs, float[] delay, float[] output) {
    var x = new float[WindowSamples];
    for (var n = 0; n < WindowSamples; ++n) {
      var row = n * Coefficients;
      double sum = 0;
      for (var k = 0; k < Coefficients; ++k)
        sum += coeffs[k] * LongBasis[row + k];
      x[n] = (float)sum;
    }
    WindowAndOverlap(x, delay, output);
  }

  /// <summary>
  /// Short-block (dual 256-point) IMDCT + window + overlap-add. The even-indexed coefficients build
  /// the first half of the windowed sequence and the odd-indexed ones the second half; the
  /// overlap-add that follows is identical to the long-block case.
  /// </summary>
  public static void Short(float[] coeffs, float[] delay, float[] output) {
    const int half = Coefficients / 2;
    var x = new float[WindowSamples];
    for (var m = 0; m < BlockSamples; ++m) {
      var rowFirst = m * half;
      var rowSecond = (m + half) * half;
      double first = 0, second = 0;
      for (var k = 0; k < half; ++k) {
        first += coeffs[2 * k] * ShortBasis[rowFirst + k];
        second += coeffs[2 * k + 1] * ShortBasis[rowSecond + k];
      }
      x[m] = (float)first;
      x[BlockSamples + m] = (float)second;
    }
    WindowAndOverlap(x, delay, output);
  }

  // A/52 §7.9.4 steps 5 and 6. Only the rising half of the 512-point window is tabulated; the
  // falling half is its mirror, w[511 - n] = w[n].
  private static void WindowAndOverlap(float[] x, float[] delay, float[] output) {
    var w = Ac3Tables.Window;
    for (var n = 0; n < BlockSamples; ++n) {
      var head = x[n] * w[n];
      var tail = x[BlockSamples + n] * w[BlockSamples - 1 - n];
      output[n] = 2f * (head + delay[n]);
      delay[n] = tail;
    }
  }
}
