namespace Compression.Analysis.Scanning;

/// <summary>
/// Result of a signature scan at a specific offset.
/// </summary>
/// <param name="Offset">Byte offset where the signature was found.</param>
/// <param name="FormatName">Name of the detected format.</param>
/// <param name="Confidence">Confidence level (0.0-1.0) based on magic length and specificity.</param>
/// <param name="MagicLength">Number of bytes in the matched magic pattern.</param>
/// <param name="HeaderPreview">First few bytes at the match location (hex).</param>
public sealed record ScanResult(
  long Offset,
  string FormatName,
  double Confidence,
  int MagicLength,
  string HeaderPreview
);
