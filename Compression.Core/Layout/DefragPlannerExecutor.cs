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

    // An owner with more than one move is fragmented, and its runs have to be
    // relinked as one chain. A mover that cannot do that would write each run
    // as a file of its own — the file would end up as its last run and the rest
    // would be lost. Refuse instead; every caller falls back to a rebuild,
    // which reads each file whole before writing anything.
    var runsPerOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var move in moves) {
      runsPerOwner.TryGetValue(move.FileName, out var count);
      runsPerOwner[move.FileName] = count + 1;
    }
    var fragmented = runsPerOwner.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
    if (fragmented.Count > 0 && !mover.SupportsScatteredRelink)
      throw new InvalidOperationException(
        $"Defragmentation cannot be done in place: {fragmented.Count} fragmented owner(s) " +
        $"({string.Join(", ", fragmented.Take(3))}) would each need their runs relinked as one " +
        $"chain, which {mover.GetType().Name} cannot express.");

    // Where each block ends up, simulated in the same order the moves run, so a
    // relink can be told an owner's whole new allocation afterwards.
    var relink = mover.SupportsScatteredRelink ? new ChainTracker(moves) : null;

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
      if (relink == null)
        mover.UpdateAllocationAfterMove(archive, move.FileName, move.SrcOffset, move.DstOffset, move.Length);

      reinitAfterMove?.Invoke();
    }

    // With a scattered relink available, metadata is written once per owner
    // after every byte has moved — a fragmented owner's runs then become one
    // chain rather than several files.
    if (relink != null)
      foreach (var (owner, oldBlocks, newBlocks) in relink.Owners())
        mover.UpdateAllocationScattered(archive, owner, oldBlocks, newBlocks, relink.BlocksInUseAfterMoves);
  }

  /// <summary>
  /// Follows each moved block from where it started to where it ends up, so an
  /// owner's new allocation can be stated in one go. A move's destination can
  /// itself be moved on by a later move — staging hops do exactly that — so the
  /// blocks are tracked rather than read off the plan.
  /// </summary>
  private sealed class ChainTracker {

    private readonly Dictionary<string, List<long>> _originalByOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, long> _finalOf = [];

    public ChainTracker(IReadOnlyList<ClusterMove> moves) {
      // Block size is not known here, so a move is tracked as a whole run: the
      // planner emits one move per contiguous run, and a run's blocks stay
      // contiguous and in order wherever it lands.
      var occupant = new Dictionary<long, long>();
      foreach (var move in moves) {
        if (!this._originalByOwner.TryGetValue(move.FileName, out var list))
          this._originalByOwner[move.FileName] = list = [];
        if (!occupant.ContainsKey(move.SrcOffset)) {
          occupant[move.SrcOffset] = move.SrcOffset;
          this._finalOf[move.SrcOffset] = move.SrcOffset;
          list.Add(move.SrcOffset);
        }
      }

      foreach (var move in moves) {
        if (!occupant.TryGetValue(move.SrcOffset, out var origin)) {
          occupant[move.DstOffset] = move.DstOffset;
          continue;
        }
        occupant[move.DstOffset] = origin;
        this._finalOf[origin] = move.DstOffset;
        if (move.SrcOffset != move.DstOffset) occupant.Remove(move.SrcOffset);
      }

      this.BlocksInUseAfterMoves = this._finalOf.Values.ToHashSet();
    }

    /// <summary>Every offset an owner occupies once the moves have run.</summary>
    public IReadOnlySet<long> BlocksInUseAfterMoves { get; }

    public IEnumerable<(string Owner, IReadOnlyList<long> Old, IReadOnlyList<long> New)> Owners() {
      foreach (var (owner, originals) in this._originalByOwner) {
        var finals = originals.Select(o => this._finalOf[o]).ToList();
        yield return (owner, originals, finals);
      }
    }
  }
}
