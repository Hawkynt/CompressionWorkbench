using Compression.Analysis.Statistics;
using Compression.Core.Transforms;

namespace Compression.Analysis.TrialDecompression;

/// <summary>
/// Attempts BWT (Burrows-Wheeler Transform) inverse.
/// Since the original index is unknown, probes at 0, N/4, N/2, 3N/4.
/// Limits input to 4096 bytes for performance (BWT inverse is O(N)).
/// </summary>
public sealed class TrialBwt : ITrialStrategy {
  /// <inheritdoc />
  public string Algorithm => "BWT";

  private const int MaxTrialSize = 4096;

  /// <inheritdoc />
  public DecompressionAttempt TryDecompress(ReadOnlySpan<byte> data, int maxOutput, CancellationToken ct) {
    if (data.Length > MaxTrialSize)
      return Fail("Data too large for BWT trial");

    var n = data.Length;
    var indices = new[] { 0, n / 4, n / 2, 3 * n / 4 };

    DecompressionAttempt? bestResult = null;
    var bestEntropy = double.MaxValue;

    foreach (var idx in indices) {
      if (ct.IsCancellationRequested) break;
      try {
        var output = BurrowsWheelerTransform.Inverse(data, idx);
        if (output.Length == 0) continue;

        var entropy = BinaryStatistics.ComputeEntropy(output);
        if (entropy < bestEntropy) {
          bestEntropy = entropy;
          bestResult = new(Algorithm, 0, output.Length, entropy, true, null, output);
        }
      }
      catch {
        // Try next index
      }
    }

    return bestResult ?? Fail("No valid BWT inverse found");
  }

  private DecompressionAttempt Fail(string error)
    => new(Algorithm, 0, -1, -1, false, error, null);
}
