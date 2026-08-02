using Compression.Registry.Layout;

namespace Compression.Registry;

/// <summary>
/// Controls where filesystem metadata (superblock, FAT, MFT, bitmaps, inode tables)
/// and directory extents land during defragmentation.
/// </summary>
public enum MetadataZone {
  /// <summary>Don't move metadata — preserve current positions. This is the default.</summary>
  Unchanged,
  /// <summary>Metadata + directories at lowest offsets (fast outer-track on HDDs, low-address flash advantage).</summary>
  Front,
  /// <summary>Metadata + directories at highest offsets (reserve front for file data).</summary>
  Back,
  /// <summary>Metadata + directories centered in the image (minimize average seek time on platters).</summary>
  Middle,
  /// <summary>Each directory block placed immediately before its children's data (read-ahead optimization).</summary>
  BeforeContent,
}

/// <summary>Defragmentation strategies for <see cref="IArchiveDefragmentable.Defragment(System.IO.Stream, DefragOptions)"/>.</summary>
public enum DefragMode {
  /// <summary>
  /// Pack every live extent contiguously starting at the image's data origin.
  /// Free space ends up after the last extent. The default mode and the
  /// closest match to traditional "defrag" tools.
  /// </summary>
  ConsolidateAtStart,

  /// <summary>
  /// Pack every live extent contiguously at the end of the image, leaving
  /// free space at the start (after metadata). Useful when a bootloader /
  /// installer / preallocated header expects to land in low offsets.
  /// </summary>
  ConsolidateAtEnd,

  /// <summary>
  /// Lazy compaction: each existing hole is filled with a single tail extent
  /// that fits, in best-fit order. Moves the minimum number of bytes but
  /// doesn't guarantee a contiguous final layout. Use when only a few small
  /// files were removed from a huge image.
  /// </summary>
  FillHolesLazy,

  /// <summary>
  /// Carve a contiguous free region of <see cref="DefragOptions.HoleSize"/>
  /// bytes at <see cref="DefragOptions.HoleAt"/>. Live extents intersecting
  /// the target region are relocated to the first available post-region
  /// free slot (or appended to the end of the image if no existing free
  /// slot fits).
  /// </summary>
  CarveHole,
}

/// <summary>
/// Inputs to <see cref="IArchiveDefragmentable.Defragment(System.IO.Stream, DefragOptions)"/>.
/// <see cref="Mode"/> selects the strategy; the rest are mode-specific knobs
/// that default to "do the obvious thing" for the chosen mode.
/// </summary>
public sealed record class DefragOptions {
  /// <summary>Defragmentation strategy. Default: <see cref="DefragMode.ConsolidateAtStart"/>.</summary>
  public DefragMode Mode { get; init; } = DefragMode.ConsolidateAtStart;

  /// <summary>Byte offset of the first sector available for live data
  /// (e.g. 16 * 2048 for ISO 9660 to leave the volume descriptor space alone,
  /// 0 for raw FAT). Default: 0.</summary>
  public long Origin { get; init; } = 0;

  /// <summary>Byte offset just past the last sector available for live data.
  /// -1 = auto-detect from the image's physical size. Required for
  /// <see cref="DefragMode.ConsolidateAtEnd"/> — must be set explicitly or
  /// auto-detected.</summary>
  public long ImageEnd { get; init; } = -1;

  /// <summary>Round each target offset up to this byte alignment (1 for
  /// byte-tight, 2048 for ISO 9660 sectors, 512 for FAT12/16, …). Default: 1.</summary>
  public long Alignment { get; init; } = 1;

  /// <summary>Size in bytes of the hole to carve. Required for
  /// <see cref="DefragMode.CarveHole"/>; ignored otherwise.</summary>
  public long HoleSize { get; init; } = 0;

  /// <summary>
  /// Bytes a defragmentation may hold in memory while rearranging a volume that
  /// has nowhere of its own to park a run.
  /// </summary>
  /// <remarks>
  /// A run whose destination is still occupied has to go somewhere, and on a
  /// volume with no free region left there is nowhere on disk. Memory is the
  /// answer, and the more of it there is the more of the rearrangement happens
  /// at memory speed; past this figure the runs go to a scratch file instead,
  /// which is slower but has no ceiling.
  /// </remarks>
  public long StagingMemoryBudgetBytes { get; set; } = 256L * 1024 * 1024;

  /// <summary>Byte offset where the carved hole should start. -1 (default) =
  /// auto-pick (carve at the end, immediately after the last live extent).
  /// Ignored except in <see cref="DefragMode.CarveHole"/>.</summary>
  public long HoleAt { get; init; } = -1;

  /// <summary>
  /// Optional progress callback. When non-null, the defragmenter emits at
  /// least three events: a "scanning" event with the pre-defrag block map,
  /// periodic "writing" events with read/write offsets during the rebuild,
  /// and a "complete" event with the post-defrag block map. UI consumers
  /// can render a live tile chart from these events.
  /// </summary>
  public Action<DefragProgressEvent>? OnProgress { get; init; }

  /// <summary>
  /// Layout profile for planner-driven defragmentation. Controls whether
  /// the defragmenter performs full zone-based rearrangement
  /// (<see cref="LayoutProfile.Performance"/>) or per-file consolidation
  /// only (<see cref="LayoutProfile.Quick"/>). Default:
  /// <see cref="LayoutProfile.Performance"/>.
  /// </summary>
  public LayoutProfile Profile { get; init; } = LayoutProfile.Performance;

  /// <summary>
  /// Optional metadata placement profile for file-internal optimizers.
  /// When non-null, optimizers that support <see cref="IFileInternalChunkMover"/>
  /// use these rules to decide where metadata chunks land relative to the
  /// primary data payload. When null, each optimizer uses its format-specific
  /// default placement.
  /// </summary>
  public MetadataPlacementProfile? MetadataPlacement { get; init; }

  /// <summary>
  /// Block interleave factor. 1 = contiguous (default), 2 = every-other-block,
  /// N = place each file's Kth block at (start + K*stride). Free blocks between
  /// the scattered fragments are left available for other files' interleaved
  /// blocks, round-robin style. Useful for optimizing sequential read throughput
  /// on spinning media (interleave matches rotational latency) and for testing
  /// FS robustness with fragmented layouts. Range: 1-256.
  /// </summary>
  public int InterleaveStride { get; init; } = 1;

  /// <summary>
  /// Controls where filesystem metadata and directory extents are placed during
  /// defragmentation. Default: <see cref="MetadataZone.Unchanged"/> (metadata
  /// stays where it is). Only affects planner-driven defragmentation of
  /// filesystem images; ignored for archive optimization and file-internal layout.
  /// </summary>
  public MetadataZone MetadataZonePlacement { get; init; } = MetadataZone.Unchanged;

  /// <summary>
  /// Optional layout template that overrides <see cref="Mode"/> /
  /// <see cref="MetadataZonePlacement"/> with a fine-grained per-zone plan.
  /// When set, the planner uses <see cref="LayoutTemplateResolver"/> to
  /// assign files to byte ranges; <see cref="Mode"/> is interpreted as the
  /// fallback strategy for files outside all zones (per the template's
  /// leftover strategy). When <c>null</c> (default), the planner uses the
  /// classic mode/profile/metadata-zone pipeline.
  /// </summary>
  public LayoutTemplate? LayoutTemplate { get; init; }
}
