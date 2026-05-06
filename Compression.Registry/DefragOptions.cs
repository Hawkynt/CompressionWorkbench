namespace Compression.Registry;

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

  /// <summary>Byte offset where the carved hole should start. -1 (default) =
  /// auto-pick (carve at the end, immediately after the last live extent).
  /// Ignored except in <see cref="DefragMode.CarveHole"/>.</summary>
  public long HoleAt { get; init; } = -1;
}
