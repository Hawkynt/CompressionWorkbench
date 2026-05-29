#pragma warning disable CS1591

using Compression.Registry;

namespace Compression.Core.Layout;

/// <summary>
/// Shared execution loop for planner-driven defragmentation. Runs an ordered list of
/// <see cref="ClusterMove"/>s against the archive, emitting per-move progress events
/// so the UI can animate cluster relocations tile-by-tile. Replaces the duplicated
/// move loops in each filesystem descriptor's <c>DefragmentWithPlanner</c> method.
/// </summary>
public static class DefragPlannerExecutor {

  /// <summary>
  /// Executes the supplied <paramref name="moves"/> against <paramref name="archive"/>,
  /// calling <see cref="IFilesystemBlockMover.MoveExtent"/> and
  /// <see cref="IFilesystemBlockMover.UpdateAllocationAfterMove"/> for each move.
  /// Emits a <see cref="DefragProgressEvent"/> per move so the UI can animate
  /// read/write head positions in real time.
  /// </summary>
  /// <param name="archive">Seekable read/write image stream.</param>
  /// <param name="options">Defrag options (progress callback + mode).</param>
  /// <param name="mover">Filesystem-specific block mover that performs the raw copy
  /// and metadata patch. Must already be initialised for the current image.</param>
  /// <param name="moves">Ordered moves from <see cref="DefragPlanner.Plan"/>.</param>
  /// <param name="imageSize">Total image size in bytes (used for progress reporting).</param>
  /// <param name="reinitAfterMove">Optional callback invoked after each move so the
  /// caller can re-read image data and reinitialise the mover (required when the mover
  /// caches byte arrays). May be <c>null</c> if the mover reads directly from the stream.</param>
  public static void Execute(
    Stream archive,
    DefragOptions options,
    IFilesystemBlockMover mover,
    IReadOnlyList<ClusterMove> moves,
    long imageSize,
    Action? reinitAfterMove = null) {

    for (var i = 0; i < moves.Count; i++) {
      var move = moves[i];

      // Emit per-move progress so the UI's block chart updates tile-by-tile.
      options.OnProgress?.Invoke(new DefragProgressEvent(
        Phase: "moving",
        Fraction: (double)(i + 1) / moves.Count,
        CurrentReadOffset: move.SrcOffset,
        CurrentWriteOffset: move.DstOffset,
        ImageSize: imageSize,
        BlockMap: null,
        Status: $"Moving {move.FileName} ({i + 1}/{moves.Count})"));

      // Crash-safe move order: COPY first (additive, idempotent), then UPDATE
      // metadata so the file is reachable via the new location, then leave the
      // old bytes in place as garbage that fsck/wipe-empty can reclaim later.
      // Zeroing the source eagerly would create a crash window where the dir
      // entry still points to a now-zeroed cluster, destroying user data.
      // For forensic wipe, callers run wipe-empty after defrag completes.
      mover.MoveExtent(archive, move.SrcOffset, move.DstOffset, move.Length, zeroSource: false);
      mover.UpdateAllocationAfterMove(archive, move.FileName, move.SrcOffset, move.DstOffset, move.Length);

      reinitAfterMove?.Invoke();
    }
  }
}
