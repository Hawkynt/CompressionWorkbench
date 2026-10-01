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
/// engine <see cref="Compression.Registry.DefragMode"/>, except <see cref="SortEntries"/>, which no
/// descriptor offers yet and is answered by the capability query rather than by a placeholder.
/// </summary>
internal enum DefragStrategy {
  /// <summary>Pack every file at one end of the volume (<c>ConsolidateAtStart</c>, or <c>ConsolidateAtEnd</c>).</summary>
  Consolidate,

  /// <summary>Fill existing holes from the tail, moving as little as possible (<c>FillHolesLazy</c>).</summary>
  Defrag,

  /// <summary>Move only what reads backwards, so every file ascends (<c>AscendingOrder</c>).</summary>
  Reorder,

  /// <summary>Sort the entries of every directory. No descriptor offers this today.</summary>
  SortEntries,

  /// <summary>Reserve one contiguous free region of a chosen size (<c>CarveHole</c>).</summary>
  CarveHole,
}

/// <summary>Which engine an optimize runs through for a given format.</summary>
internal enum OptimizeRoute {
  /// <summary>The format has no optimizer.</summary>
  None,

  /// <summary>Chunks inside one file are reordered in place (MP4 fast-start, PNG/JPEG metadata).</summary>
  FileInternal,

  /// <summary>A compressed-volume file's clusters are re-encoded (DoubleSpace, DriveSpace).</summary>
  CompressedVolume,

  /// <summary>A 7z archive's solid blocks are regrouped; the smallest candidate wins.</summary>
  SolidBlocks,

  /// <summary>The container is re-encoded with its strongest settings into a staged copy.</summary>
  Reencode,
}
