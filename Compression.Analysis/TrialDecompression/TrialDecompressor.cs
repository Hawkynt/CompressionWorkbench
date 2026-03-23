namespace Compression.Analysis.TrialDecompression;

/// <summary>
/// Orchestrates trial decompression: runs all strategies with configurable timeout per trial.
/// </summary>
public sealed class TrialDecompressor {

  private readonly int _maxOutputBytes;
  private readonly int _perTrialTimeoutMs;

  /// <summary>
  /// Creates a trial decompressor.
  /// </summary>
  /// <param name="maxOutputBytes">Maximum output size per trial (default: min(4x input, 1MB)).</param>
  /// <param name="perTrialTimeoutMs">Per-trial timeout in milliseconds (default: 100ms).</param>
  public TrialDecompressor(int maxOutputBytes = 0, int perTrialTimeoutMs = 100) {
    _maxOutputBytes = maxOutputBytes;
    _perTrialTimeoutMs = perTrialTimeoutMs;
  }

  /// <summary>
  /// Runs all registered trial strategies against the data.
  /// Returns successful attempts sorted by output entropy (lowest first).
  /// </summary>
  public List<DecompressionAttempt> TryAll(ReadOnlySpan<byte> data) {
    var maxOutput = _maxOutputBytes > 0 ? _maxOutputBytes : Math.Min(data.Length * 4, 1024 * 1024);
    var results = new List<DecompressionAttempt>();
    var dataArray = data.ToArray();

    foreach (var strategy in TrialRegistry.All) {
      using var cts = new CancellationTokenSource(_perTrialTimeoutMs);
      try {
        var attempt = strategy.TryDecompress(dataArray, maxOutput, cts.Token);
        if (attempt.Success)
          results.Add(attempt);
      }
      catch {
        // Strategy threw — skip
      }
    }

    results.Sort((a, b) => a.OutputEntropy.CompareTo(b.OutputEntropy));
    return results;
  }

  /// <summary>
  /// Attempts decompression with a specific strategy.
  /// </summary>
  public DecompressionAttempt TryOne(ReadOnlySpan<byte> data, string algorithm) {
    var strategy = TrialRegistry.GetByName(algorithm);
    if (strategy == null)
      return new(algorithm, 0, -1, -1, false, $"Unknown algorithm: {algorithm}", null);

    var maxOutput = _maxOutputBytes > 0 ? _maxOutputBytes : Math.Min(data.Length * 4, 1024 * 1024);
    using var cts = new CancellationTokenSource(_perTrialTimeoutMs);
    return strategy.TryDecompress(data, maxOutput, cts.Token);
  }
}
