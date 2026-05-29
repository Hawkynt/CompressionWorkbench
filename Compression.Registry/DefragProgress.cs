namespace Compression.Registry;

/// <summary>
/// What kind of bytes a contiguous region holds. Used by the live-progress
/// block map to color-code regions as defrag proceeds.
/// </summary>
public enum DefragBlockKind {
  /// <summary>Free space — not allocated to any file.</summary>
  Free,
  /// <summary>Allocated to a file (see <see cref="DefragBlockInfo.FileName"/>).</summary>
  Used,
  /// <summary>Marked bad / quarantined (FAT-style "BAD" cluster, or post-fsck flag).</summary>
  Bad,
  /// <summary>Reserved for filesystem metadata (boot sectors, superblock, MFT, FAT, bitmap, root directory).</summary>
  MetadataReserved,
  /// <summary>Currently being read or written by the in-progress defrag operation.</summary>
  InProgress,
}

/// <summary>
/// Heuristic classification of a file's "thermal" zone based on its
/// modification time. Drives layout placement: hot at start, normal in
/// the middle, frozen near the end. Used by the live-progress block map
/// for tile coloring.
/// </summary>
public enum DefragBlockClass {
  /// <summary>File modified recently (top quartile) — placed near start.</summary>
  Hot,
  /// <summary>File modified normally — placed in the middle.</summary>
  Normal,
  /// <summary>File modified a while ago — placed near end.</summary>
  Cold,
  /// <summary>File hasn't been touched in a long time (bottom quartile) — placed at end.</summary>
  Frozen,
  /// <summary>Directory metadata (folder contents, B-tree dir node, etc.) — rendered gold to make placement visible.</summary>
  Directory,
}

/// <summary>
/// One contiguous region of an image's address space, as seen by the
/// live-progress block map.
/// </summary>
public sealed record DefragBlockInfo(
  long Offset,
  long Length,
  DefragBlockKind Kind,
  string? FileName = null,
  DefragBlockClass? Classification = null);

/// <summary>
/// Snapshot of an image's block layout at a moment in time. Emitted by
/// <see cref="IArchiveDefragmentable.Defragment(System.IO.Stream, DefragOptions)"/>
/// implementations through DefragOptions.OnProgress at scan
/// start, periodically during writes, and at completion.
/// </summary>
/// <param name="Phase">Progress phase identifier ("scanning" / "writing" / "complete" / "error").</param>
/// <param name="Fraction">0..1 fraction of work done. -1 = indeterminate.</param>
/// <param name="CurrentReadOffset">Byte offset currently being read; -1 if not reading.</param>
/// <param name="CurrentWriteOffset">Byte offset currently being written; -1 if not writing.</param>
/// <param name="ImageSize">Total image size in bytes (helpful for tile binning).</param>
/// <param name="BlockMap">Block-map snapshot, present at scan start + completion. Null during incremental updates.</param>
/// <param name="Status">Optional human-readable status (e.g. "moving extent 23 of 87").</param>
public sealed record DefragProgressEvent(
  string Phase,
  double Fraction,
  long CurrentReadOffset,
  long CurrentWriteOffset,
  long ImageSize,
  IReadOnlyList<DefragBlockInfo>? BlockMap,
  string? Status = null);
