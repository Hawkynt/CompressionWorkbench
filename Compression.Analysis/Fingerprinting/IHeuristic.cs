namespace Compression.Analysis.Fingerprinting;

/// <summary>
/// Interface for algorithm fingerprinting heuristics.
/// Each heuristic analyzes binary data and produces a confidence-scored identification.
/// </summary>
public interface IHeuristic {
  /// <summary>Analyzes data and returns a fingerprint result, or null if not applicable.</summary>
  FingerprintResult? Analyze(ReadOnlySpan<byte> data);
}
