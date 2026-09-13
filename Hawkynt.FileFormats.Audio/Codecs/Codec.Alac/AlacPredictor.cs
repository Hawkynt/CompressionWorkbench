#pragma warning disable CS1591

namespace Codec.Alac;

/// <summary>
/// Dynamic (sign-adaptive LPC) predictor, following the reference <c>dp_dec.c</c>
/// (<c>unpc_block</c>) and <c>dp_enc.c</c> (<c>pc_block</c>).
/// <para>
/// The first sample is stored verbatim and the next <c>numActive</c> samples as running
/// first differences. Every later sample is predicted from the previous <c>numActive</c>
/// reconstructed samples, but relative to an <em>anchor</em> — the sample
/// <c>numActive + 1</c> positions back — so the coefficients weight differences rather
/// than absolute values. After each sample the coefficients take a signed unit step
/// toward reducing the residual, walking from the oldest tap to the newest and stopping
/// as soon as the running residual estimate changes sign. That early exit and the
/// <c>(numActive - k)</c> weighting are load-bearing: a plain sign-LMS update decodes a
/// different signal.
/// </para>
/// <para>
/// Coefficients are 16-bit and are meant to wrap; the sums are 32-bit and are meant to
/// wrap too, so every arithmetic step here is deliberately <c>unchecked</c>.
/// </para>
/// </summary>
internal static class AlacPredictor {

  /// <summary>Order 0 (verbatim) and order 31 (pure first difference) are the two special modes.</summary>
  private const int FirstDifferenceOrder = 31;

  /// <summary>The reference <c>sign_of_int()</c>: -1, 0 or +1.</summary>
  private static int SignOf(int value) => (int)((uint)-value >> 31) | (value >> 31);

  /// <summary>
  /// Reconstructs <paramref name="numSamples"/> samples from the residuals in
  /// <paramref name="input"/> into <paramref name="output"/> (which may be the same array),
  /// using <paramref name="coefs"/> of <paramref name="numActive"/> taps, quantisation
  /// <paramref name="denShift"/> and channel width <paramref name="chanBits"/>.
  /// <paramref name="coefs"/> is updated in place, as the reference does.
  /// </summary>
  public static void Decompress(
      int[] input, int[] output, int numSamples, short[] coefs, int numActive, int chanBits, int denShift) {
    if (numSamples <= 0)
      return;

    var chanShift = 32 - chanBits;
    output[0] = input[0];

    if (numActive == 0) {
      if (numSamples > 1 && !ReferenceEquals(input, output))
        Array.Copy(input, 1, output, 1, numSamples - 1);
      return;
    }

    if (numActive == FirstDifferenceOrder) {
      var previous = output[0];
      for (var j = 1; j < numSamples; ++j) {
        previous = SignExtend(input[j] + previous, chanShift);
        output[j] = previous;
      }
      return;
    }

    for (var j = 1; j <= numActive && j < numSamples; ++j)
      output[j] = SignExtend(input[j] + output[j - 1], chanShift);

    var lim = numActive + 1;
    var denHalf = denShift > 0 ? 1 << (denShift - 1) : 0;

    unchecked {
      for (var j = lim; j < numSamples; ++j) {
        var top = output[j - lim];

        var sum = 0;
        for (var k = 0; k < numActive; ++k)
          sum += coefs[k] * (output[j - 1 - k] - top);

        var residual = input[j];
        var remaining = residual;
        var residualSign = SignOf(residual);

        output[j] = SignExtend(residual + top + ((sum + denHalf) >> denShift), chanShift);

        if (residualSign > 0) {
          for (var k = numActive - 1; k >= 0; --k) {
            var difference = top - output[j - 1 - k];
            var sign = SignOf(difference);
            coefs[k] -= (short)sign;
            remaining -= (numActive - k) * ((sign * difference) >> denShift);
            if (remaining <= 0)
              break;
          }
        } else if (residualSign < 0) {
          for (var k = numActive - 1; k >= 0; --k) {
            var difference = top - output[j - 1 - k];
            var sign = SignOf(difference);
            coefs[k] += (short)sign;
            remaining -= (numActive - k) * ((-sign * difference) >> denShift);
            if (remaining >= 0)
              break;
          }
        }
      }
    }
  }

  /// <summary>
  /// Produces residuals from the samples in <paramref name="input"/> into
  /// <paramref name="output"/>, the exact inverse of <see cref="Decompress"/>. Prediction and
  /// adaptation read only <paramref name="input"/>, which the decoder will have rebuilt
  /// identically, so the two evolve the same coefficient state.
  /// </summary>
  public static void Compress(
      int[] input, int[] output, int numSamples, short[] coefs, int numActive, int chanBits, int denShift) {
    if (numSamples <= 0)
      return;

    var chanShift = 32 - chanBits;
    output[0] = input[0];

    if (numActive == 0) {
      if (numSamples > 1 && !ReferenceEquals(input, output))
        Array.Copy(input, 1, output, 1, numSamples - 1);
      return;
    }

    if (numActive == FirstDifferenceOrder) {
      for (var j = 1; j < numSamples; ++j)
        output[j] = SignExtend(input[j] - input[j - 1], chanShift);
      return;
    }

    for (var j = 1; j <= numActive && j < numSamples; ++j)
      output[j] = SignExtend(input[j] - input[j - 1], chanShift);

    var lim = numActive + 1;
    var denHalf = denShift > 0 ? 1 << (denShift - 1) : 0;

    unchecked {
      for (var j = lim; j < numSamples; ++j) {
        var top = input[j - lim];

        var sum = 0;
        for (var k = 0; k < numActive; ++k)
          sum -= coefs[k] * (top - input[j - 1 - k]);

        var residual = SignExtend(input[j] - top - ((sum + denHalf) >> denShift), chanShift);
        output[j] = residual;

        var remaining = residual;
        var residualSign = SignOf(residual);

        if (residualSign > 0) {
          for (var k = numActive - 1; k >= 0; --k) {
            var difference = top - input[j - 1 - k];
            var sign = SignOf(difference);
            coefs[k] -= (short)sign;
            remaining -= (numActive - k) * ((sign * difference) >> denShift);
            if (remaining <= 0)
              break;
          }
        } else if (residualSign < 0) {
          for (var k = numActive - 1; k >= 0; --k) {
            var difference = top - input[j - 1 - k];
            var sign = SignOf(difference);
            coefs[k] += (short)sign;
            remaining -= (numActive - k) * ((-sign * difference) >> denShift);
            if (remaining >= 0)
              break;
          }
        }
      }
    }
  }

  /// <summary>The reference <c>init_coefs()</c> seed for a fresh channel.</summary>
  public static short[] InitialCoefficients(int numActive, int denShift) {
    var coefs = new short[Math.Max(numActive, 1)];
    var den = 1 << denShift;
    if (coefs.Length > 0) coefs[0] = (short)(38 * den >> 4);
    if (coefs.Length > 1) coefs[1] = (short)(-29 * den >> 4);
    if (coefs.Length > 2) coefs[2] = (short)(-2 * den >> 4);
    return coefs;
  }

  private static int SignExtend(int value, int chanShift) => value << chanShift >> chanShift;
}
