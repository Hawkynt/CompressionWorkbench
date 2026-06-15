using Compression.Core.Entropy.ContextMixing;

namespace Compression.Core.Entropy.Neural;

/// <summary>
/// An online-trained two-layer neural network that predicts the probability of
/// the next bit being 1, learning its weights as it sees data. This is the
/// statistical engine behind <see cref="NnCompressor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Architecture.</b> Unlike the single-layer logistic mixer in
/// <c>ContextMixing</c> (which is a plain weighted sum in the logit domain), this
/// is a genuine multi-layer perceptron with a nonlinear hidden layer and full
/// backpropagation:
/// </para>
/// <list type="number">
///   <item><b>Inputs.</b> Each prediction <c>pᵢ</c> from a small bank of order-N
///         bit models (orders 0..3 plus two hashed sparse contexts) is mapped
///         into the logit domain via <c>stretch(pᵢ)/256</c>, giving real-valued
///         features roughly in <c>[-8, 8]</c>. A constant bias input of 1 is
///         appended.</item>
///   <item><b>Hidden layer.</b> <c>hⱼ = tanh(Σ W1[j,i]·xᵢ)</c> — a fully
///         connected layer of <c>H</c> units with a <c>tanh</c> nonlinearity.
///         The nonlinearity is what lets the net learn interactions between
///         context predictions that a linear mixer cannot represent (e.g.
///         "trust the order-3 model only when the order-1 model also agrees").</item>
///   <item><b>Output.</b> <c>y = Σ W2[j]·hⱼ</c>, then the bit probability is
///         <c>p = σ(y) = 1/(1+e^-y)</c>.</item>
/// </list>
/// <para>
/// <b>Online training.</b> After each bit is coded the true outcome
/// <c>t ∈ {0,1}</c> is known, so the net performs one step of stochastic
/// gradient descent on the cross-entropy (= coding cost):
/// </para>
/// <list type="bullet">
///   <item>output error <c>δ = t − p</c>;</item>
///   <item>output weights <c>W2[j] += η · δ · hⱼ</c>;</item>
///   <item>backprop to hidden <c>δⱼ = δ · W2[j] · (1 − hⱼ²)</c> (the tanh
///         derivative);</item>
///   <item>input weights <c>W1[j,i] += η · δⱼ · xᵢ</c>.</item>
/// </list>
/// <para>
/// <b>Determinism.</b> The decoder runs this exact same network with the same
/// initial weights (all zero), the same input ordering, and the same update
/// order, so its predictions are bit-identical to the encoder's. No weights are
/// stored in the compressed stream — the net is reconstructed by replaying the
/// data. All maths uses <see cref="double"/> with a fixed evaluation order;
/// because both sides perform the identical sequence of floating-point
/// operations the results are reproducible on any IEEE-754 platform.
/// </para>
/// <para>
/// Backing context models reuse the deterministic integer
/// <see cref="ContextModel"/> and <see cref="Logistic"/> tables from the
/// context-mixing package (read-only), so only the mixer itself is neural.
/// </para>
/// </remarks>
public sealed class NeuralPredictor {
  // Bank of bit models: orders 0..3 over the recent byte history, plus two
  // hashed sparse contexts that skip bytes (capture longer-range structure
  // cheaply). Kept small so a few-KB input codes in a fraction of a second.
  private static readonly int[] _Orders = [0, 1, 2, 3];
  private static readonly int[] _OrderTableBits = [10, 16, 18, 20];

  // Sparse contexts: (gap pattern) describing which prior bytes to fold in.
  // Each entry lists history indices to hash together with the partial byte.
  private static readonly int[][] _SparsePatterns = [[1, 3], [2, 4]];
  private const int SparseTableBits = 18;

  // Network sizing. Inputs = model count + 1 bias. Hidden layer is modest.
  private const int HiddenUnits = 12;
  private const double LearningRate = 0.06;

  private readonly ContextModel[] _models;
  private readonly int _inputCount;       // model predictions + 1 bias
  private readonly int[] _history;        // _history[0] = most recent byte

  // Network weights (zero-initialised → symmetric, deterministic start).
  private readonly double[,] _w1;         // [HiddenUnits, inputCount]
  private readonly double[] _w2;          // [HiddenUnits]

  // Per-bit forward-pass cache, reused by Update.
  private readonly double[] _inputs;      // stretched model predictions + bias
  private readonly double[] _hidden;      // tanh activations
  private readonly int[] _contexts;       // context hash per model
  private double _lastProbability;        // p from the last Predict, in (0,1)

  /// <summary>
  /// Initializes a fresh predictor with small, fixed (deterministic) weights so
  /// the hidden layer is active from the first bit and backprop has a non-zero
  /// gradient to flow through. Identical on encode and decode.
  /// </summary>
  public NeuralPredictor() {
    var orderModels = NeuralPredictor._Orders.Length;
    var sparseModels = NeuralPredictor._SparsePatterns.Length;
    var modelCount = orderModels + sparseModels;

    this._models = new ContextModel[modelCount];
    for (var i = 0; i < orderModels; ++i)
      this._models[i] = new ContextModel(NeuralPredictor._OrderTableBits[i]);
    for (var i = 0; i < sparseModels; ++i)
      this._models[orderModels + i] = new ContextModel(NeuralPredictor.SparseTableBits);

    this._inputCount = modelCount + 1; // + bias
    this._history = new int[8];

    this._w1 = new double[NeuralPredictor.HiddenUnits, this._inputCount];
    this._w2 = new double[NeuralPredictor.HiddenUnits];

    this._inputs = new double[this._inputCount];
    this._hidden = new double[NeuralPredictor.HiddenUnits];
    this._contexts = new int[modelCount];

    // Symmetry-breaking initialisation. Zero weights would leave the hidden
    // layer dead (tanh(0)=0) AND block backprop to W1 (since ∂h/∂x is gated by
    // W2, which would also be zero). A fixed, deterministic pseudo-random fill
    // — replayed identically by the decoder — seeds small weights so every unit
    // starts with a distinct, trainable response.
    var rng = 0x12345678u;
    for (var j = 0; j < NeuralPredictor.HiddenUnits; ++j) {
      for (var i = 0; i < this._inputCount; ++i)
        this._w1[j, i] = NeuralPredictor.NextWeight(ref rng, 0.20);
      this._w2[j] = NeuralPredictor.NextWeight(ref rng, 0.20);
    }
  }

  // Deterministic LCG → a small signed weight in roughly [-scale, scale].
  private static double NextWeight(ref uint state, double scale) {
    state = state * 1664525u + 1013904223u;
    var unit = (state >> 8) / (double)(1 << 24); // [0,1)
    return (unit * 2.0 - 1.0) * scale;
  }

  /// <summary>
  /// Predicts the probability that the next bit is 1, given the partial byte
  /// coded so far. Caches the forward pass for the matching <see cref="Update"/>.
  /// </summary>
  /// <param name="partialByte">
  /// The bits of the current byte decoded so far, with a leading 1 sentinel
  /// (lpaq convention): starts at 1, then <c>(partial &lt;&lt; 1) | bit</c> per bit.
  /// </param>
  /// <returns>Probability of bit 1 scaled to [1, 65535] out of 65536.</returns>
  public int Predict(int partialByte) {
    this.ComputeContexts(partialByte);

    // Forward: gather stretched model predictions as real-valued features.
    for (var i = 0; i < this._models.Length; ++i) {
      var p12 = this._models[i].Predict(this._contexts[i]);
      // stretch is the integer logit in [-2047,2047]; /256 → natural-log units.
      this._inputs[i] = Logistic.Stretch(p12) / 256.0;
    }

    this._inputs[this._inputCount - 1] = 1.0; // bias input

    // Hidden layer: tanh(W1 · x).
    for (var j = 0; j < NeuralPredictor.HiddenUnits; ++j) {
      var sum = 0.0;
      for (var i = 0; i < this._inputCount; ++i)
        sum += this._w1[j, i] * this._inputs[i];
      this._hidden[j] = Math.Tanh(sum);
    }

    // Output neuron: y = W2 · hidden, p = sigmoid(y).
    var y = 0.0;
    for (var j = 0; j < NeuralPredictor.HiddenUnits; ++j)
      y += this._w2[j] * this._hidden[j];

    var p = 1.0 / (1.0 + Math.Exp(-y));
    this._lastProbability = p;

    var p16 = (int)(p * 65536.0);
    return Math.Clamp(p16, 1, 65535);
  }

  /// <summary>
  /// Trains the network on the observed bit (one SGD step of backprop) and
  /// updates the backing context models. Must follow a matching
  /// <see cref="Predict"/> call.
  /// </summary>
  /// <param name="bit">The bit that was actually coded (0 or 1).</param>
  public void Update(int bit) {
    // Output-layer error: derivative of cross-entropy through the sigmoid
    // collapses to the clean residual (t - p).
    var delta = bit - this._lastProbability;
    var lr = NeuralPredictor.LearningRate;

    // Backprop to the hidden layer BEFORE mutating W2 (use the old weights),
    // then apply both updates. Fixed order → identical on encode and decode.
    Span<double> hiddenDelta = stackalloc double[NeuralPredictor.HiddenUnits];
    for (var j = 0; j < NeuralPredictor.HiddenUnits; ++j) {
      var h = this._hidden[j];
      hiddenDelta[j] = delta * this._w2[j] * (1.0 - h * h); // tanh'(net) = 1-h²
    }

    for (var j = 0; j < NeuralPredictor.HiddenUnits; ++j)
      this._w2[j] += lr * delta * this._hidden[j];

    for (var j = 0; j < NeuralPredictor.HiddenUnits; ++j) {
      var dj = lr * hiddenDelta[j];
      for (var i = 0; i < this._inputCount; ++i)
        this._w1[j, i] += dj * this._inputs[i];
    }

    for (var i = 0; i < this._models.Length; ++i)
      this._models[i].Update(this._contexts[i], bit);
  }

  /// <summary>Shifts a fully decoded byte into the rolling history.</summary>
  /// <param name="value">The completed byte value (0..255).</param>
  public void PushByte(int value) {
    for (var k = this._history.Length - 1; k > 0; --k)
      this._history[k] = this._history[k - 1];
    this._history[0] = value & 0xFF;
  }

  private void ComputeContexts(int partialByte) {
    var orderModels = NeuralPredictor._Orders.Length;

    for (var i = 0; i < orderModels; ++i) {
      var order = NeuralPredictor._Orders[i];
      var h = (uint)(order * 0x9E3779B1u);
      for (var k = 0; k < order; ++k)
        h = NeuralPredictor.Mix(h, (uint)this._history[k]);

      h = NeuralPredictor.Mix(h, (uint)partialByte);
      this._contexts[i] = (int)(h & 0x7FFFFFFF);
    }

    for (var s = 0; s < NeuralPredictor._SparsePatterns.Length; ++s) {
      var pattern = NeuralPredictor._SparsePatterns[s];
      var h = (uint)(0xA5A5A5A5u + (uint)s * 0x85EBCA77u);
      foreach (var idx in pattern)
        h = NeuralPredictor.Mix(h, (uint)this._history[idx]);

      h = NeuralPredictor.Mix(h, (uint)partialByte);
      this._contexts[orderModels + s] = (int)(h & 0x7FFFFFFF);
    }
  }

  private static uint Mix(uint h, uint x) {
    h ^= x + 0x9E3779B1u + (h << 6) + (h >> 2);
    return h;
  }
}
