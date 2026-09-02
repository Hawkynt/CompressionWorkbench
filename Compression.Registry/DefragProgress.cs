namespace Compression.Registry;

/// <summary>
/// What kind of bytes a contiguous region holds. Used by the live-progress
/// block map to color-code regions as maintenance proceeds.
/// </summary>
public enum DefragBlockKind {
  /// <summary>Free space — not allocated to any file.</summary>
  Free,
  /// <summary>Allocated to a file (see <see cref="DefragBlockInfo.FileName"/>).</summary>
  Used,
  /// <summary>Marked bad / quarantined (FAT-style "BAD" cluster, or post-fsck flag).</summary>
  Bad,
  /// <summary>Reserved for filesystem/container metadata.</summary>
  MetadataReserved,
  /// <summary>Currently being read, moved, compressed, grouped, or written.</summary>
  InProgress,
}

/// <summary>
/// Heuristic classification used for the maintenance block-map colors. Filesystem
/// defraggers commonly map this to hot/cold placement; archive rebuilds may map it
/// to storage/compression classes so a staged target remains visually informative.
/// </summary>
public enum DefragBlockClass {
  /// <summary>Hot / heavy-processing class.</summary>
  Hot,
  /// <summary>Normal class.</summary>
  Normal,
  /// <summary>Cold / alternative-processing class.</summary>
  Cold,
  /// <summary>Frozen / stored-verbatim class.</summary>
  Frozen,
  /// <summary>Directory or structural metadata class.</summary>
  Directory,
}

/// <summary>
/// One contiguous region in the address-space currently visualized by the
/// maintenance block map.
/// </summary>
public sealed record DefragBlockInfo(
  long Offset,
  long Length,
  DefragBlockKind Kind,
  string? FileName = null,
  DefragBlockClass? Classification = null);

/// <summary>
/// Snapshot emitted by defrag/re-layout/rebuild maintenance operations.
/// Native in-place movers normally report one physical image address-space.
/// Transactional WORM/archive rebuilds may report the source read head and staged
/// target write head in their respective byte-spaces, projected onto the same
/// chart. In that mode the two head offsets are progress visualization and do not
/// assert that identical numerical offsets refer to the same physical bytes.
/// </summary>
/// <param name="Phase">
/// Progress phase identifier. Common values are <c>scanning</c>, <c>reading</c>,
/// <c>writing</c>, <c>verifying</c>, <c>staged</c>, <c>committing</c>,
/// <c>complete</c>, and <c>error</c>.
/// </param>
/// <param name="Fraction">0..1 fraction of work done. -1 = indeterminate.</param>
/// <param name="CurrentReadOffset">Current source/read offset; -1 when not reading.</param>
/// <param name="CurrentWriteOffset">Current destination/write offset; -1 when not writing.</param>
/// <param name="ImageSize">
/// Address-space size used for visualization/binning. For staged rebuilds this is
/// a display scale large enough to project the source and target progress.
/// </param>
/// <param name="BlockMap">
/// Optional block-map snapshot. Null incremental events retain the previous map
/// and only move heads/progress, keeping redraw cost low on large archives.
/// </param>
/// <param name="Status">Optional human-readable phase/status text.</param>
public sealed record DefragProgressEvent(
  string Phase,
  double Fraction,
  long CurrentReadOffset,
  long CurrentWriteOffset,
  long ImageSize,
  IReadOnlyList<DefragBlockInfo>? BlockMap,
  string? Status = null);
