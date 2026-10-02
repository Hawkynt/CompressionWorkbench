namespace Compression.NativeUI.Maintenance;

/// <summary>
/// The maintenance operations the Defragment tab runs: the five canonical verbs from
/// <c>docs/ARCHIVE-MODEL.md</c> plus the two composites.
/// </summary>
internal enum MaintenanceVerb {
  /// <summary>Find and apply the best layout, or re-encode the payload; size preserved where possible.</summary>
  Optimize,

  /// <summary>Keep the parameter set; minimise the stored footprint.</summary>
  Shrink,

  /// <summary>Re-order entries and extents so files are contiguous; size preserved.</summary>
  Defragment,

  /// <summary>Erase all live data, leaving a valid empty container.</summary>
  Purge,

  /// <summary>Overwrite only unused space — free clusters, slack, deleted entries; size preserved.</summary>
  WipeEmpty,

  /// <summary>Defragment, optimize and shrink in one pass: the smallest valid container.</summary>
  Compact,

  /// <summary>
  /// Scatter every allocation block across the volume — fragmentation on purpose, so
  /// <see cref="Defragment"/> has something real to undo. Content is preserved; only its place changes.
  /// </summary>
  Scramble,
}

/// <summary>
/// How a defragmentation lays the volume out, in the words the ribbon uses. Each maps onto one
/// engine <see cref="Compression.Registry.DefragMode"/>, except <see cref="SortEntries"/>, which is
/// the registry's separate directory sort and moves no data.
/// </summary>
internal enum DefragStrategy {
  /// <summary>Pack every file at one end of the volume (<c>ConsolidateAtStart</c>, or <c>ConsolidateAtEnd</c>).</summary>
  Consolidate,

  /// <summary>Fill existing holes from the tail, moving as little as possible (<c>FillHolesLazy</c>).</summary>
  Defrag,

  /// <summary>Move only what reads backwards, so every file ascends (<c>AscendingOrder</c>).</summary>
  Reorder,

  /// <summary>Sort the entries of every directory by name, in place (<c>SortDirectoryEntries</c>).</summary>
  SortEntries,

  /// <summary>Reserve one contiguous free region of a chosen size (<c>CarveHole</c>).</summary>
  CarveHole,
}

/// <summary>How Optimize improves a container; the format offers some, all or none of them.</summary>
internal enum OptimizeMethod {
  /// <summary>Re-encode the payload with the best compression, same format.</summary>
  Compress,

  /// <summary>Rebuild from the same entries, stored bytes copied verbatim, dead space dropped.</summary>
  Repack,

  /// <summary>Rewrite into the canonical representation (chunk order, padding, header normal form).</summary>
  Canonicalize,
}
