using Compression.Analysis.Statistics;
using Compression.Core.Transforms;

namespace Compression.Analysis.TrialDecompression;

/// <summary>Attempts MTF (Move-to-Front) decoding.</summary>
public sealed class TrialMtf : ITrialStrategy {
  /// <inheritdoc />
  public string Algorithm => "MTF";

  /// <inheritdoc />
  public DecompressionAttempt TryDecompress(ReadOnlySpan<byte> data, int maxOutput, CancellationToken ct) {
    try {
      var output = MoveToFrontTransform.Decode(data);
      if (output.Length == 0 || output.Length > maxOutput)
        return Fail("Output empty or too large");

      var entropy = BinaryStatistics.ComputeEntropy(output);
      return new(Algorithm, 0, output.Length, entropy, true, null, output);
    }
    catch (Exception ex) {
      return Fail(ex.Message);
    }
  }

  private DecompressionAttempt Fail(string error)
    => new(Algorithm, 0, -1, -1, false, error, null);
}
