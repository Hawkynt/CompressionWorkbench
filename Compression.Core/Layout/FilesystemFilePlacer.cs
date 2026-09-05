#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Core.Layout;

/// <summary>
/// Runs a <see cref="PlacementPlanner" /> plan against a volume through the
/// shared move loop, so a descriptor that already defragments in place gains
/// the placement verb without a second copy of the machinery.
/// </summary>
public static class FilesystemFilePlacer {

  /// <summary>
  /// Refuses a placement the mover cannot express, before a byte is written.
  /// </summary>
  /// <remarks>
  /// A placed owner is one run wherever the volume allows it, and several
  /// ascending runs where a reserved region forces a step over. A mover that
  /// can neither relink a scattered owner nor repoint runs one at a time can
  /// describe the second case only by claiming each run is a file of its own,
  /// which truncates the owner to its last run — so the answer is a refusal
  /// naming the mover, never a rebuild. A rebuild puts the owner wherever the
  /// directory walk happens to reach it, which is not where it was asked to go.
  /// </remarks>
  public static void RequirePlacementRelink(IFilesystemBlockMover mover) {
    ArgumentNullException.ThrowIfNull(mover);
    if (mover.SupportsScatteredRelink || mover.RepointsRunsIndependently) return;
    throw new InvalidOperationException(
      "Placement cannot be done in place: an owner stepped over a reserved region comes out in " +
      $"several runs, which {mover.GetType().Name} cannot relink as a single allocation. " +
      "Nothing was changed.");
  }

  /// <summary>
  /// Plans and runs the placement. Emits the same scanning / moving / complete
  /// progress a defragmentation does, so the maintenance block map shows the
  /// owner arrive at its offset.
  /// </summary>
  public static void PlaceFileAt(
      Stream image,
      PlacementOptions options,
      IFilesystemBlockMover mover,
      IReadOnlyList<DefragBlockInfo> extents,
      long dataOrigin,
      long imageSize,
      int clusterSize,
      Action? reinitAfterMove = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(extents);
    RequirePlacementRelink(mover);

    options.CancellationToken.ThrowIfCancellationRequested();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, imageSize, extents, "Analysing layout"));

    var moves = PlacementPlanner.Plan(extents, options.FileName, options.TargetOffset,
      dataOrigin, imageSize, clusterSize, allowMemoryStaging: mover.SupportsHeldRuns);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, imageSize, extents,
        $"'{options.FileName}' already starts at {options.TargetOffset:N0}"));
      return;
    }

    // The layout goes with the plan: a placement interleaves the owner's own
    // moves with the evictions that clear the way, so the order the moves
    // arrive in says nothing about the order an owner's blocks belong in. The
    // extent map does.
    DefragPlannerExecutor.Execute(image, AsDefragOptions(options), mover, moves, imageSize,
      reinitAfterMove, metadataMover: null, layout: extents);

    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, imageSize, null,
      $"'{options.FileName}' placed at {options.TargetOffset:N0} — {moves.Count} move(s) executed"));
  }

  /// <summary>
  /// The knobs the shared move loop reads. Its own mode is never consulted once
  /// a plan is in hand, which is why a placement needs no mode of its own.
  /// </summary>
  public static DefragOptions AsDefragOptions(PlacementOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return new DefragOptions {
      StagingMemoryBudgetBytes = options.StagingMemoryBudgetBytes,
      OnProgress = options.OnProgress,
      CancellationToken = options.CancellationToken,
    };
  }
}
