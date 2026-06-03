#pragma warning disable CS1591

namespace Codec.Alac;

/// <summary>
/// Dynamic (sign-adaptive LPC) predictor — modelled on Apple's <c>dp_dec.c</c>
/// (<c>unpc_block</c>) and <c>dp_enc.c</c> (<c>pc_block</c>) from the open-source ALAC
/// reference. For predictor order N the first N+1 samples are stored as a running
/// first difference (the warm-up); every later sample is predicted from the previous
/// N reconstructed samples with integer coefficients and a quantisation shift, and
/// the coefficients adapt by a sign step after each sample. Crucially, both the
/// prediction and the coefficient adaptation depend only on already-reconstructed
/// samples, so encoder and decoder evolve identical state and the round-trip is
/// bit-exact. Order 0 (verbatim residuals) and order 31 (pure first difference) — the
/// two special modes the reference defines — are handled directly.
/// </summary>
internal static class AlacPredictor {

  /// <summary>
  /// Reconstructs <paramref name="numSamples"/> samples in place from the residuals in
  /// <paramref name="buffer"/>, using predictor <paramref name="coefs"/> of <paramref name="order"/>,
  /// quantisation <paramref name="shift"/> and channel width <paramref name="bitsPerSample"/>.
  /// </summary>
  public static void Decompress(
      int[] buffer, int numSamples, int[] coefs, int order, int shift, int bitsPerSample) {
    if (order == 0)
      return;

    if (order == 31) {
      for (var i = 1; i < numSamples; ++i)
        buffer[i] = SignExtend(buffer[i] + buffer[i - 1], bitsPerSample);
      return;
    }

    // Warm-up: the first (order+1) entries are running first differences.
    var lead = Math.Min(order, numSamples - 1);
    for (var i = 1; i <= lead; ++i)
      buffer[i] = SignExtend(buffer[i] + buffer[i - 1], bitsPerSample);

    var c = (int[])coefs.Clone();
    var round = shift > 0 ? 1 << (shift - 1) : 0;

    for (var i = order + 1; i < numSamples; ++i) {
      var residual = buffer[i];

      long sum = round;
      var anchor = buffer[i - order - 1];
      for (var j = 0; j < order; ++j)
        sum += (long)c[j] * (buffer[i - 1 - j] - anchor);
      var prediction = (int)(sum >> shift);

      var sample = SignExtend(anchor + prediction + residual, bitsPerSample);
      buffer[i] = sample;

      Adapt(c, buffer, i, order, anchor, residual);
    }
  }

  /// <summary>
  /// Produces residuals in place from the samples in <paramref name="buffer"/> with the same
  /// adaptive predictor. The exact inverse of <see cref="Decompress"/>.
  /// </summary>
  public static void Compress(
      int[] buffer, int numSamples, int[] coefs, int order, int shift, int bitsPerSample) {
    if (order == 0)
      return;

    if (order == 31) {
      for (var i = numSamples - 1; i >= 1; --i)
        buffer[i] = SignExtend(buffer[i] - buffer[i - 1], bitsPerSample);
      return;
    }

    // Work over a reconstructed copy so prediction/adaptation see the same state as
    // the decoder (the decoder rebuilds samples from anchor + prediction + residual).
    var recon = (int[])buffer.Clone();
    var c = (int[])coefs.Clone();
    var round = shift > 0 ? 1 << (shift - 1) : 0;

    var lead = Math.Min(order, numSamples - 1);

    for (var i = order + 1; i < numSamples; ++i) {
      long sum = round;
      var anchor = recon[i - order - 1];
      for (var j = 0; j < order; ++j)
        sum += (long)c[j] * (recon[i - 1 - j] - anchor);
      var prediction = (int)(sum >> shift);

      var residual = SignExtend(recon[i] - anchor - prediction, bitsPerSample);
      buffer[i] = residual;

      Adapt(c, recon, i, order, anchor, residual);
    }

    // Emit warm-up as running first differences (after the residual pass so the
    // reconstructed history above was untouched).
    for (var i = lead; i >= 1; --i)
      buffer[i] = SignExtend(recon[i] - recon[i - 1], bitsPerSample);
  }

  /// <summary>
  /// Sign-step coefficient adaptation, identical on both sides. Each coefficient nudges
  /// toward reducing the residual based on the sign of the residual and of the matching
  /// history difference — using only already-reconstructed samples.
  /// </summary>
  private static void Adapt(int[] c, int[] history, int i, int order, int anchor, int residual) {
    if (residual == 0)
      return;
    var rSign = residual > 0 ? 1 : -1;
    for (var j = 0; j < order; ++j) {
      var diff = history[i - 1 - j] - anchor;
      var dSign = diff > 0 ? 1 : diff < 0 ? -1 : 0;
      c[j] += rSign * dSign;
    }
  }

  private static int SignExtend(int value, int bits) {
    if (bits >= 32)
      return value;
    var s = 32 - bits;
    return (value << s) >> s;
  }
}
