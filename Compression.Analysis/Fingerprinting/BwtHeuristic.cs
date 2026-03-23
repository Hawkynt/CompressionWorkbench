using Compression.Analysis.Statistics;

namespace Compression.Analysis.Fingerprinting;

/// <summary>
/// Detects Burrows-Wheeler Transform output: characterized by run clustering
/// where identical bytes group together, creating longer runs than in random data.
/// </summary>
public sealed class BwtHeuristic : IHeuristic {

  /// <inheritdoc />
  public FingerprintResult? Analyze(ReadOnlySpan<byte> data) {
    if (data.Length < 32) return null;

    // Compute mean run length: count consecutive identical bytes
    var runCount = 1;
    var totalRunLength = 0;
    for (var i = 1; i < data.Length; i++) {
      if (data[i] == data[i - 1]) {
        totalRunLength++;
      }
      else {
        runCount++;
      }
    }
    totalRunLength += data.Length; // each byte is part of a run of at least 1
    var meanRunLength = (double)totalRunLength / runCount;

    if (meanRunLength < 2.0) return null;

    // BWT output has entropy similar to original but with clustered bytes
    var entropy = BinaryStatistics.ComputeEntropy(data);

    // BWT tends to have moderate entropy (not extremely low, not extremely high)
    // and longer runs than random data (mean run > 3)
    var confidence = 0.3;
    if (meanRunLength > 3.0) confidence += 0.15;
    if (meanRunLength > 5.0) confidence += 0.1;
    if (entropy > 3.0 && entropy < 7.5) confidence += 0.1;

    // Check run-length distribution: BWT has many short and some long runs
    if (runCount < data.Length / 2) confidence += 0.1;

    confidence = Math.Min(confidence, 0.75);
    if (confidence < 0.4) return null;

    return new("BWT", confidence, $"Mean run length: {meanRunLength:F1}, runs: {runCount}, entropy: {entropy:F2}");
  }
}
