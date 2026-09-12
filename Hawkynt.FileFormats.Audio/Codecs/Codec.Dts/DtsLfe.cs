#pragma warning disable CS1591

namespace Codec.Dts;

/// <summary>
/// LFE decimation-FIR interpolation for the DCA core, a faithful port of FFmpeg's
/// <c>dca_lfe_fir</c> / <c>lfe_interpolation_fir</c>. Each decimated LFE sample expands to
/// 2 × decifactor interpolated samples through the symmetric prototype FIR
/// (<see cref="DtsTables.LfeFir64"/> for LFF=2 / decifactor 32, <see cref="DtsTables.LfeFir128"/>
/// for LFF=1 / decifactor 64). History samples sit at negative indices relative to the current
/// subframe's first decimated sample, as in the reference.
/// </summary>
internal static class DtsLfe {

  /// <summary>
  /// Interpolates <paramref name="lfe"/> (1 or 2) groups of decimated samples. <paramref name="lfeData"/>
  /// holds the decimated stream; <paramref name="baseIndex"/> is the index of this block's first
  /// decimated sample (history precedes it). Writes the interpolated PCM into <paramref name="output"/>
  /// starting at <paramref name="outStart"/>.
  /// </summary>
  public static void Interpolate(int lfe, float[] lfeData, int baseIndex, float[] output, int outStart) {
    // decifactor: LFF=1 → 64 (lfe_fir_128), LFF=2 → 32 (lfe_fir_64).
    var prCoeff = lfe == 1 ? DtsTables.LfeFir128 : DtsTables.LfeFir64;
    var decifactor = lfe == 1 ? 64 : 32;

    var inIndex = baseIndex;
    var outPos = outStart;
    for (var deci = 0; deci < 2 * lfe; ++deci) {
      Fir(output, outPos, lfeData, inIndex, prCoeff, decifactor);
      ++inIndex;
      outPos += 2 * 32 * (lfe == 1 ? 2 : 1);
    }
  }

  // out[k] / out2[k]: one decimated sample (at lfeData[in]) generates 2*decifactor interpolated ones.
  private static void Fir(float[] output, int outBase, float[] lfeData, int inIndex,
                          float[] coefs, int decifactor) {
    var numCoeffs = 256 / decifactor;
    var out2 = outBase + 2 * decifactor - 1;
    var coefPos = 0;
    for (var k = 0; k < decifactor; ++k) {
      var v0 = 0f;
      var v1 = 0f;
      for (var j = 0; j < numCoeffs; ++j, ++coefPos) {
        v0 += Sample(lfeData, inIndex - j) * coefs[coefPos];
        v1 += Sample(lfeData, inIndex + j + 1 - numCoeffs) * coefs[coefPos];
      }
      output[outBase + k] = v0;
      output[out2 - k] = v1;
    }
  }

  private static float Sample(float[] data, int index)
    => index >= 0 && index < data.Length ? data[index] : 0f;
}
