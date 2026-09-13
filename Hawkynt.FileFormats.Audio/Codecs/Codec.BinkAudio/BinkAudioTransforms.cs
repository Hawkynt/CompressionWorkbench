#pragma warning disable CS1591
namespace Codec.BinkAudio;

/// <summary>
/// The two inverse transforms used by the Bink Audio decoder, standing in for FFmpeg's
/// <c>av_tx_init(AV_TX_FLOAT_RDFT, 1, …)</c> and <c>av_tx_init(AV_TX_FLOAT_DCT, 1, …)</c>
/// (binkaudio.c <c>decode_init</c>). A direct O(n²) evaluation is used: Bink transform
/// sizes top out at 2048 points and decoding is not the workbench's hot path, so a
/// faithful, obviously-correct transform is preferred over a hand-ported split-radix FFT
/// — the same trade-off taken by the WMA decoder's <c>WmaMdct</c>.
/// </summary>
internal static class BinkAudioTransforms {

  /// <summary>
  /// Inverse real DFT (complex-to-real), reproducing <c>AV_TX_FLOAT_RDFT</c> with
  /// <c>inv = 1</c> and the binkaudio scale of <c>0.5</c> (tx_template.c
  /// <c>ff_tx_rdft_c2r</c> / <c>ff_tx_rdft_init</c>). The reference packs the half-complex
  /// spectrum of a length-<paramref name="len"/> real signal as <c>len/2</c> interleaved
  /// complex bins where <c>data[0].re</c> is the DC term and <c>data[0].im</c> is the
  /// Nyquist term (binkaudio writes the Nyquist value it carried in <c>coeffs[1]</c> into
  /// <c>coeffs[len]</c> and clears <c>coeffs[1]</c> before calling the transform, so the
  /// input layout here is the raw <c>coeffs[0..len+1]</c> array: <c>coeffs[0]</c> = DC,
  /// <c>coeffs[1]</c> = 0, <c>coeffs[2i]/coeffs[2i+1]</c> = Re/Im of bin <c>i</c>, and
  /// <c>coeffs[len]</c> = Nyquist). The result is the standard inverse real DFT scaled by
  /// <paramref name="scale"/>:
  /// <c>out[n] = scale · ( Re[0] + (-1)^n · Nyq + 2·Σ_{k=1}^{len/2-1}( Re[k]·cos(2πkn/len) − Im[k]·sin(2πkn/len) ) )</c>.
  /// </summary>
  public static void InverseRdft(float[] coeffs, float[] output, int len, double scale) {
    var half = len >> 1;
    var dc = coeffs[0];
    var nyquist = coeffs[len];
    var w = 2.0 * Math.PI / len;
    for (var n = 0; n < len; ++n) {
      var sum = (double)dc;
      sum += ((n & 1) == 0 ? 1.0 : -1.0) * nyquist;
      for (var k = 1; k < half; ++k) {
        var re = coeffs[2 * k];
        var im = coeffs[2 * k + 1];
        var angle = w * k * n;
        sum += 2.0 * (re * Math.Cos(angle) - im * Math.Sin(angle));
      }
      output[n] = (float)(sum * scale);
    }
  }

  /// <summary>
  /// Inverse DCT (DCT-III), reproducing <c>AV_TX_FLOAT_DCT</c> with <c>inv = 1</c>
  /// (tx_template.c documents "the inverse transform is a DCT-III"). For
  /// <paramref name="len"/> input coefficients and a scale of <paramref name="scale"/>
  /// the orthogonal-family DCT-III is
  /// <c>out[n] = scale · ( X[0] + 2·Σ_{k=1}^{len-1} X[k]·cos( π·k·(2n+1) / (2·len) ) )</c>.
  /// binkaudio pre-multiplies <c>coeffs[0]</c> by 2 (<c>coeffs[0] /= 0.5</c>) before the
  /// call and passes <c>scale = 1 / (1 &lt;&lt; frame_len_bits) = 1 / (2·len)</c>; that
  /// scaling is supplied by the caller, so this routine only evaluates the transform.
  /// </summary>
  public static void InverseDctIII(float[] coeffs, float[] output, int len, double scale) {
    var w = Math.PI / (2.0 * len);
    for (var n = 0; n < len; ++n) {
      var sum = (double)coeffs[0];
      for (var k = 1; k < len; ++k)
        sum += 2.0 * coeffs[k] * Math.Cos(w * k * (2 * n + 1));
      output[n] = (float)(sum * scale);
    }
  }
}
