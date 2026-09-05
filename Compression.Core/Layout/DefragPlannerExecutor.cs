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
  /// <see cref="IFilesystemBlockMover.UpdateAllocationAfterMove(Stream, string, long, long, long)"/> for each move.
  /// Emits a <see cref="DefragProgressEvent"/> per move so the UI can animate
  /// read/write head positions in real time. Cancellation is checked between
  /// safe move units; already-completed in-place moves are intentionally not rolled back.
  /// </summary>
  public static void Execute(
    Stream archive,
    DefragOptions options,
    IFilesystemBlockMover mover,
    IReadOnlyList<ClusterMove> moves,
    long imageSize,
    Action? reinitAfterMove = null,
    IFilesystemMetadataMover? metadataMover = null,
    IReadOnlyList<DefragBlockInfo>? layout = null) {

    options.CancellationToken.ThrowIfCancellationRequested();
    var metadataNames = metadataMover?.RelocatableMetadata ?? (IReadOnlySet<string>)new HashSet<string>();
    bool IsMetadata(string owner) => metadataNames.Contains(owner);

    var writes = moves.Where(m => m.Staging != DefragStaging.Park).ToList();
    var liveRanges = metadataNames.Count == 0
      ? null
      : writes.Select(m => (m.DstOffset, m.Length)).ToList();

    var runsPerOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var move in writes) {
      if (IsMetadata(move.FileName)) continue;
      runsPerOwner.TryGetValue(move.FileName, out var count);
      runsPerOwner[move.FileName] = count + 1;
    }
    var fragmented = runsPerOwner.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
    if (fragmented.Count > 0 && !mover.SupportsScatteredRelink && !mover.RepointsRunsIndependently)
      throw new InvalidOperationException(
        $"Defragmentation cannot be done in place: {fragmented.Count} fragmented owner(s) " +
        $"({string.Join(", ", fragmented.Take(3))}) would each need their runs relinked as one " +
        $"chain, which {mover.GetType().Name} cannot express.");

    var fileMoves = metadataNames.Count == 0
      ? moves
      : moves.Where(m => !IsMetadata(m.FileName)).ToList();
    var relink = mover.SupportsScatteredRelink
      ? new ChainTracker(fileMoves, mover.AllocationBlockSize, layout)
      : null;

    var parks = moves.Any(m => m.Staging == DefragStaging.Park);
    if (parks && !mover.SupportsHeldRuns)
      throw new InvalidOperationException(
        $"Defragmentation cannot be done in place: the layout needs a run held out of the volume " +
        $"while the rest move, which {mover.GetType().Name} does not offer.");

    using var staging = parks ? new DefragStagingBuffer(options.StagingMemoryBudgetBytes) : null;

    for (var i = 0; i < moves.Count; i++) {
      // This is the native/in-place cancellation boundary. A previous move may
      // already be durable; stopping here preserves validity rather than trying
      // to reverse arbitrary filesystem pointer updates.
      options.CancellationToken.ThrowIfCancellationRequested();
      var move = moves[i];
      var what = move.Staging switch {
        DefragStaging.Park => "Holding",
        DefragStaging.Unpark => "Placing",
        _ => "Moving",
      };
      options.OnProgress?.Invoke(new DefragProgressEvent(
        Phase: "moving",
        Fraction: (double)(i + 1) / moves.Count,
        CurrentReadOffset: move.SrcOffset,
        CurrentWriteOffset: move.DstOffset,
        ImageSize: imageSize,
        BlockMap: null,
        Status: $"{what} {move.FileName} ({i + 1}/{moves.Count})"));

      if (move.Staging == DefragStaging.Park) {
        staging!.Park(archive, move.StagingSlot, move.SrcOffset, move.Length);
        continue;
      }

      if (move.Staging == DefragStaging.Unpark) {
        if (IsMetadata(move.FileName))
          metadataMover!.PrepareMetadataMove(archive, move.FileName,
            move.SrcOffset, move.DstOffset, move.Length);
        staging!.Unpark(archive, move.StagingSlot, move.DstOffset);
        if (IsMetadata(move.FileName))
          metadataMover!.UpdateMetadataAfterMove(archive, move.FileName,
            move.SrcOffset, move.DstOffset, move.Length, liveRanges);
        else if (relink == null)
          mover.UpdateAllocationAfterMove(archive, move.FileName, move.SrcOffset, move.DstOffset,
            move.Length, releaseOldSpace: false);
        reinitAfterMove?.Invoke();
        continue;
      }

      // Metadata that owns allocation state may need its destination claim in
      // the source bytes before those bytes are copied. The default hook is a
      // no-op, so ordinary filesystems retain the classic COPY -> REPOINT order.
      if (IsMetadata(move.FileName))
        metadataMover!.PrepareMetadataMove(archive, move.FileName,
          move.SrcOffset, move.DstOffset, move.Length);

      mover.MoveExtent(archive, move.SrcOffset, move.DstOffset, move.Length, zeroSource: false);
      if (IsMetadata(move.FileName))
        metadataMover!.UpdateMetadataAfterMove(archive, move.FileName,
          move.SrcOffset, move.DstOffset, move.Length, liveRanges);
      else if (relink == null)
        mover.UpdateAllocationAfterMove(archive, move.FileName, move.SrcOffset, move.DstOffset, move.Length);

      reinitAfterMove?.Invoke();
    }

    options.CancellationToken.ThrowIfCancellationRequested();
    if (relink != null) {
      var live = new HashSet<long>(relink.BlocksInUseAfterMoves);
      if (metadataNames.Count > 0) {
        var step = mover.AllocationBlockSize;
        foreach (var move in moves) {
          if (!IsMetadata(move.FileName)) continue;
          if (step <= 0) { live.Add(move.DstOffset); continue; }
          for (var at = 0L; at < move.Length; at += step)
            live.Add(move.DstOffset + at);
        }
      }

      foreach (var (owner, oldBlocks, newBlocks) in relink.Owners()) {
        options.CancellationToken.ThrowIfCancellationRequested();
        mover.UpdateAllocationScattered(archive, owner, oldBlocks, newBlocks, live);
      }
    }
  }

  private sealed class ChainTracker {
    private readonly Dictionary<string, List<long>> _originalByOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, long> _finalOf = [];

    public ChainTracker(IReadOnlyList<ClusterMove> moves, int blockSize,
        IReadOnlyList<DefragBlockInfo>? layout) {
      var occupant = new Dictionary<long, long>();
      var held = new Dictionary<(int Slot, long At), long>();

      // An owner's blocks have to be relinked in the owner's own order, and the
      // order the moves arrive in is the order it is safe to carry them out —
      // which is not the same thing. A plan that keeps each owner's moves
      // together and in sequence hides the difference; one that interleaves
      // them, as any block-level rearrangement does, hands a chain-based mover
      // the owner's blocks shuffled and the file reads back as noise.
      //
      // Given the layout the plan was made from, the order comes from there:
      // extent maps walk each owner run by run in chain order. Without it the
      // move order is all there is, which is what every caller had before.
      if (layout != null) {
        var moving = new HashSet<string>(moves.Select(m => m.FileName), StringComparer.OrdinalIgnoreCase);
        foreach (var extent in layout) {
          if (extent.Kind != DefragBlockKind.Used) continue;
          var owner = extent.FileName ?? "<unknown>";
          if (!moving.Contains(owner)) continue;
          if (!this._originalByOwner.TryGetValue(owner, out var ordered))
            this._originalByOwner[owner] = ordered = [];

          var unit = blockSize > 0 ? blockSize : extent.Length;
          if (unit <= 0) continue;
          var count = (extent.Length + unit - 1) / unit;
          for (var index = 0L; index < count; ++index) {
            var at = extent.Offset + index * unit;
            if (!occupant.TryAdd(at, at)) continue;
            this._finalOf[at] = at;
            ordered.Add(at);
          }
        }
      }

      foreach (var move in moves) {
        if (!this._originalByOwner.TryGetValue(move.FileName, out var list))
          this._originalByOwner[move.FileName] = list = [];
        var step = blockSize > 0 ? blockSize : move.Length;

        if (move.Staging == DefragStaging.Park) {
          for (var at = 0L; at < move.Length; at += step) {
            var from = move.SrcOffset + at;
            if (!occupant.TryGetValue(from, out var origin)) {
              origin = from;
              this._finalOf[from] = from;
              list.Add(from);
            }
            occupant.Remove(from);
            held[(move.StagingSlot, at)] = origin;
          }
          continue;
        }

        if (move.Staging == DefragStaging.Unpark) {
          for (var at = 0L; at < move.Length; at += step) {
            var to = move.DstOffset + at;
            if (!held.Remove((move.StagingSlot, at), out var origin)) {
              origin = to;
              this._finalOf[to] = to;
              list.Add(to);
            }
            occupant[to] = origin;
            this._finalOf[origin] = to;
          }
          continue;
        }

        for (var at = 0L; at < move.Length; at += step) {
          var from = move.SrcOffset + at;
          var to = move.DstOffset + at;
          if (!occupant.TryGetValue(from, out var origin)) {
            origin = from;
            occupant[from] = from;
            this._finalOf[from] = from;
            list.Add(from);
          }
          occupant[to] = origin;
          this._finalOf[origin] = to;
          if (from != to) occupant.Remove(from);
        }
      }

      this.BlocksInUseAfterMoves = this._finalOf.Values.ToHashSet();
    }

    public IReadOnlySet<long> BlocksInUseAfterMoves { get; }

    public IEnumerable<(string Owner, IReadOnlyList<long> Old, IReadOnlyList<long> New)> Owners() {
      foreach (var (owner, originals) in this._originalByOwner) {
        var finals = originals.Select(o => this._finalOf[o]).ToList();
        yield return (owner, originals, finals);
      }
    }
  }
}
