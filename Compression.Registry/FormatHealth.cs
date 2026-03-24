namespace Compression.Registry;

/// <summary>
/// Describes the structural/integrity health of a detected format instance.
/// Orthogonal to identification confidence — a file can be confidently identified
/// as ZIP (high confidence) but have broken entries (Damaged health).
/// </summary>
public enum FormatHealth {
  /// <summary>All validation checks pass, checksums verified, fully extractable.</summary>
  Perfect,
  /// <summary>Structure valid, minor non-critical issues (e.g. extra trailing data).</summary>
  Good,
  /// <summary>Mostly valid but some entries broken, unknown methods, or minor corruption.</summary>
  Degraded,
  /// <summary>Significant damage: premature EOF, corrupted sections, but partially readable.</summary>
  Damaged,
  /// <summary>Identification uncertain — magic matches but structure doesn't validate.</summary>
  Uncertain,
  /// <summary>Cannot determine health (validation not available or not attempted).</summary>
  Unknown,
}
