namespace Compression.Core.Entropy.ContextMixing;

/// <summary>
/// A single context model that predicts the probability of the next bit
/// being 1, given a context hash. Uses a table of adaptive counters.
/// </summary>
/// <remarks>
/// This is a building block for PAQ-style context mixing compressors.
/// Each model maintains a table of bit prediction counters indexed by
/// a context hash. The counters adapt over time using exponential smoothing.
/// </remarks>
public sealed class ContextModel {
  private readonly int[] _counts0;
  private readonly int[] _counts1;
  private readonly int _tableMask;

  /// <summary>
  /// Initializes a new <see cref="ContextModel"/> with the given table size.
  /// </summary>
  /// <param name="tableBits">Log2 of the table size (e.g. 16 → 65536 entries).</param>
  public ContextModel(int tableBits) {
    var tableSize = 1 << tableBits;
    this._tableMask = tableSize - 1;
    this._counts0 = new int[tableSize];
    this._counts1 = new int[tableSize];
    // Initialize with small counts for Laplace smoothing
    this._counts0.AsSpan().Fill(1);
    this._counts1.AsSpan().Fill(1);
  }

  /// <summary>
  /// Gets the predicted probability of the next bit being 1 (scaled 0–4095).
  /// </summary>
  /// <param name="context">The context hash.</param>
  /// <returns>Probability of 1, in range [1, 4095] out of 4096.</returns>
  public int Predict(int context) {
    var idx = context & this._tableMask;
    var c0 = this._counts0[idx];
    var c1 = this._counts1[idx];
    var total = c0 + c1;
    // Scale to 12-bit probability
    var p = (c1 * 4096 + (total >> 1)) / total;
    return Math.Clamp(p, 1, 4095);
  }

  /// <summary>
  /// Updates the model after observing a bit.
  /// </summary>
  /// <param name="context">The context hash.</param>
  /// <param name="bit">The observed bit (0 or 1).</param>
  public void Update(int context, int bit) {
    var idx = context & this._tableMask;
    ++((bit == 0) ? this._counts0 : this._counts1)[idx];

    // Rescale when counts get too large
    var total = this._counts0[idx] + this._counts1[idx];
    if (total <= 1024)
      return;

    this._counts0[idx] = (this._counts0[idx] + 1) >> 1;
    this._counts1[idx] = (this._counts1[idx] + 1) >> 1;
  }
}
