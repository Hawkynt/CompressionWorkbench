namespace Compression.Registry;

/// <summary>
/// Flags describing what operations a format supports.
/// <para>
/// Write capability is a four-level scale:
/// </para>
/// <list type="bullet">
///   <item><description><b>Unsupported</b> — no descriptor exists.</description></item>
///   <item><description><b>Read-Only</b> — <see cref="CanList"/> and/or <see cref="CanExtract"/> only.</description></item>
///   <item><description><b>WORM</b> (Write-Once-Read-Many) — adds <see cref="CanCreate"/>: a fresh archive can be produced from inputs, but existing archives cannot be modified in place.</description></item>
///   <item><description><b>R/W</b> (Modify) — adds <see cref="CanModify"/>: entries can be added, replaced, or removed in an existing archive without full rewrite.</description></item>
/// </list>
/// <para>
/// Most archive formats stop at WORM; true in-place modification is rare because compressed
/// archive containers don't generally support entry mutation without a full rebuild.
/// </para>
/// </summary>
[Flags]
public enum FormatCapabilities {
  None = 0,
  CanList = 1 << 0,
  CanExtract = 1 << 1,
  /// <summary>WORM: can produce a fresh archive from inputs (no in-place modification).</summary>
  CanCreate = 1 << 2,
  CanTest = 1 << 3,
  SupportsPassword = 1 << 4,
  SupportsMultipleEntries = 1 << 5,
  SupportsDirectories = 1 << 6,
  SupportsOptimize = 1 << 8,
  CanCompoundWithTar = 1 << 9,
  /// <summary>R/W: can modify an existing archive (add/replace/remove entries) without full rewrite. Implies <see cref="CanCreate"/>.</summary>
  CanModify = 1 << 10,
}
