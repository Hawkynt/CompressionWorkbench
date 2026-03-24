namespace Compression.Registry;

/// <summary>Depth of validation performed.</summary>
public enum ValidationLevel {
  /// <summary>Magic byte pattern match only.</summary>
  Magic,
  /// <summary>Header fields checked for valid ranges and consistency.</summary>
  Header,
  /// <summary>Directory/TOC parsed, offsets and entry counts verified.</summary>
  Structure,
  /// <summary>Checksums verified and/or partial decompression succeeded.</summary>
  Integrity,
}

/// <summary>Severity of a validation issue.</summary>
public enum IssueSeverity {
  /// <summary>Informational observation (e.g. "uses uncommon compression method").</summary>
  Info,
  /// <summary>Non-critical issue that may indicate partial damage.</summary>
  Warning,
  /// <summary>Critical issue that prevents correct extraction.</summary>
  Error,
}

/// <summary>A single issue discovered during format validation.</summary>
public sealed record ValidationIssue(
  ValidationLevel Level,
  IssueSeverity Severity,
  string Code,
  string Description,
  long? Offset = null
);

/// <summary>
/// Result of validating a format at a specific depth.
/// </summary>
public sealed class ValidationResult {
  /// <summary>Whether the validation at this level passed.</summary>
  public required bool IsValid { get; init; }

  /// <summary>Combined confidence after this validation level (0.0–1.0).</summary>
  public required double Confidence { get; init; }

  /// <summary>Overall health assessment.</summary>
  public required FormatHealth Health { get; init; }

  /// <summary>Highest validation level that was attempted.</summary>
  public required ValidationLevel Level { get; init; }

  /// <summary>All issues found during validation.</summary>
  public required IReadOnlyList<ValidationIssue> Issues { get; init; }

  /// <summary>Number of valid/extractable entries (for archives). Null for stream formats.</summary>
  public int? ValidEntries { get; init; }

  /// <summary>Total number of entries (for archives). Null for stream formats.</summary>
  public int? TotalEntries { get; init; }
}
