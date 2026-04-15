namespace Compression.Analysis.TrialDecompression;

/// <summary>
/// Orchestrates trial decompression: runs all strategies with configurable timeout per trial.
/// </summary>
public sealed class TrialDecompressor {

  private readonly int _maxOutputBytes;
  private readonly int _perTrialTimeoutMs;

  /// <summary>Entropy threshold below which a result is considered high-confidence (enables early termination).</summary>
  private const double HighConfidenceEntropyThreshold = 4.0;

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
    var foundConfidentMatch = false;

    foreach (var strategy in TrialRegistry.All) {
      // Magic detections are instant — no timeout needed.
      var isMagic = strategy.Category == TrialCategory.Magic;

      // Skip slow trials (archives, building blocks) if we already have a confident format match.
      if (foundConfidentMatch && !isMagic)
        continue;

      using var cts = isMagic
        ? new CancellationTokenSource()
        : new CancellationTokenSource(_perTrialTimeoutMs);

      try {
        var attempt = strategy.TryDecompress(dataArray, maxOutput, cts.Token);
        if (!attempt.Success) continue;

        results.Add(attempt);

        // A magic match or a successful archive listing is a confident match.
        if (strategy.Category is TrialCategory.Magic or TrialCategory.Archive)
          foundConfidentMatch = true;
      }
      catch {
        // Strategy threw — skip
      }
    }

    results.Sort((a, b) => a.OutputEntropy.CompareTo(b.OutputEntropy));
    return results;
  }

  /// <summary>
  /// Runs all registered trial strategies concurrently against the data.
  /// Each trial gets its own timeout. When a high-confidence match is found (low output entropy),
  /// remaining trials are cancelled early.
  /// Returns successful attempts sorted by output entropy (lowest first).
  /// </summary>
  /// <param name="data">Input data to try decompressing.</param>
  /// <param name="ct">Cancellation token for overall cancellation.</param>
  /// <returns>List of successful decompression attempts, sorted by entropy ascending.</returns>
  public async Task<List<DecompressionAttempt>> TryAllAsync(byte[] data, CancellationToken ct = default) {
    var maxOutput = _maxOutputBytes > 0 ? _maxOutputBytes : Math.Min(data.Length * 4, 1024 * 1024);

    // Shared CTS: cancelled when a high-confidence match is found, or when caller cancels
    using var earlyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var results = new System.Collections.Concurrent.ConcurrentBag<DecompressionAttempt>();
    var highConfidenceFound = 0; // 0 = not found, 1 = found; used with Interlocked

    var strategies = TrialRegistry.All;
    var tasks = new Task[strategies.Count];
    for (var i = 0; i < strategies.Count; i++) {
      var strategy = strategies[i];
      tasks[i] = Task.Run(() => {
        // Per-trial timeout linked with the early-termination token
        using var trialCts = CancellationTokenSource.CreateLinkedTokenSource(earlyCts.Token);
        trialCts.CancelAfter(_perTrialTimeoutMs);
        try {
          var attempt = strategy.TryDecompress(data, maxOutput, trialCts.Token);
          if (!attempt.Success)
            return;

          results.Add(attempt);

          // Early termination: if entropy is low enough, cancel remaining trials
          if (attempt.OutputEntropy >= 0
              && attempt.OutputEntropy < HighConfidenceEntropyThreshold
              && Interlocked.CompareExchange(ref highConfidenceFound, 1, 0) == 0) {
            try { earlyCts.Cancel(); } catch (ObjectDisposedException) { }
          }
        }
        catch {
          // Strategy threw or was cancelled — skip
        }
      }, earlyCts.Token);
    }

    try {
      await Task.WhenAll(tasks).ConfigureAwait(false);
    }
    catch {
      // Some tasks may have been cancelled — that's expected
    }

    var list = results.ToList();
    list.Sort((a, b) => a.OutputEntropy.CompareTo(b.OutputEntropy));
    return list;
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
