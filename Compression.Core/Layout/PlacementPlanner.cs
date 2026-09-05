#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Core.Layout;

/// <summary>
/// Puts one named owner at one chosen offset, moving whatever is in the way out
/// of the way first.
/// </summary>
/// <remarks>
/// <para>Two halves. Clearing the target is the eviction the carve-hole mode
/// already does — <see cref="DefragPlanner.EvictFromRegions" /> is that code,
/// shared rather than written twice. Laying the owner down afterwards is the
/// new half: its blocks take the cluster slots from the target upward, in the
/// owner's own order.</para>
///
/// <para>Where nothing is in the way the owner ends up in one run. Where a bad
/// block or a reserved table sits inside the span, the owner steps over it and
/// carries on above — so it comes out split, but every block still sits above
/// the one before it. That is the property <see cref="AscendingBlockOrder" />
/// names, and this planner never emits a layout that breaks it: the slots are
/// handed out in address order, so it holds by construction.</para>
///
/// <para>A request that cannot be honoured is refused before a byte moves. The
/// owner has to exist, the target has to be a real cluster boundary inside the
/// volume that is not itself reserved, the volume has to have room for the
/// owner from there upward, and everything already sitting in the way has to
/// have somewhere else to go.</para>
/// </remarks>
public static class PlacementPlanner {

  /// <summary>
  /// Plans the moves that put <paramref name="owner" /> at
  /// <paramref name="targetOffset" />.
  /// </summary>
  /// <param name="extents">Current layout, from
  /// <see cref="IFilesystemExtentMap.EnumerateExtents" />, in chain order per
  /// owner.</param>
  /// <param name="owner">The owner to place. Matched case-insensitively.</param>
  /// <param name="targetOffset">Byte offset its first block must end up at. Has
  /// to be on the volume's cluster grid — <paramref name="dataOrigin" /> plus a
  /// whole number of clusters.</param>
  /// <param name="dataOrigin">First byte the data area may use.</param>
  /// <param name="imageSize">Byte just past the last one the volume owns.</param>
  /// <param name="clusterSize">One allocation block, in bytes.</param>
  /// <param name="allowMemoryStaging">Whether a run may be held outside the
  /// volume to unwind a cycle.</param>
  /// <returns>Ordered moves, empty when the owner already sits there.</returns>
  /// <exception cref="InvalidOperationException">The request cannot be honoured
  /// at all. Nothing is planned and nothing is changed.</exception>
  public static IReadOnlyList<ClusterMove> Plan(
      IReadOnlyList<DefragBlockInfo> extents,
      string owner,
      long targetOffset,
      long dataOrigin,
      long imageSize,
      int clusterSize,
      bool allowMemoryStaging = true)
    => DefragPlanner.Validate(
      PlanCore(extents, owner, targetOffset, dataOrigin, imageSize, clusterSize, allowMemoryStaging),
      imageSize, extents);

  /// <summary>
  /// The addresses <paramref name="owner" />'s blocks would end up at. Ascending
  /// by construction, contiguous except where a reserved or bad region forces a
  /// step over it.
  /// </summary>
  public static IReadOnlyList<long> TargetSlots(
      IReadOnlyList<DefragBlockInfo> extents,
      string owner,
      long targetOffset,
      long dataOrigin,
      long imageSize,
      int clusterSize) {
    var (blocks, forbidden) = Survey(extents, owner, targetOffset, dataOrigin, imageSize, clusterSize);
    return Slots(targetOffset, blocks.Count, imageSize, clusterSize, forbidden, owner);
  }

  private static (List<long> Blocks, List<(long Start, long End)> Forbidden) Survey(
      IReadOnlyList<DefragBlockInfo> extents, string owner, long targetOffset,
      long dataOrigin, long imageSize, int clusterSize) {
    ArgumentNullException.ThrowIfNull(extents);
    ArgumentException.ThrowIfNullOrEmpty(owner);
    if (clusterSize <= 0) throw new ArgumentOutOfRangeException(nameof(clusterSize));

    var forbiddenRaw = new List<(long Start, long End)>();
    var blocks = new List<long>();
    var found = false;

    foreach (var extent in extents) {
      switch (extent.Kind) {
        case DefragBlockKind.MetadataReserved:
        case DefragBlockKind.Bad:
          forbiddenRaw.Add((extent.Offset, extent.Offset + extent.Length));
          continue;
        case DefragBlockKind.Used:
          break;
        default:
          continue;
      }

      if (!string.Equals(extent.FileName ?? "<unknown>", owner, StringComparison.OrdinalIgnoreCase)) continue;
      found = true;
      if (extent.Length <= 0) continue;

      // Extents arrive in chain order and each is a run of whole blocks, so
      // this list is the owner's logical order — the order the relink writes
      // back and the order the invariant is about.
      var count = (extent.Length + clusterSize - 1) / clusterSize;
      for (var index = 0L; index < count; ++index)
        blocks.Add(extent.Offset + index * clusterSize);
    }

    if (!found)
      throw new InvalidOperationException(
        $"Cannot place '{owner}': the volume holds no owner by that name. Nothing was changed.");
    if (blocks.Count == 0)
      throw new InvalidOperationException(
        $"Cannot place '{owner}': it occupies no blocks, so there is nothing to put anywhere. " +
        "Nothing was changed.");

    if (targetOffset < dataOrigin || targetOffset >= imageSize)
      throw new InvalidOperationException(
        $"Cannot place '{owner}' at {targetOffset:N0}: the volume's data area is " +
        $"[{dataOrigin:N0}..{imageSize:N0}). Nothing was changed.");

    // The grid the volume's own clusters sit on. A target off it names a place
    // no cluster begins, so the owner could not start exactly there and saying
    // it did would be a lie.
    var offGrid = (targetOffset - dataOrigin) % clusterSize;
    if (offGrid != 0)
      throw new InvalidOperationException(
        $"Cannot place '{owner}' at {targetOffset:N0}: cluster boundaries are at {dataOrigin:N0} " +
        $"plus a multiple of {clusterSize:N0}, so the nearest are {targetOffset - offGrid:N0} and " +
        $"{targetOffset - offGrid + clusterSize:N0}. Nothing was changed.");

    var forbidden = DefragPlanner.MergeIntervals(forbiddenRaw);

    // The whole first cluster has to be available, not merely the byte the
    // target names. A reserved region that begins inside it would make the
    // owner step over and start above — somewhere the caller did not ask for,
    // reported as though it had.
    foreach (var (start, end) in forbidden)
      if (targetOffset < end && start < targetOffset + clusterSize)
        throw new InvalidOperationException(
          $"Cannot place '{owner}' at {targetOffset:N0}: [{start:N0}..{end:N0}) is reserved by the " +
          "volume or marked bad, so the first cluster cannot go there. Nothing was changed.");

    return (blocks, forbidden);
  }

  /// <summary>
  /// Hands out <paramref name="count" /> cluster slots from
  /// <paramref name="from" /> upward, stepping over anything reserved.
  /// </summary>
  private static List<long> Slots(long from, int count, long imageSize, int clusterSize,
      List<(long Start, long End)> forbidden, string owner) {
    var slots = new List<long>(count);
    var at = from;

    while (slots.Count < count) {
      if (at + clusterSize > imageSize)
        throw new InvalidOperationException(
          $"Cannot place '{owner}' at {from:N0}: it needs {count:N0} cluster(s) and only " +
          $"{slots.Count:N0} fit between there and the end of the volume at {imageSize:N0}. " +
          "Nothing was changed.");

      var clear = true;
      foreach (var (start, end) in forbidden) {
        if (end <= at) continue;
        if (start >= at + clusterSize) break;
        // Step over it. The owner comes out split here and the block after the
        // gap still sits above the block before it, which is the whole promise.
        at = end + (clusterSize - (end - from) % clusterSize) % clusterSize;
        clear = false;
        break;
      }
      if (!clear) continue;

      slots.Add(at);
      at += clusterSize;
    }

    if (slots[0] != from)
      throw new InvalidOperationException(
        $"Cannot place '{owner}' at {from:N0}: the first cluster it could be given is {slots[0]:N0}. " +
        "Nothing was changed.");
    return slots;
  }

  private static IReadOnlyList<ClusterMove> PlanCore(
      IReadOnlyList<DefragBlockInfo> extents,
      string owner,
      long targetOffset,
      long dataOrigin,
      long imageSize,
      int clusterSize,
      bool allowMemoryStaging) {
    var (blocks, forbidden) = Survey(extents, owner, targetOffset, dataOrigin, imageSize, clusterSize);
    var slots = Slots(targetOffset, blocks.Count, imageSize, clusterSize, forbidden, owner);

    if (blocks.Count > DefragPlanner.MaxPlannableExtents)
      throw new InvalidOperationException(
        $"Cannot place '{owner}': {blocks.Count:N0} block(s) exceed the " +
        $"{DefragPlanner.MaxPlannableExtents:N0} this planner resolves; rebuild the volume instead.");

    // Already exactly there: the slots it would be given are the slots it holds.
    var settled = true;
    for (var i = 0; i < blocks.Count && settled; ++i) settled = blocks[i] == slots[i];
    if (settled) return [];

    var live = new List<DefragBlockInfo>();
    var freeRegions = new List<(long Offset, long Length)>();
    foreach (var extent in extents)
      switch (extent.Kind) {
        case DefragBlockKind.Used: live.Add(extent); break;
        case DefragBlockKind.Free: freeRegions.Add((extent.Offset, extent.Length)); break;
      }
    DefragPlanner.AddUnclaimedSpace(freeRegions, extents, dataOrigin, imageSize);

    // Where the owner is going, expressed as ranges: consecutive slots merge
    // into one, so the eviction sees the same shape a carved hole has.
    var wantedRaw = new List<(long Start, long End)>(slots.Count);
    foreach (var slot in slots) wantedRaw.Add((slot, slot + clusterSize));
    var wanted = DefragPlanner.MergeIntervals(wantedRaw);

    // Free space outside the target, so an eviction never lands back in the
    // region it is clearing — and neither does a staged run, which is why the
    // clipped list is what the ordering pass is given too.
    var safeFree = DefragPlanner.ClipFree(freeRegions, wanted);

    // Whatever the owner holds outside its destination is about to be vacated,
    // so it is somewhere for the evictions to go. Leaving it out refused a
    // placement on any volume without slack — which is most of them, since the
    // owner being moved is usually the largest thing on it, and the room it is
    // giving up is exactly the room the move needs.
    foreach (var extent in live) {
      if (!string.Equals(extent.FileName, owner, StringComparison.OrdinalIgnoreCase)) continue;
      if (extent.Length <= 0) continue;
      var span = Math.Min(extent.Offset + DefragPlanner.AlignUp(extent.Length, clusterSize), imageSize);
      foreach (var (start, end) in DefragPlanner.Subtract(extent.Offset, span, wanted))
        if (end > start) safeFree.Add((start, end - start));
    }

    // The owner itself is not evicted from its own destination: its blocks are
    // permuted into place by the moves below, and the ordering pass sorts out
    // any block that has to leave before another arrives.
    var moves = DefragPlanner.EvictFromRegions(live, wanted, safeFree, clusterSize,
      $"place '{owner}' at {targetOffset:N0}", exempt: owner);


    // The owner's blocks, in the owner's order, into the slots in address
    // order. Runs whose source and destination both step by one cluster are
    // emitted as one move so a long file is not a thousand of them.
    for (var i = 0; i < blocks.Count;) {
      var runLength = 1;
      while (i + runLength < blocks.Count
             && blocks[i + runLength] == blocks[i] + (long)runLength * clusterSize
             && slots[i + runLength] == slots[i] + (long)runLength * clusterSize)
        ++runLength;

      if (blocks[i] != slots[i])
        moves.Add(new ClusterMove(blocks[i], slots[i], (long)runLength * clusterSize, owner));
      i += runLength;
    }

    // Every refusal this verb makes says the volume is untouched, including the
    // ones the shared ordering pass raises. A caller told only that some moves
    // form a cycle cannot tell whether half of them already ran.
    try {
      return DefragPlanner.ResolveDependencies(moves, safeFree, clusterSize, allowMemoryStaging);
    } catch (InvalidOperationException ex) {
      throw new InvalidOperationException(
        $"Cannot place '{owner}' at {targetOffset:N0}: {ex.Message} Nothing was changed.", ex);
    }
  }
}
