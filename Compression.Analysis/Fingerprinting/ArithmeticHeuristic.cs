using Compression.Analysis.Statistics;

namespace Compression.Analysis.Fingerprinting;

/// <summary>
/// Detects arithmetic-coded data: near-uniform byte distribution with very high entropy.
/// Capped at 0.5 confidence since arithmetic coding output is statistically indistinguishable
/// from encrypted or truly random data.
/// </summary>
public sealed class ArithmeticHeuristic : IHeuristic {

  /// <inheritdoc />
  public FingerprintResult? Analyze(ReadOnlySpan<byte> data) {
    if (data.Length < 64) return null;

    var entropy = BinaryStatistics.ComputeEntropy(data);
    if (entropy < 7.9) return null;

    // Check distribution uniformity via chi-square
    var freq = BinaryStatistics.ComputeByteFrequency(data);
    var chiSq = BinaryStatistics.ComputeChiSquare(freq, data.Length);
    var pValue = BinaryStatistics.ChiSquarePValue(chiSq, 255);

    // Must pass randomness test
    if (pValue < 0.01) return null;

    // Check serial correlation — arithmetic coding has near-zero correlation
    var serialCorr = BinaryStatistics.ComputeSerialCorrelation(data);
    if (Math.Abs(serialCorr) > 0.1) return null;

    // Cap confidence: cannot distinguish from encryption
    var confidence = Math.Min(0.5, 0.3 + pValue * 0.2);
    return new("Arithmetic", confidence, $"Entropy {entropy:F3}, p-value {pValue:F4}, serial corr {serialCorr:F4} — may also be encrypted");
  }
}
