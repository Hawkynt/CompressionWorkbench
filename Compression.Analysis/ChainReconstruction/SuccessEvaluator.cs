using Compression.Analysis.Scanning;
using Compression.Analysis.Statistics;

namespace Compression.Analysis.ChainReconstruction;

/// <summary>
/// Evaluates whether a decompression attempt produced meaningful output.
/// Checks for entropy decrease, known file signatures, and valid text content.
/// </summary>
public static class SuccessEvaluator {

  /// <summary>Result of evaluating a decompression attempt.</summary>
  public sealed record EvaluationResult(
    bool IsImprovement,
    string Reason
  );

  /// <summary>
  /// Evaluates whether the output represents an improvement over the input.
  /// </summary>
  public static EvaluationResult Evaluate(ReadOnlySpan<byte> inputData, ReadOnlySpan<byte> outputData, string algorithm) {
    if (outputData.Length == 0)
      return new(false, "Output is empty");

    // Check for excessive expansion (>100x is suspicious)
    if (outputData.Length > inputData.Length * 100)
      return new(false, "Excessive expansion ratio");

    var inputEntropy = BinaryStatistics.ComputeEntropy(inputData);
    var outputEntropy = BinaryStatistics.ComputeEntropy(outputData);

    // Size-preserving transforms (BWT, MTF): require significant structural improvement
    if (algorithm is "BWT" or "MTF") {
      // Must have meaningful entropy decrease (at least 0.5 bits/byte)
      if (outputEntropy < inputEntropy - 0.5)
        return new(true, $"Entropy decreased: {inputEntropy:F2} → {outputEntropy:F2}");

      // Or output is clearly text while input was not
      if (IsLikelyText(outputData) && !IsLikelyText(inputData))
        return new(true, "Output appears to be text");

      return new(false, "No structural improvement detected");
    }

    // For true decompression: require entropy decrease of at least 0.5 bits/byte
    // and the output must be larger than input (decompression expands)
    if (inputEntropy - outputEntropy >= 0.5 && outputData.Length >= inputData.Length)
      return new(true, $"Entropy decreased: {inputEntropy:F2} → {outputEntropy:F2}");

    // Format-level decompression with significant expansion + any entropy decrease
    if (outputData.Length > inputData.Length * 1.2 && outputEntropy < inputEntropy - 0.3)
      return new(true, $"Expanded {outputData.Length / (double)inputData.Length:F1}x with entropy drop");

    // Check if output is clearly text while input was not
    if (IsLikelyText(outputData) && !IsLikelyText(inputData))
      return new(true, "Output is text, input was not");

    // Check for known file signature at output start (only high-confidence ones)
    if (outputData.Length >= 8) {
      var signatures = SignatureScanner.Scan(outputData[..Math.Min(32, outputData.Length)], maxResults: 1);
      if (signatures.Count > 0 && signatures[0].Confidence >= 0.8)
        return new(true, $"Output contains {signatures[0].FormatName} signature");
    }

    return new(false, $"No improvement: entropy {inputEntropy:F2} → {outputEntropy:F2}");
  }

  private static bool IsLikelyText(ReadOnlySpan<byte> data) {
    if (data.Length < 8) return false;
    var printable = 0;
    foreach (var b in data) {
      if (b is >= 0x20 and < 0x7F or 0x09 or 0x0A or 0x0D)
        printable++;
    }
    return (double)printable / data.Length > 0.85;
  }
}
