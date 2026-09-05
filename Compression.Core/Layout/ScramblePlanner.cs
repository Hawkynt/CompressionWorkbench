#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Core.Layout;

/// <summary>
/// Plans the pessimal layout: every allocation block of every live owner is
/// dealt a slot at random from the whole data area, so a file that was one run
/// comes back as one run per block.
/// </summary>
/// <remarks>
/// <para>It exists because a defragmenter that has only ever been pointed at
/// tidy volumes has not been tested. Nothing in the public surface produced a
/// fragmented volume — the writers lay a volume out from scratch and removing
/// files leaves gaps measured in kilobytes — so every planned-defrag fixture
/// was a volume that barely needed defragmenting.</para>
///
/// <para>The shuffle is a seeded Fisher-Yates over the slot list, so the same
/// seed deals the same layout every time. That is what lets a test assert a
/// specific extent count and a screenshot capture the same picture twice.</para>
/// </remarks>
public static class ScramblePlanner {

  /// <summary>
  /// Deals every live block a new slot and returns the moves that put it there,
  /// in an order that never writes over a block that has not moved yet.
  /// </summary>
  /// <param name="extents">Current layout, from
  /// <see cref="IFilesystemExtentMap.EnumerateExtents" />.</param>
  /// <param name="dataOrigin">First byte the data area may use.</param>
  /// <param name="imageSize">Byte just past the last one the volume owns.</param>
  /// <param name="clusterSize">One allocation block, in bytes.</param>
  /// <param name="seed">Seeds the shuffle; the same seed deals the same layout.</param>
  /// <param name="allowMemoryStaging">Whether a block may be held outside the
  /// volume to unwind a cycle. Only for movers that declare
  /// <see cref="IFilesystemBlockMover.SupportsHeldRuns" />.</param>
  public static IReadOnlyList<ClusterMove> Plan(
      IReadOnlyList<DefragBlockInfo> extents,
      long dataOrigin,
      long imageSize,
      int clusterSize,
      int seed,
      bool allowMemoryStaging = true)
    => DefragPlanner.Validate(
      PlanCore(extents, dataOrigin, imageSize, clusterSize, seed, allowMemoryStaging),
      imageSize, extents);

  private static IReadOnlyList<ClusterMove> PlanCore(
      IReadOnlyList<DefragBlockInfo> extents,
      long dataOrigin,
      long imageSize,
      int clusterSize,
      int seed,
      bool allowMemoryStaging) {
    ArgumentNullException.ThrowIfNull(extents);
    if (clusterSize <= 0) throw new ArgumentOutOfRangeException(nameof(clusterSize));

    // What the volume uses to find its own files stays exactly where it is. A
    // superblock, a table, a bitmap or a bad block is not an owner with a chain
    // to relink, and scattering one leaves nothing able to read the volume back
    // — which would make the round trip that proves the content survived
    // impossible to run.
    var live = new List<DefragBlockInfo>();
    var freeRegions = new List<(long Offset, long Length)>();
    var forbiddenRaw = new List<(long Start, long End)>();
    foreach (var extent in extents)
      switch (extent.Kind) {
        case DefragBlockKind.Used:
          live.Add(extent);
          break;
        case DefragBlockKind.Free:
          freeRegions.Add((extent.Offset, extent.Length));
          break;
        case DefragBlockKind.MetadataReserved:
        case DefragBlockKind.Bad:
          forbiddenRaw.Add((extent.Offset, extent.Offset + extent.Length));
          break;
      }

    if (live.Count == 0) return [];
    if (live.Count > DefragPlanner.MaxPlannableExtents)
      throw new InvalidOperationException(
        $"Scramble cannot be planned in place: {live.Count:N0} extents exceed the " +
        $"{DefragPlanner.MaxPlannableExtents:N0} this planner resolves.");

    var forbidden = DefragPlanner.MergeIntervals(forbiddenRaw);
    DefragPlanner.AddUnclaimedSpace(freeRegions, extents, dataOrigin, imageSize);

    // Owners in name order rather than the order the map happened to walk them,
    // so what a seed deals depends on the volume's contents and nothing else.
    var owners = new List<string>();
    var blocksOf = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
    foreach (var extent in live) {
      var owner = extent.FileName ?? "<unknown>";
      if (!blocksOf.TryGetValue(owner, out var blocks)) {
        blocksOf[owner] = blocks = [];
        owners.Add(owner);
      }

      // Extents arrive in chain order, and each is a run of whole blocks. The
      // owner's block list is therefore its logical order, which is the order
      // the relink has to write back.
      var count = (extent.Length + clusterSize - 1) / clusterSize;
      for (var block = 0L; block < count; ++block)
        blocks.Add(extent.Offset + block * clusterSize);
    }
    owners.Sort(StringComparer.Ordinal);

    var slots = UsableSlots(DefragPlanner.AlignUp(dataOrigin, clusterSize), imageSize, clusterSize, forbidden);
    var needed = 0L;
    foreach (var owner in owners) needed += blocksOf[owner].Count;
    if (needed > slots.Length)
      throw new InvalidOperationException(
        $"Scramble cannot be planned in place: {needed:N0} live block(s) do not fit in the " +
        $"{slots.Length:N0} slot(s) the data area offers.");
    if (needed > DefragPlanner.MaxPlannableMoves)
      throw new InvalidOperationException(
        $"Scramble cannot be planned in place: {needed:N0} block moves exceed the " +
        $"{DefragPlanner.MaxPlannableMoves:N0} this planner resolves.");

    Shuffle(slots, seed);

    // Every block takes the next slot off the shuffled list. Because the list
    // is shuffled and not the assignment, an owner's blocks are as scattered as
    // the volume allows rather than merely permuted among themselves.
    var raw = new List<ClusterMove>();
    var taken = 0;
    foreach (var owner in owners)
      foreach (var source in blocksOf[owner]) {
        var destination = slots[taken++];
        if (destination == source) continue;
        raw.Add(new ClusterMove(source, destination, clusterSize, owner));
      }

    if (raw.Count == 0) return [];

    // Slots nothing starts on and nothing lands on. One of them is all a cycle
    // needs to unwind without leaving the volume.
    var occupied = new HashSet<long>();
    foreach (var owner in owners)
      foreach (var source in blocksOf[owner])
        occupied.Add(source);
    var claimed = new HashSet<long>();
    foreach (var move in raw) claimed.Add(move.DstOffset);
    var spare = new List<long>();
    foreach (var slot in slots)
      if (!occupied.Contains(slot) && !claimed.Contains(slot)) spare.Add(slot);
    spare.Sort();

    return Order(raw, occupied, spare, allowMemoryStaging);
  }

  /// <summary>Every cluster-aligned slot of the data area, in address order.</summary>
  private static long[] UsableSlots(long origin, long imageSize, int clusterSize,
      List<(long Start, long End)> forbidden) {
    var runs = DefragPlanner.UsableClusterRuns(origin, imageSize, clusterSize, forbidden);
    var total = 0L;
    foreach (var (_, clusters) in runs) total += clusters;
    if (total > int.MaxValue)
      throw new InvalidOperationException("Scramble cannot be planned in place: the data area has more slots than can be shuffled.");

    var slots = new long[total];
    var at = 0;
    foreach (var (start, clusters) in runs)
      for (var index = 0L; index < clusters; ++index)
        slots[at++] = start + index * clusterSize;
    return slots;
  }

  /// <summary>
  /// Fisher-Yates, driven by a generator written out here rather than taken from
  /// the runtime.
  /// </summary>
  /// <remarks>
  /// A seed is only worth having if it deals the same layout on every machine
  /// and every runtime version. SplitMix64 is four lines and fixes that
  /// forever; a shared library's generator is free to change its sequence.
  /// </remarks>
  private static void Shuffle(long[] slots, int seed) {
    var state = (ulong)(uint)seed * 0x9E3779B97F4A7C15ul + 0x1234567890ABCDEFul;

    ulong Next() {
      state += 0x9E3779B97F4A7C15ul;
      var z = state;
      z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ul;
      z = (z ^ (z >> 27)) * 0x94D049BB133111EBul;
      return z ^ (z >> 31);
    }

    // Rejection sampling: taking the remainder straight would favour the low
    // slots, and a shuffle that favours anything is not one.
    ulong Below(ulong bound) {
      var limit = ulong.MaxValue - ulong.MaxValue % bound;
      ulong value;
      do value = Next(); while (value >= limit);
      return value % bound;
    }

    for (var i = slots.Length - 1; i > 0; --i) {
      var j = (int)Below((ulong)(i + 1));
      (slots[i], slots[j]) = (slots[j], slots[i]);
    }
  }

  /// <summary>
  /// Orders the moves so that nothing is ever written over a block that has not
  /// been read out yet, breaking the shuffle's cycles as it goes.
  /// </summary>
  /// <remarks>
  /// <para>A scramble is a permutation of distinct slots, all one block long,
  /// which is a much simpler shape than the general planner's overlapping runs:
  /// each slot has at most one move heading for it and at most one move
  /// starting on it. So a block can go the moment its destination is empty, and
  /// running a block empties exactly one slot — the one it came from — which is
  /// the only thing that can release another block. That makes the whole
  /// ordering one sweep of a ready queue instead of the general resolver's
  /// repeated all-pairs scan, which on a volume-wide permutation is the
  /// difference between a second and an hour.</para>
  ///
  /// <para>What is left when the queue empties is cycles. One block of a cycle
  /// is taken out of its slot — hopped through a spare slot where the volume has
  /// one, held outside the volume where it has not — and the rest follows it.</para>
  /// </remarks>
  private static List<ClusterMove> Order(List<ClusterMove> raw, HashSet<long> occupied,
      List<long> spare, bool allowMemoryStaging) {
    var pendingFrom = new Dictionary<long, ClusterMove>(raw.Count);
    var pendingTo = new Dictionary<long, ClusterMove>(raw.Count);
    foreach (var move in raw) {
      pendingFrom[move.SrcOffset] = move;
      pendingTo[move.DstOffset] = move;
    }

    var held = new Dictionary<long, int>();
    var result = new List<ClusterMove>(raw.Count + 8);
    var ready = new Queue<ClusterMove>();
    // Address order so the plan a seed produces is the same list every run,
    // down to the order the moves are written in.
    foreach (var move in raw.OrderBy(m => m.SrcOffset))
      if (!occupied.Contains(move.DstOffset)) ready.Enqueue(move);

    var spareAt = 0;
    var slot = 0;
    var breaks = 0;

    void Run(ClusterMove move) {
      pendingFrom.Remove(move.SrcOffset);
      pendingTo.Remove(move.DstOffset);

      if (held.Remove(move.SrcOffset, out var stagingSlot)) {
        // Held runs left their slot when they were lifted; putting one down
        // repoints it from where the volume still thinks it is.
        result.Add(move with { Staging = DefragStaging.Unpark, StagingSlot = stagingSlot });
      } else {
        result.Add(move);
        occupied.Remove(move.SrcOffset);
        Release(move.SrcOffset);
      }
    }

    void Release(long freed) {
      if (pendingTo.TryGetValue(freed, out var waiting)) ready.Enqueue(waiting);
    }

    while (true) {
      while (ready.Count > 0) {
        var move = ready.Dequeue();
        if (!pendingFrom.TryGetValue(move.SrcOffset, out var current) || !ReferenceEquals(current, move))
          continue;
        if (occupied.Contains(move.DstOffset)) continue;
        Run(move);
      }

      if (pendingFrom.Count == 0) break;
      if (++breaks > raw.Count + 8)
        throw new InvalidOperationException(
          "Scramble cannot be planned in place: the moves keep blocking each other after being " +
          "taken out of the way, so no safe order exists.");

      // Nothing can go, so everything left is on a cycle — any of them will do.
      // Each remaining block's destination is held by another remaining block,
      // and destinations are distinct, so "the block holding my destination" is
      // a one-to-one map of what is left onto itself. A map like that is made of
      // cycles and nothing else: there is no chain feeding into one, so no way
      // to pick a block whose slot nothing is waiting for.
      //
      // Lowest address, so a seed deals the same plan every run.
      var stuck = pendingFrom.Values.OrderBy(m => m.SrcOffset).First();

      if (spareAt < spare.Count) {
        // A spare slot the shuffle handed to nobody: hop the block through it,
        // which keeps the whole pass inside the volume.
        var staging = spare[spareAt++];
        result.Add(new ClusterMove(stuck.SrcOffset, staging, stuck.Length, stuck.FileName));
        var rest = new ClusterMove(staging, stuck.DstOffset, stuck.Length, stuck.FileName);
        pendingFrom.Remove(stuck.SrcOffset);
        pendingFrom[staging] = rest;
        pendingTo[stuck.DstOffset] = rest;
        occupied.Remove(stuck.SrcOffset);
        Release(stuck.SrcOffset);
        if (!occupied.Contains(rest.DstOffset)) ready.Enqueue(rest);
        continue;
      }

      if (!allowMemoryStaging)
        throw new InvalidOperationException(
          "Scramble cannot be planned in place: the shuffle leaves a cycle of blocks that each " +
          "hold the next one's destination, the volume has no spare block to hop one through, " +
          "and this mover cannot hold a run outside the volume.");

      result.Add(new ClusterMove(stuck.SrcOffset, stuck.SrcOffset, stuck.Length, stuck.FileName) {
        Staging = DefragStaging.Park,
        StagingSlot = slot,
      });
      held[stuck.SrcOffset] = slot++;
      occupied.Remove(stuck.SrcOffset);
      Release(stuck.SrcOffset);
      if (!occupied.Contains(stuck.DstOffset)) ready.Enqueue(stuck);
    }

    if (pendingFrom.Count > 0)
      throw new InvalidOperationException(
        $"Scramble cannot be planned in place: {pendingFrom.Count} block(s) have no order that " +
        "avoids writing over a block that has not moved yet.");

    return result;
  }
}
