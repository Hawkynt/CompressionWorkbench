using Compression.Analysis.Statistics;
using Compression.Core.Dictionary.Lzw;

namespace Compression.Analysis.TrialDecompression;

/// <summary>Attempts LZW decompression with variable bit widths (9-12 bits).</summary>
public sealed class TrialLzw : ITrialStrategy {
  /// <inheritdoc />
  public string Algorithm => "LZW";

  /// <inheritdoc />
  public DecompressionAttempt TryDecompress(ReadOnlySpan<byte> data, int maxOutput, CancellationToken ct) {
    // Try standard LZW with clear+stop codes (like Unix .Z / GIF)
    try {
      using var input = new MemoryStream(data.ToArray());
      var decoder = new LzwDecoder(input, minBits: 9, maxBits: 12, useClearCode: true, useStopCode: true);
      var result = decoder.Decode(maxOutput);
      if (result.Length == 0 || result.Length > maxOutput)
        return Fail("Output empty or too large");

      var entropy = BinaryStatistics.ComputeEntropy(result);
      return new(Algorithm, 0, result.Length, entropy, true, null, result);
    }
    catch (Exception ex) {
      return Fail(ex.Message);
    }
  }

  private DecompressionAttempt Fail(string error)
    => new(Algorithm, 0, -1, -1, false, error, null);
}
