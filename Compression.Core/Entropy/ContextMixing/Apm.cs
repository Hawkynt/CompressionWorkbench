namespace Compression.Core.Entropy.ContextMixing;

/// <summary>
/// Adaptive Probability Map (a.k.a. Secondary Symbol Estimation, SSE) — refines
/// a probability produced by the mixer using a small refinement context.
/// </summary>
/// <remarks>
/// <para>
/// The mixed probability is mapped into the stretch domain and quantised onto a
/// grid of interpolation knots. For a given refinement context, each knot stores
/// an adaptive probability; the APM linearly interpolates between the two knots
/// bracketing the stretched input and, after the bit is seen, nudges those knots
/// towards the truth. This corrects systematic bias the mixer leaves behind
/// (for example, predictions that are consistently over-confident), typically
/// shaving a few more percent off the output.
/// </para>
/// <para>
/// All arithmetic is integer and deterministic so the decoder reproduces the
/// encoder's refinements exactly.
/// </para>
/// </remarks>
public sealed class Apm {
  // Interpolation knots per context, spanning the stretch domain.
  private const int Knots = 33;
  private const int StretchSpan = Logistic.MaxStretch - Logistic.MinStretch; // 4094
  private const int Step = Apm.StretchSpan / (Apm.Knots - 1);                // ~127

  private readonly int[] _map; // [context * Knots + knot] → 12-bit probability
  private readonly int _contextMask;
  private readonly int _rate;

  private int _lastIndex; // knot index updated on the last Refine call
  private int _lastWeight; // interpolation weight (0..Step) towards the upper knot

  /// <summary>
  /// Initializes a new <see cref="Apm"/>.
  /// </summary>
  /// <param name="contexts">Number of distinct refinement contexts.</param>
  /// <param name="rate">Update-rate shift (larger = slower adaptation). Defaults to 7.</param>
  public Apm(int contexts, int rate = 7) {
    this._contextMask = contexts - 1;
    this._rate = rate;
    this._map = new int[contexts * Apm.Knots];

    // Initialise each context's knots to the identity map: knot k corresponds
    // to logit (MinStretch + k*Step), so its prior probability is squash(that).
    for (var c = 0; c < contexts; ++c)
    for (var k = 0; k < Apm.Knots; ++k) {
      var logit = Logistic.MinStretch + k * Apm.Step;
      this._map[c * Apm.Knots + k] = Logistic.Squash(logit);
    }
  }

  /// <summary>
  /// Refines a probability for the given context, caching state for <see cref="Update"/>.
  /// </summary>
  /// <param name="probability">Input probability of bit 1, 12-bit [0, 4095].</param>
  /// <param name="context">Refinement context (masked to the configured count).</param>
  /// <returns>The refined 12-bit probability in [1, 4095].</returns>
  public int Refine(int probability, int context) {
    var s = Logistic.Stretch(probability) - Logistic.MinStretch; // 0 .. StretchSpan
    var knot = s / Apm.Step;
    var weight = s % Apm.Step;
    if (knot >= Apm.Knots - 1) {
      knot = Apm.Knots - 2;
      weight = Apm.Step;
    }

    var baseIdx = (context & this._contextMask) * Apm.Knots + knot;
    var lo = this._map[baseIdx];
    var hi = this._map[baseIdx + 1];
    var refined = lo + (hi - lo) * weight / Apm.Step;

    // Remember the knot closer to the input for the update step.
    this._lastIndex = weight * 2 >= Apm.Step ? baseIdx + 1 : baseIdx;
    this._lastWeight = weight;

    return Math.Clamp(refined, 1, Logistic.ProbabilityScale - 1);
  }

  /// <summary>
  /// Updates the knots used by the last <see cref="Refine"/> call towards the observed bit.
  /// </summary>
  /// <param name="bit">The observed bit (0 or 1).</param>
  public void Update(int bit) {
    var target = bit == 1 ? Logistic.ProbabilityScale - 1 : 0;
    var current = this._map[this._lastIndex];
    this._map[this._lastIndex] = current + ((target - current) >> this._rate);
  }
}
