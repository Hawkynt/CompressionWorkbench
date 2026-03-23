using Compression.Analysis.Statistics;
using Compression.Core.Dictionary.Lzss;

namespace Compression.Analysis.TrialDecompression;

/// <summary>Attempts LZSS decompression with common flag byte patterns.</summary>
public sealed class TrialLzss : ITrialStrategy {
  /// <inheritdoc />
  public string Algorithm => "LZSS";

  /// <inheritdoc />
  public DecompressionAttempt TryDecompress(ReadOnlySpan<byte> data, int maxOutput, CancellationToken ct) {
    // Try standard LZSS parameters (12-bit distance, 4-bit length, min match 3)
    try {
      using var input = new MemoryStream(data.ToArray());
      var decoder = new LzssDecoder(input, distanceBits: 12, lengthBits: 4, minMatchLength: 3);
      var result = decoder.Decode(maxOutput);

      if (result.Length == 0)
        return Fail("Output empty");

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
