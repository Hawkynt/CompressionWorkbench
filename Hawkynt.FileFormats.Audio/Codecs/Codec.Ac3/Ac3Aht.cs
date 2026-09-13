#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// Adaptive-Hybrid-Transform (AHT) arithmetic for E-AC-3 (ATSC A/52 Annex E §E.2.3.2). AHT codes the
/// six audio blocks of a channel jointly: each transform bin carries six pre-mantissas — decoded by
/// vector quantization (small hebap) or gain-adaptive quantization (GAQ, large hebap) — that are then
/// run through a 6-point inverse DCT to recover the per-block mantissa. This type isolates the two
/// numeric kernels (<see cref="Idct6"/> and <see cref="GaqDequant"/>) so they can be cross-checked in
/// isolation against the FFmpeg reference (<c>eac3dec.c</c>).
/// </summary>
public static class Ac3Aht {

  // 6-point inverse DCT coefficients (FFmpeg idct6), all 24-bit fixed-point.
  private const long Coeff0 = 10273905L;        // lrint(M_SQRT2*cos(2*pi/12)*(1<<23))
  private const long Coeff1 = 11863283L;        // lrint(M_SQRT2*cos(0*pi/12)*(1<<23)) = sqrt2<<23
  private const long Coeff2 = 3070444L;         // lrint(M_SQRT2*cos(5*pi/12)*(1<<23))

  /// <summary>
  /// In-place 6-point inverse DCT of the six per-block pre-mantissas (FFmpeg <c>idct6</c>). A pure
  /// DC input (only index 0 non-zero) spreads the constant across all six outputs.
  /// </summary>
  public static void Idct6(int[] pm) {
    var odd1 = pm[1] - pm[3] - pm[5];

    var even2 = (int)((pm[2] * Coeff0) >> 23);
    var tmp = (int)((pm[4] * Coeff1) >> 23);
    var odd0 = (int)(((pm[1] + pm[5]) * Coeff2) >> 23);

    var even0 = pm[0] + (tmp >> 1);
    var even1 = pm[0] - tmp;

    tmp = even0;
    even0 = tmp + even2;
    even2 = tmp - even2;

    tmp = odd0;
    odd0 = tmp + pm[1] + pm[3];
    var odd2 = tmp + pm[5] - pm[3];

    pm[0] = even0 + odd0;
    pm[1] = even1 + odd1;
    pm[2] = even2 + odd2;
    pm[3] = even2 - odd2;
    pm[4] = even1 - odd1;
    pm[5] = even0 - odd0;
  }

  /// <summary>
  /// Gain-adaptive-quantization (GAQ) pre-mantissa reconstruction for a single block (FFmpeg
  /// <c>ff_eac3_decode_transform_coeffs_aht_ch</c>, the <c>hebap >= 8</c> branch). <paramref name="r"/>
  /// supplies the coded mantissa(s); <paramref name="hebap"/> selects the quantizer and
  /// <paramref name="logGain"/> the GAQ gain (0 = none). Returns the 24-bit fixed-point pre-mantissa.
  /// </summary>
  public static int GaqDequant(Ac3BitReader r, int hebap, int logGain) {
    var bits = Ac3EnhancedTables.BitsVsHebap[hebap];
    var gbits = bits - logGain;
    var mant = gbits <= 0 ? 0 : r.ReadSigned(gbits);

    if (logGain != 0 && gbits > 0 && mant == -(1 << (gbits - 1))) {
      // large mantissa: re-read at higher precision and remap the asymmetric quantizer.
      var mbits = bits - (2 - logGain);
      var raw = mbits <= 0 ? 0 : r.ReadSigned(mbits);
      long mlong = (long)(uint)raw << (23 - (mbits - 1));
      var b = mlong >= 0
        ? 1L << (23 - logGain)
        : (long)Ac3EnhancedTables.GaqRemap24B[hebap - 8][logGain - 1] << 8;
      mlong += ((Ac3EnhancedTables.GaqRemap24A[hebap - 8][logGain - 1] * mlong) >> 15) + b;
      return (int)mlong;
    }

    {
      long mlong = (long)mant << (24 - bits);
      if (logGain == 0)
        mlong += (Ac3EnhancedTables.GaqRemap1[hebap - 8] * mlong) >> 15;
      return (int)mlong;
    }
  }
}
