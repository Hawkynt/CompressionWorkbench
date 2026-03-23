namespace Compression.Analysis.Fingerprinting;

/// <summary>
/// Result of an algorithm fingerprinting analysis.
/// </summary>
/// <param name="Algorithm">Name of the identified algorithm.</param>
/// <param name="Confidence">Confidence level (0.0-1.0).</param>
/// <param name="Explanation">Human-readable explanation of why this algorithm was identified.</param>
public sealed record FingerprintResult(
  string Algorithm,
  double Confidence,
  string Explanation
);
