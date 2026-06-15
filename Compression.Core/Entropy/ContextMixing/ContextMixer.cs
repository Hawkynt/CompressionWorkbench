namespace Compression.Core.Entropy.ContextMixing;

/// <summary>
/// Combines predictions from multiple <see cref="ContextModel"/> instances
/// in the logistic (stretch) domain — the genuine PAQ/lpaq mixer.
/// </summary>
/// <remarks>
/// <para>
/// Each model's probability <c>pᵢ</c> is mapped into the logit domain via
/// <c>stretch(pᵢ)</c>. The mixer forms the dot product
/// <c>y = Σ wᵢ · stretch(pᵢ)</c> and squashes it back to a probability,
/// <c>p = squash(y)</c>. After the true bit is observed the weights follow the
/// logistic-regression gradient
/// <c>wᵢ += lr · (bit − p) · stretch(pᵢ)</c>,
/// which is exactly online gradient descent on coding cost (cross-entropy).
/// </para>
/// <para>
/// Weights live in fixed point (16 fractional bits) so encoder and decoder
/// evolve identically. Mixing in the logit domain — rather than averaging
/// probabilities — lets a single confident model dominate when it is right,
/// which is what makes context mixing beat a plain linear blend.
/// </para>
/// </remarks>
public sealed class ContextMixer {
  /// <summary>Fixed-point fractional bits for mixer weights.</summary>
  private const int WeightShift = 16;

  /// <summary>Learning-rate numerator (paired with <see cref="WeightShift"/>).</summary>
  private const int LearningRate = 3;

  private readonly ContextModel[] _models;
  private readonly int _numModels;
  private readonly int[] _weights;     // fixed-point Q16 weights
  private readonly int[] _stretched;   // stretch(pᵢ) cached from the last Predict
  private int _lastProbability;        // p from the last Predict (12-bit)

  /// <summary>
  /// Initializes a new <see cref="ContextMixer"/> with the given models.
  /// </summary>
  /// <param name="models">The context models to mix.</param>
  public ContextMixer(params ContextModel[] models) {
    this._models = models;
    this._numModels = models.Length;
    this._weights = new int[models.Length];
    this._stretched = new int[models.Length];

    // Start with a modest uniform weight so the initial blend sits near 0.5.
    var initial = (1 << ContextMixer.WeightShift) / Math.Max(1, models.Length);
    this._weights.AsSpan().Fill(initial);
  }

  /// <summary>
  /// Gets the mixed prediction (probability of bit 1) given contexts for each model.
  /// </summary>
  /// <param name="contexts">Context hash values, one per model.</param>
  /// <returns>Probability of 1, scaled to [1, 65535] out of 65536.</returns>
  public int Predict(ReadOnlySpan<int> contexts) {
    long dot = 0;
    for (var i = 0; i < this._numModels; ++i) {
      var s = Logistic.Stretch(this._models[i].Predict(contexts[i]));
      this._stretched[i] = s;
      dot += (long)this._weights[i] * s;
    }

    var logit = (int)(dot >> ContextMixer.WeightShift);
    var p12 = Logistic.Squash(logit);
    this._lastProbability = p12;

    // Scale 12-bit probability to the 16-bit precision the arithmetic coder uses.
    var p16 = p12 << (16 - Logistic.ProbabilityBits);
    return Math.Clamp(p16, 1, 65535);
  }

  /// <summary>
  /// Updates all models and mixer weights after observing a bit.
  /// </summary>
  /// <param name="contexts">Context hash values used for prediction.</param>
  /// <param name="bit">The observed bit (0 or 1).</param>
  /// <remarks>Must be called after <see cref="Predict"/> with the same contexts.</remarks>
  public void Update(ReadOnlySpan<int> contexts, int bit) {
    // Error in 12-bit probability units: (bit - p).
    var error = (bit << Logistic.ProbabilityBits) - this._lastProbability;

    // Gradient step: wᵢ += lr · error · stretch(pᵢ), kept in fixed point.
    for (var i = 0; i < this._numModels; ++i) {
      var grad = ContextMixer.LearningRate * error * this._stretched[i];
      this._weights[i] += grad >> Logistic.ProbabilityBits;
    }

    for (var i = 0; i < this._numModels; ++i)
      this._models[i].Update(contexts[i], bit);
  }
}
