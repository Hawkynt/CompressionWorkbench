#pragma warning disable CS1591

namespace Codec.MonkeysAudio;

/// <summary>
/// Monkey's Audio compression-level 1000 ("fast") predictor: a single order-16
/// sign-sign LMS adaptive filter per channel, the structure used by the reference
/// SDK / ffmpeg <c>apedec.c</c> for the fast profile (one short adaptive filter,
/// no cascaded high-order stages). On decode (<see cref="Decode"/>) the filter
/// predicts the next sample from the 16 most recent reconstructed inputs, adds
/// the entropy residual, then nudges every weight by the sign of the input scaled
/// by the sign of its history tap. The encoder (<see cref="Encode"/>) runs the
/// identical predict/adapt loop over the same reconstructed history, so the two
/// stay bit-locked and the round-trip is lossless.
/// <para>
/// EXACT vs SELF-CONSISTENT: the order-16 sign-sign adaptation, the fixed-point
/// prediction shift and the history ring are faithful to the fast-profile filter
/// shape in <c>apedec.c</c>; the precise initial weights and rounding of the
/// reference's <c>predictorUpdate</c> are algebraically matched between this
/// encoder and decoder rather than bit-verified against a third-party stream.
/// Higher compression levels (2000+) use additional cascaded filters this codec
/// does not implement and are rejected on decode.
/// </para>
/// </summary>
internal sealed class ApePredictor {

  private const int Order = 16;
  private const int Shift = 10;

  private readonly int[] _weights = new int[Order];
  private readonly int[] _history = new int[Order]; // most-recent-first ring (index 0 = x[n-1])

  /// <summary>Predict + add residual + adapt; returns the reconstructed input.</summary>
  public int Decode(int residual) {
    var prediction = this.Predict();
    var input = residual + prediction;
    this.Adapt(residual, input);
    return input;
  }

  /// <summary>Predict + subtract + adapt; returns the residual to entropy-code.</summary>
  public int Encode(int input) {
    var prediction = this.Predict();
    var residual = input - prediction;
    this.Adapt(residual, input);
    return residual;
  }

  private int Predict() {
    long acc = 0;
    for (var i = 0; i < Order; ++i)
      acc += (long)this._weights[i] * this._history[i];
    return (int)(acc >> Shift);
  }

  private void Adapt(int residual, int input) {
    // Sign-sign LMS: each weight steps toward the correlation of the residual
    // sign with that tap's history sign (apedec.c adapt direction).
    var sign = Math.Sign(residual);
    if (sign != 0)
      for (var i = 0; i < Order; ++i)
        this._weights[i] += sign * Math.Sign(this._history[i]);

    // Shift the new reconstructed input into the most-recent slot.
    for (var i = Order - 1; i > 0; --i)
      this._history[i] = this._history[i - 1];
    this._history[0] = input;
  }
}
