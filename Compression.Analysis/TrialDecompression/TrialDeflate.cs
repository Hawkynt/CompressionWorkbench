using Compression.Analysis.Statistics;
using Compression.Core.Deflate;

namespace Compression.Analysis.TrialDecompression;

/// <summary>Attempts raw Deflate decompression (no wrapper header).</summary>
public sealed class TrialDeflate : ITrialStrategy {
  /// <inheritdoc />
  public string Algorithm => "Deflate";

  /// <inheritdoc />
  public TrialCategory Category => TrialCategory.Primitive;

  /// <inheritdoc />
  public DecompressionAttempt TryDecompress(ReadOnlySpan<byte> data, int maxOutput, CancellationToken ct) {
    try {
      var output = DeflateDecompressor.Decompress(data);
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
