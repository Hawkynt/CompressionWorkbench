namespace Compression.Core.Entropy.ContextMixing;

/// <summary>
/// Combines predictions from multiple <see cref="ContextModel"/> instances
/// using adaptive linear mixing (weighted average).
/// </summary>
/// <remarks>
/// This is the core component of PAQ-style compressors (used in ZPAQ, KGB, etc.).
/// Each model provides a probability prediction, and the mixer assigns learned
/// weights to produce a combined prediction. Weights are updated using gradient
/// descent on the prediction error after each bit is observed.
/// </remarks>
public sealed class ContextMixer {
  private readonly ContextModel[] _models;
  private readonly double[] _weights;
  private readonly int _numModels;

  /// <summary>Learning rate for weight updates.</summary>
  private const double LearningRate = 0.002;

  /// <summary>
  /// Initializes a new <see cref="ContextMixer"/> with the given models.
  /// </summary>
  /// <param name="models">The context models to mix.</param>
  public ContextMixer(params ContextModel[] models) {
    this._models = models;
    this._numModels = models.Length;
    this._weights = new double[models.Length];
    // Start with uniform weights
    var w = 1.0 / models.Length;
    Array.Fill(this._weights, w);
  }

  /// <summary>
  /// Gets the mixed prediction (probability of bit 1) given contexts for each model.
  /// </summary>
  /// <param name="contexts">Context hash values, one per model.</param>
  /// <returns>Probability of 1, scaled to [1, 65535] out of 65536.</returns>
  public int Predict(ReadOnlySpan<int> contexts) {
    double mixed = 0;
    for (var i = 0; i < this._numModels; ++i) {
      var p = this._models[i].Predict(contexts[i]);
      // Convert 12-bit probability to logistic space for better mixing
      var prob = p / 4096.0;
      mixed += this._weights[i] * prob;
    }

    // Clamp and scale to 16-bit
    mixed = Math.Clamp(mixed, 0.0001, 0.9999);
    var result = (int)(mixed * 65536);
    return Math.Clamp(result, 1, 65535);
  }

  /// <summary>
  /// Updates all models and mixer weights after observing a bit.
  /// </summary>
  /// <param name="contexts">Context hash values used for prediction.</param>
  /// <param name="bit">The observed bit (0 or 1).</param>
  public void Update(ReadOnlySpan<int> contexts, int bit) {
    // Update weights using gradient descent
    double target = bit;
    var preds = new double[this._numModels];
    double mixed = 0;

    for (var i = 0; i < this._numModels; ++i) {
      preds[i] = this._models[i].Predict(contexts[i]) / 4096.0;
      mixed += this._weights[i] * preds[i];
    }

    mixed = Math.Clamp(mixed, 0.0001, 0.9999);
    var error = target - mixed;

    // Update weights: w_i += lr * error * p_i
    for (var i = 0; i < this._numModels; ++i) {
      this._weights[i] += ContextMixer.LearningRate * error * preds[i];
      // Keep weights positive
      if (this._weights[i] < 0.001)
        this._weights[i] = 0.001;
    }

    // Normalize weights
    double sum = 0;
    for (var i = 0; i < this._numModels; ++i)
      sum += this._weights[i];

    for (var i = 0; i < this._numModels; ++i)
      this._weights[i] /= sum;

    // Update individual models
    for (var i = 0; i < this._numModels; ++i)
      this._models[i].Update(contexts[i], bit);
  }
}
