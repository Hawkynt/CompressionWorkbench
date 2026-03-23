using Compression.Analysis.Statistics;

namespace Compression.Analysis.Fingerprinting;

/// <summary>
/// Classifies data by entropy range: plaintext, structured, compressed, or encrypted.
/// </summary>
public sealed class EntropyClassifier : IHeuristic {

  /// <inheritdoc />
  public FingerprintResult? Analyze(ReadOnlySpan<byte> data) {
    if (data.Length < 16) return null;

    var entropy = BinaryStatistics.ComputeEntropy(data);
    return entropy switch {
      < 4.0 => new("Plaintext", 0.7, $"Low entropy ({entropy:F2} bits/byte) indicates uncompressed text or structured data"),
      < 6.0 => new("Structured", 0.5, $"Moderate entropy ({entropy:F2} bits/byte) suggests partially structured data"),
      < 7.5 => new("Dictionary-Compressed", 0.6, $"Entropy {entropy:F2} bits/byte is consistent with dictionary-based compression"),
      < 7.95 => new("Strong-Compression", 0.5, $"High entropy ({entropy:F2} bits/byte) suggests strong compression"),
      _ => new("Encrypted/Random", 0.3, $"Very high entropy ({entropy:F2} bits/byte) — encrypted or random data (indistinguishable)"),
    };
  }
}
