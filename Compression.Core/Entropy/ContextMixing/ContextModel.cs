namespace Compression.Core.Entropy.ContextMixing;

/// <summary>
/// A single context model that predicts the probability of the next bit
/// being 1, given a context hash. Each context maps to an adaptive 12-bit
/// probability state with a per-state adaptive update rate.
/// </summary>
/// <remarks>
/// <para>
/// This is a building block for PAQ/lpaq-style context mixing compressors.
/// Each context slot stores a packed state: the high bits hold a 12-bit
/// probability of bit 1, the low bits hold a saturating <em>hit count</em>.
/// </para>
/// <para>
/// On update the probability moves towards the observed bit by
/// <c>delta = (target - p) / rate</c>, where <c>rate</c> shrinks as the hit
/// count grows. Fresh contexts therefore adapt quickly (low rate → large
/// steps) and well-trained contexts adapt slowly (high rate → stable
/// estimates), the standard lpaq state-machine behaviour expressed as a
/// counted probability. This is deterministic integer arithmetic, so encoder
/// and decoder stay bit-exact.
/// </para>
/// </remarks>
public sealed class ContextModel {
  // Probability stored in the upper 22 bits, hit count in the lower 10 bits.
  private const int CountBits = 10;
  private const int CountMask = (1 << ContextModel.CountBits) - 1;
  private const int MaxCount = ContextModel.CountMask;
  private const int InitialProbability = Logistic.ProbabilityScale / 2; // 0.5

  private readonly int[] _state;
  private readonly int _tableMask;

  /// <summary>
  /// Initializes a new <see cref="ContextModel"/> with the given table size.
  /// </summary>
  /// <param name="tableBits">Log2 of the table size (e.g. 16 → 65536 entries).</param>
  public ContextModel(int tableBits) {
    var tableSize = 1 << tableBits;
    this._tableMask = tableSize - 1;
    this._state = new int[tableSize];
    // Initialize every slot to p = 0.5, count = 0.
    this._state.AsSpan().Fill(ContextModel.InitialProbability << ContextModel.CountBits);
  }

  /// <summary>
  /// Gets the predicted probability of the next bit being 1 (scaled 0–4095).
  /// </summary>
  /// <param name="context">The context hash.</param>
  /// <returns>Probability of 1, in range [1, 4095] out of 4096.</returns>
  public int Predict(int context) {
    var p = this._state[context & this._tableMask] >> ContextModel.CountBits;
    return Math.Clamp(p, 1, Logistic.ProbabilityScale - 1);
  }

  /// <summary>
  /// Updates the model after observing a bit.
  /// </summary>
  /// <param name="context">The context hash.</param>
  /// <param name="bit">The observed bit (0 or 1).</param>
  public void Update(int context, int bit) {
    var idx = context & this._tableMask;
    var packed = this._state[idx];
    var probability = packed >> ContextModel.CountBits;
    var count = packed & ContextModel.CountMask;

    // Adaptive learning rate: divisor grows with experience, bounded so even
    // a saturated context keeps tracking slow drift.
    var rate = count + 2;
    var target = bit == 1 ? Logistic.ProbabilityScale : 0;
    probability += (target - probability) / rate;
    probability = Math.Clamp(probability, 1, Logistic.ProbabilityScale - 1);

    if (count < ContextModel.MaxCount)
      ++count;

    this._state[idx] = (probability << ContextModel.CountBits) | count;
  }
}
