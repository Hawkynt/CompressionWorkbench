namespace Compression.Analysis.Statistics;

/// <summary>
/// Describes a region of binary data with its statistical properties.
/// </summary>
/// <param name="Offset">Start offset in the source data.</param>
/// <param name="Length">Length of the region in bytes.</param>
/// <param name="Entropy">Shannon entropy in bits/byte (0-8).</param>
/// <param name="ChiSquare">Chi-square statistic for byte distribution.</param>
/// <param name="Mean">Mean byte value (0-255).</param>
/// <param name="Classification">Human-readable classification of the region.</param>
public sealed record RegionProfile(
  long Offset,
  int Length,
  double Entropy,
  double ChiSquare,
  double Mean,
  string Classification
);
