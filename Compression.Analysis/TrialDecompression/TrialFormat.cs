using Compression.Analysis.Statistics;
using Compression.Registry;

namespace Compression.Analysis.TrialDecompression;

/// <summary>
/// Attempts format-level decompression using the Compression.Lib format libraries.
/// Supports all stream formats registered in the FormatRegistry.
/// </summary>
public sealed class TrialFormat : ITrialStrategy {
  /// <inheritdoc />
  public string Algorithm { get; }

  private readonly Func<Stream, Stream, bool> _decompressor;

  private TrialFormat(string algorithm, Func<Stream, Stream, bool> decompressor) {
    Algorithm = algorithm;
    _decompressor = decompressor;
  }

  /// <inheritdoc />
  public DecompressionAttempt TryDecompress(ReadOnlySpan<byte> data, int maxOutput, CancellationToken ct) {
    try {
      using var input = new MemoryStream(data.ToArray());
      using var output = new MemoryStream();

      if (!_decompressor(input, output))
        return Fail("Decompression failed");

      var result = output.ToArray();
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

  /// <summary>Creates trial strategies for all registered stream formats that support decompression.</summary>
  public static IEnumerable<TrialFormat> CreateAll() {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    foreach (var desc in FormatRegistry.All) {
      if (desc.Category is not (FormatCategory.Stream or FormatCategory.CompoundTar))
        continue;
      if (!desc.Capabilities.HasFlag(FormatCapabilities.CanExtract))
        continue;

      var ops = FormatRegistry.GetStreamOps(desc.Id);
      if (ops == null)
        continue;

      var capturedOps = ops;
      yield return new(desc.DisplayName, (input, output) => {
        capturedOps.Decompress(input, output);
        return true;
      });
    }
  }
}
