#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// High-level layout strategy for planner-driven defragmentation.
/// Complements <see cref="DefragMode"/> (which controls *where* files land)
/// with a *how* dimension (rebuild vs. in-place planning).
/// </summary>
public enum LayoutProfile {
  /// <summary>
  /// Full zone-based layout: classify files into Hot / Normal / Cold / Frozen
  /// zones based on modification time, place Hot at the front and Frozen at
  /// the end, largest-first within each zone. Minimises seek latency for
  /// frequently-accessed files on rotational media.
  /// </summary>
  Performance,

  /// <summary>
  /// Per-file consolidation only: each fragmented file's clusters are made
  /// contiguous, but no global rearrangement is performed. Fastest to execute;
  /// useful on SSDs or when only a handful of files are fragmented.
  /// </summary>
  Quick,

  /// <summary>
  /// Caller supplies sort/group rules via <see cref="DefragOptions"/>. Reserved
  /// for future extensibility; currently behaves like <see cref="Performance"/>.
  /// </summary>
  Custom,
}
