#pragma warning disable CS1591

using Compression.Analysis.Statistics;
using Compression.Registry;

namespace Compression.Analysis.TrialDecompression;

/// <summary>
/// Attempts decompression using raw building block algorithms (algorithm primitives
/// without container format headers). Useful for detecting the underlying compression
/// algorithm even when no container format matches.
/// </summary>
public sealed class TrialBuildingBlock : ITrialStrategy {
  /// <inheritdoc />
  public string Algorithm { get; }

  /// <inheritdoc />
  public TrialCategory Category => TrialCategory.BuildingBlock;

  private readonly IBuildingBlock _block;

  private TrialBuildingBlock(IBuildingBlock block) {
    _block = block;
    Algorithm = $"{block.DisplayName} (building block)";
  }

  /// <inheritdoc />
  public DecompressionAttempt TryDecompress(ReadOnlySpan<byte> data, int maxOutput, CancellationToken ct) {
    try {
      var output = _block.Decompress(data);
      if (output.Length == 0)
        return Fail("Output empty");

      if (output.Length <= data.Length)
        return Fail("Output not larger than input");

      var entropy = BinaryStatistics.ComputeEntropy(output);

      // Reasonable entropy range: too low means likely garbage zeros, too high means random noise
      if (entropy is < 1.0 or > 7.5)
        return Fail($"Output entropy {entropy:F2} outside plausible range (1.0-7.5)");

      return new(Algorithm, 0, output.Length, entropy, true, null, output);
    }
    catch (Exception ex) {
      return Fail(ex.Message);
    }
  }

  private DecompressionAttempt Fail(string error)
    => new(Algorithm, 0, -1, -1, false, error, null);

  /// <summary>Creates trial strategies for all registered building blocks.</summary>
  public static IEnumerable<TrialBuildingBlock> CreateAll() {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    foreach (var block in BuildingBlockRegistry.All)
      yield return new(block);
  }
}
