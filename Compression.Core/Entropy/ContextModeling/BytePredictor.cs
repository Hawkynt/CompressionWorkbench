namespace Compression.Core.Entropy.ContextModeling;

/// <summary>
/// Order-N byte prediction model that estimates the probability distribution
/// of the next byte given the previous N bytes of context.
/// </summary>
/// <remarks>
/// This model maintains frequency counts conditioned on contexts of length 0
/// through <c>maxOrder</c>. Higher-order contexts provide more specific predictions
/// but require more data to train. When a high-order context has insufficient data,
/// the model falls back to lower orders (PPM-style escape).
/// Used by Brotli (context modeling), ZPAQ, and other advanced compressors.
/// </remarks>
public sealed class BytePredictor {
  private readonly Dictionary<ulong, int[]> _contextCounts;

  /// <summary>Minimum total count before a context is considered reliable.</summary>
  private const int MinCount = 4;

  /// <summary>Maximum total count before rescaling.</summary>
  private const int MaxCount = 4096;

  /// <summary>
  /// Initializes a new <see cref="BytePredictor"/> with the given maximum context order.
  /// </summary>
  /// <param name="maxOrder">Maximum number of previous bytes to use as context (1–8).</param>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="maxOrder"/> is outside [1, 8].
  /// </exception>
  public BytePredictor(int maxOrder = 4) {
    ArgumentOutOfRangeException.ThrowIfLessThan(maxOrder, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(maxOrder, 8);
    this.MaxOrder = maxOrder;
    this._contextCounts = new();
  }

  /// <summary>Gets the maximum context order.</summary>
  public int MaxOrder { get; }

  /// <summary>
  /// Predicts the probability distribution of the next byte given
  /// the context (previous bytes).
  /// </summary>
  /// <param name="context">
  /// Previous bytes, most recent last. Length should be at least <see cref="MaxOrder"/>.
  /// </param>
  /// <returns>
  /// An array of 256 probabilities (scaled so they sum to approximately 65536).
  /// </returns>
  public int[] Predict(ReadOnlySpan<byte> context) {
    // Try each order from highest to lowest
    for (var order = Math.Min(this.MaxOrder, context.Length); order >= 1; --order) {
      var hash = ComputeHash(context, order);
      if (!this._contextCounts.TryGetValue(hash, out var counts))
        continue;

      var total = 0;
      for (var i = 0; i < 256; i++)
        total += counts[i];

      if (total < BytePredictor.MinCount)
        continue;

      {
        // Use this order's distribution
        var probs = new int[256];
        for (var i = 0; i < 256; ++i)
          probs[i] = ((counts[i] + 1) * 65536) / (total + 256);

        return probs;
      }
    }

    // Fallback: uniform distribution
    var uniform = new int[256];
    uniform.AsSpan().Fill(256); // 256 * 256 = 65536
    return uniform;
  }

  /// <summary>
  /// Returns the most likely next byte given the context.
  /// </summary>
  /// <param name="context">Previous bytes, most recent last.</param>
  /// <returns>The predicted byte value.</returns>
  public byte PredictByte(ReadOnlySpan<byte> context) {
    var probs = this.Predict(context);
    var bestSym = 0;
    var bestProb = 0;
    for (var i = 0; i < 256; ++i)
      if (probs[i] > bestProb) {
        bestProb = probs[i];
        bestSym = i;
      }

    return (byte)bestSym;
  }

  /// <summary>
  /// Updates the model after observing a byte.
  /// </summary>
  /// <param name="context">Previous bytes, most recent last.</param>
  /// <param name="symbol">The observed byte.</param>
  public void Update(ReadOnlySpan<byte> context, byte symbol) {
    // Update all orders
    for (var order = 1; order <= Math.Min(this.MaxOrder, context.Length); ++order) {
      var hash = ComputeHash(context, order);
      if (!this._contextCounts.TryGetValue(hash, out var counts)) {
        counts = new int[256];
        this._contextCounts[hash] = counts;
      }

      ++counts[symbol];

      // Rescale when counts get too large
      var total = 0;
      for (var i = 0; i < 256; ++i)
        total += counts[i];

      if (total <= BytePredictor.MaxCount)
        continue;

      for (var i = 0; i < 256; ++i)
        counts[i] = (counts[i] + 1) >> 1;
    }
  }

  /// <summary>
  /// Computes a hash for the last <paramref name="order"/> bytes of context.
  /// </summary>
  private static ulong ComputeHash(ReadOnlySpan<byte> context, int order) {
    var hash = (ulong)order * 0x9E3779B97F4A7C15UL;
    var start = context.Length - order;
    for (var i = start; i < context.Length; ++i) {
      hash ^= context[i];
      hash *= 0x517CC1B727220A95UL;
    }
    return hash;
  }
}
