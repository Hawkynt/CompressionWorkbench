namespace Compression.Analysis.TrialDecompression;

/// <summary>
/// Result of a trial decompression attempt.
/// </summary>
/// <param name="Algorithm">Name of the algorithm attempted.</param>
/// <param name="Offset">Offset in the input data where decompression was attempted.</param>
/// <param name="OutputSize">Size of the decompressed output, or -1 if failed.</param>
/// <param name="OutputEntropy">Shannon entropy of the output, or -1 if failed.</param>
/// <param name="Success">Whether decompression completed without error.</param>
/// <param name="Error">Error message if decompression failed, null otherwise.</param>
/// <param name="Output">The decompressed output data, if successful.</param>
public sealed record DecompressionAttempt(
  string Algorithm,
  long Offset,
  int OutputSize,
  double OutputEntropy,
  bool Success,
  string? Error,
  byte[]? Output
);
