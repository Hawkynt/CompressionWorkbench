using Compression.Registry;

namespace Compression.Analysis.Scanning;

/// <summary>
/// Result of deep-probing a format candidate, combining identification confidence
/// with structural health assessment.
/// </summary>
/// <param name="FormatId">Format identifier from the registry.</param>
/// <param name="Offset">Byte offset where the format was detected.</param>
/// <param name="Confidence">Identification confidence (0.0-1.0) after all validation levels.</param>
/// <param name="Health">Structural/integrity health assessment.</param>
/// <param name="HighestLevel">Highest validation level that was successfully completed.</param>
/// <param name="Issues">All issues found during validation.</param>
/// <param name="ValidEntries">Number of valid entries (archives only).</param>
/// <param name="TotalEntries">Total entries (archives only).</param>
public sealed record ProbeResult(
  string FormatId,
  long Offset,
  double Confidence,
  FormatHealth Health,
  ValidationLevel HighestLevel,
  IReadOnlyList<ValidationIssue> Issues,
  int? ValidEntries = null,
  int? TotalEntries = null
);
