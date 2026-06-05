#pragma warning disable CS1591
namespace Codec.BinkAudio;

/// <summary>
/// Constant tables for the Bink Audio decoder, mechanically transcribed from FFmpeg's
/// <c>libavcodec/wma_freqs.c</c> (<c>ff_wma_critical_freqs</c>, shared by binkaudio) and
/// <c>libavcodec/binkaudio.c</c> (<c>rle_length_tab</c>). The 96-entry quantization table
/// is generated in the static constructor from the exact constant the reference uses.
/// </summary>
internal static class BinkAudioTables {

  /// <summary>
  /// 25 critical-band frequencies (wma_freqs.c <c>ff_wma_critical_freqs</c>); identical to
  /// the WMA decoder's table. Used to derive the per-frame band boundaries.
  /// </summary>
  internal static readonly int[] CriticalFreqs = [
    100, 200, 300, 400, 510, 630, 770, 920, 1080, 1270, 1480, 1720, 2000, 2320,
    2700, 3150, 3700, 4400, 5300, 6400, 7700, 9500, 12000, 15500, 24500,
  ];

  /// <summary>
  /// Run-length table for the non-version-b coefficient parser (binkaudio.c
  /// <c>rle_length_tab</c>): a 4-bit size code selects a run length in groups of 8 coeffs.
  /// </summary>
  internal static readonly byte[] RleLengthTab = [
    2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 13, 14, 15, 16, 32, 64,
  ];

  /// <summary>
  /// 96-entry dequantization table built per binkaudio.c <c>decode_init</c>:
  /// <c>quant_table[i] = expf(i * 0.15289164787221953823f) * root</c>. The exponent
  /// constant is the reference's documented value (<c>0.066399999 / log10(M_E)</c>); the
  /// per-stream <c>root</c> factor is applied by the codec, so this table stores the
  /// <c>root = 1</c> base (<c>expf(i * k)</c>) and the codec multiplies by its own root.
  /// </summary>
  internal static readonly double[] QuantBase = BuildQuantBase();

  // Result of 0.066399999 / log10(M_E) — the exact constant binkaudio uses (the literal
  // 0.15289164787221953823f appears verbatim in the reference).
  internal const double QuantExponentStep = 0.15289164787221953823;

  private static double[] BuildQuantBase() {
    var table = new double[96];
    for (var i = 0; i < 96; ++i)
      table[i] = Math.Exp(i * QuantExponentStep);
    return table;
  }
}
