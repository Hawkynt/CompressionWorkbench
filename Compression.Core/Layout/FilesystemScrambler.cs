#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Core.Layout;

/// <summary>
/// Runs a <see cref="ScramblePlanner" /> plan against a volume through the
/// shared move loop, so a descriptor that already defragments in place gains
/// the opposite verb without a second copy of the machinery.
/// </summary>
public static class FilesystemScrambler {

  /// <summary>
  /// Refuses a scramble the mover cannot express, before a byte is written.
  /// </summary>
  /// <remarks>
  /// Scattering leaves every owner in one run per block. A mover that repoints
  /// a run at a time can describe that only by claiming each block is a file of
  /// its own, which truncates the owner to its last block — so the answer is a
  /// refusal naming the mover, never a rebuild. A rebuild lays the volume out
  /// contiguously: reporting success having done the exact opposite of what was
  /// asked is worse than doing nothing.
  /// </remarks>
  public static void RequireScatteredRelink(IFilesystemBlockMover mover) {
    ArgumentNullException.ThrowIfNull(mover);
    if (mover.SupportsScatteredRelink) return;
    throw new InvalidOperationException(
      "Scramble cannot be done in place: it leaves every owner in one run per block, which " +
      $"{mover.GetType().Name} cannot relink as a single allocation. Nothing was changed.");
  }

  /// <summary>
  /// Plans and runs the scramble. Emits the same scanning / moving / complete
  /// progress a defragmentation does, so the maintenance block map shows the
  /// scattering happen.
  /// </summary>
  public static void Scramble(
      Stream image,
      ScrambleOptions options,
      IFilesystemBlockMover mover,
      IReadOnlyList<DefragBlockInfo> extents,
      long dataOrigin,
      long imageSize,
      int clusterSize,
      Action? reinitAfterMove = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(extents);
    RequireScatteredRelink(mover);

    options.CancellationToken.ThrowIfCancellationRequested();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, imageSize, extents, "Analysing layout"));

    var moves = ScramblePlanner.Plan(extents, dataOrigin, imageSize, clusterSize,
      options.Seed, allowMemoryStaging: mover.SupportsHeldRuns);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, imageSize, extents, "Nothing to scatter"));
      return;
    }

    // The layout goes with the plan: a scramble interleaves the moves of every
    // owner, so the order they arrive in says nothing about the order an
    // owner's blocks belong in. The extent map does.
    DefragPlannerExecutor.Execute(image, AsDefragOptions(options), mover, moves, imageSize,
      reinitAfterMove, metadataMover: null, layout: extents);

    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, imageSize, null,
      $"Scramble complete — {moves.Count} block move(s) executed"));
  }

  /// <summary>
  /// The knobs the shared move loop reads. Its own mode is never consulted once
  /// a plan is in hand, which is why a scramble needs no mode of its own.
  /// </summary>
  public static DefragOptions AsDefragOptions(ScrambleOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return new DefragOptions {
      StagingMemoryBudgetBytes = options.StagingMemoryBudgetBytes,
      OnProgress = options.OnProgress,
      CancellationToken = options.CancellationToken,
    };
  }
}
