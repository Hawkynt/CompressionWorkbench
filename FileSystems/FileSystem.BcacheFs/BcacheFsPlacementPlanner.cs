#pragma warning disable CS1591
using Compression.Registry;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// bcachefs-specific physical placement planner. Targets are chosen from the
/// recovered allocation map, not from a synthetic "everything after metadata"
/// range. Complete b-tree nodes are the smallest movable metadata unit; they are
/// never fragmented. User extents remain on their current member by default so
/// relocation cannot accidentally reduce replica durability.
/// </summary>
internal static class BcacheFsPlacementPlanner {
  internal static BcacheFsPlacementPlan Plan(BcacheFsClusterMap map, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(map);
    ArgumentNullException.ThrowIfNull(options);
    if (options.InterleaveStride is < 1 or > 256)
      throw new ArgumentOutOfRangeException(nameof(options.InterleaveStride));

    var diagnostics = new List<string>();
    var planned = new List<BcacheFsPlannedMove>();
    var reserved = new HashSet<(byte Device, long Bucket)>();

    foreach (var device in map.Volume.Devices.Keys.OrderBy(d => d)) {
      var member = map.Member(device);
      if (member == null || member.BucketSizeSectors == 0) continue;

      var movableData = map.Allocations
        .Where(a => a.Device == device && a.Kind == BcacheFsPhysicalKind.UserExtent && a.Movable)
        .OrderBy(a => a.Position?.Inode ?? ulong.MaxValue)
        .ThenBy(a => a.Position?.Snapshot ?? uint.MaxValue)
        .ThenBy(a => a.Position?.Offset ?? ulong.MaxValue)
        .ThenBy(a => a.Sector)
        .ToArray();
      var movableMetadata = options.MetadataZonePlacement == MetadataZone.Unchanged
        ? []
        : map.Allocations
          .Where(a => a.Device == device && a.Kind == BcacheFsPhysicalKind.BtreeNode && a.Movable)
          .OrderBy(MetadataPriority)
          .ThenByDescending(a => a.Level ?? 0)
          .ThenBy(a => a.BtreeId)
          .ThenBy(a => a.Position, NullableBposComparer.Instance)
          .ToArray();

      if (movableData.Length == 0 && movableMetadata.Length == 0) continue;

      var candidates = map.FreeRuns(device, BcacheFsDataType.User)
        .SelectMany(r => Enumerate(r, options.Mode == DefragMode.ConsolidateAtEnd))
        .ToList();
      if (options.Mode == DefragMode.ConsolidateAtEnd) candidates.Reverse();
      if (options.Mode == DefragMode.CarveHole)
        candidates.RemoveAll(bucket => InHole(bucket, member.BucketSizeSectors, options));

      // Metadata may be permitted on a different set of devices than user data.
      // On this member we only use candidates that are legal for both when the
      // requested policy mixes the two classes.
      var metadataCandidates = map.FreeRuns(device, BcacheFsDataType.Btree)
        .SelectMany(r => Enumerate(r, false))
        .Where(b => !InHole(b, member.BucketSizeSectors, options))
        .ToList();

      if (options.Mode == DefragMode.ConsolidateAtEnd) metadataCandidates.Reverse();
      if (options.MetadataZonePlacement == MetadataZone.Middle)
        metadataCandidates = OrderAroundMiddle(metadataCandidates).ToList();

      var dataTargets = PlanDataTargets(movableData, candidates, member.BucketSizeSectors,
        options, reserved, diagnostics, device);

      foreach (var (allocation, targetBucket) in dataTargets) {
        Reserve(reserved, device, targetBucket,
          BucketsFor(allocation.Sectors, member.BucketSizeSectors));
        if (allocation.Sector == targetBucket * (long)member.BucketSizeSectors) continue;
        planned.Add(new BcacheFsPlannedMove(
          allocation,
          device,
          checked(targetBucket * (long)member.BucketSizeSectors),
          BcacheFsPlacementRole.UserData,
          PlacementScore(allocation, targetBucket, member.BucketSizeSectors)));
      }

      if (movableMetadata.Length == 0) continue;
      var metadataTargets = PlanMetadataTargets(map, movableMetadata, metadataCandidates,
        dataTargets, member.BucketSizeSectors, options, reserved, diagnostics, device);
      foreach (var (allocation, targetBucket) in metadataTargets) {
        Reserve(reserved, device, targetBucket,
          BucketsFor(allocation.Sectors, member.BucketSizeSectors));
        if (allocation.Sector == targetBucket * (long)member.BucketSizeSectors) continue;
        planned.Add(new BcacheFsPlannedMove(
          allocation,
          device,
          checked(targetBucket * (long)member.BucketSizeSectors),
          BcacheFsPlacementRole.Metadata,
          PlacementScore(allocation, targetBucket, member.BucketSizeSectors)));
      }
    }

    return new BcacheFsPlacementPlan(
      planned,
      ResolveDependencies(planned, map),
      diagnostics,
      map.Diagnostics,
      options.MetadataZonePlacement != MetadataZone.Unchanged,
      options.InterleaveStride);
  }

  private static List<(BcacheFsPhysicalAllocation Allocation, long TargetBucket)> PlanDataTargets(
      IReadOnlyList<BcacheFsPhysicalAllocation> allocations,
      IReadOnlyList<long> candidates,
      int bucketSectors,
      DefragOptions options,
      IReadOnlySet<(byte Device, long Bucket)> reserved,
      List<string> diagnostics,
      byte device) {
    var result = new List<(BcacheFsPhysicalAllocation, long)>();
    if (allocations.Count == 0) return result;

    var ordered = Interleave(allocations, options.InterleaveStride).ToArray();
    var pool = CandidatePool(candidates, options, allocations).ToArray();
    var cursor = 0;
    foreach (var allocation in ordered) {
      var needed = BucketsFor(allocation.Sectors, bucketSectors);
      var found = FindContiguous(pool, ref cursor, needed, device, reserved);
      if (found < 0) {
        diagnostics.Add($"device {device}: no {needed}-bucket target for {allocation.Label}.");
        continue;
      }
      result.Add((allocation, found));
    }
    return result;
  }

  private static List<(BcacheFsPhysicalAllocation Allocation, long TargetBucket)> PlanMetadataTargets(
      BcacheFsClusterMap map,
      IReadOnlyList<BcacheFsPhysicalAllocation> metadata,
      IReadOnlyList<long> candidates,
      IReadOnlyList<(BcacheFsPhysicalAllocation Allocation, long TargetBucket)> dataTargets,
      int bucketSectors,
      DefragOptions options,
      HashSet<(byte Device, long Bucket)> reserved,
      List<string> diagnostics,
      byte device) {
    var result = new List<(BcacheFsPhysicalAllocation, long)>();
    var available = candidates
      .Where(b => !reserved.Contains((device, b)))
      .Distinct()
      .ToList();

    IEnumerable<BcacheFsPhysicalAllocation> ordered = metadata;
    if (options.MetadataZonePlacement == MetadataZone.BeforeContent)
      ordered = metadata
        .OrderBy(a => AnchorFor(a, dataTargets))
        .ThenBy(MetadataPriority)
        .ThenByDescending(a => a.Level ?? 0)
        .ThenBy(a => a.Position, NullableBposComparer.Instance);

    foreach (var allocation in ordered) {
      var needed = BucketsFor(allocation.Sectors, bucketSectors);
      long target;
      if (options.MetadataZonePlacement == MetadataZone.BeforeContent) {
        var anchor = AnchorFor(allocation, dataTargets);
        target = FindNearestBefore(available, anchor, needed, device, reserved);
        if (target < 0)
          target = FindNearest(available, anchor, needed, device, reserved);
      } else {
        target = FindZoneTarget(available, needed, device, reserved,
          options.MetadataZonePlacement);
      }

      if (target < 0) {
        diagnostics.Add($"device {device}: no metadata target for {allocation.Label}.");
        continue;
      }

      result.Add((allocation, target));
      Reserve(reserved, device, target, needed);
      available.RemoveAll(b => b >= target && b < target + needed);
    }

    return result;
  }

  private static IEnumerable<BcacheFsPhysicalAllocation> Interleave(
      IReadOnlyList<BcacheFsPhysicalAllocation> allocations,
      int stride) {
    if (stride <= 1) return allocations;

    var owners = allocations
      .GroupBy(a => (a.Position?.Inode ?? ulong.MaxValue, a.Position?.Snapshot ?? uint.MaxValue))
      .OrderBy(g => g.Min(a => a.Sector))
      .Select(g => new Queue<BcacheFsPhysicalAllocation>(g.OrderBy(a => a.Position?.Offset ?? ulong.MaxValue)))
      .ToArray();

    var result = new List<BcacheFsPhysicalAllocation>(allocations.Count);
    for (var batchStart = 0; batchStart < owners.Length; batchStart += stride) {
      var batch = owners.Skip(batchStart).Take(stride).ToArray();
      while (batch.Any(q => q.Count != 0))
        foreach (var queue in batch)
          if (queue.Count != 0)
            result.Add(queue.Dequeue());
    }
    return result;
  }

  private static IEnumerable<long> CandidatePool(
      IReadOnlyList<long> candidates,
      DefragOptions options,
      IReadOnlyList<BcacheFsPhysicalAllocation> allocations) {
    if (options.Mode != DefragMode.FillHolesLazy) return candidates;
    var highestSource = allocations.Max(a => a.Sector);
    return candidates.Where(b => b * (long)BucketSectors < highestSource);
  }

  private static long FindContiguous(
      IReadOnlyList<long> pool,
      ref int cursor,
      long count,
      byte device,
      IReadOnlySet<(byte Device, long Bucket)> reserved) {
    for (var i = cursor; i < pool.Count; ++i) {
      var start = pool[i];
      var ok = true;
      for (var n = 0L; n < count; ++n) {
        if (reserved.Contains((device, start + n)) || !pool.Contains(start + n)) {
          ok = false;
          break;
        }
      }
      if (!ok) continue;
      cursor = i + checked((int)count);
      return start;
    }
    return -1;
  }

  private static long FindZoneTarget(
      IReadOnlyList<long> available,
      long count,
      byte device,
      IReadOnlySet<(byte Device, long Bucket)> reserved,
      MetadataZone zone) {
    IEnumerable<long> starts = zone switch {
      MetadataZone.Back => available.OrderByDescending(b => b),
      MetadataZone.Middle => OrderAroundMiddle(available),
      _ => available.OrderBy(b => b),
    };
    foreach (var start in starts)
      if (HasRun(available, start, count, device, reserved))
        return start;
    return -1;
  }

  private static long FindNearestBefore(
      IReadOnlyList<long> available,
      long anchor,
      long count,
      byte device,
      IReadOnlySet<(byte Device, long Bucket)> reserved) {
    foreach (var start in available.Where(b => b < anchor).OrderByDescending(b => b))
      if (start + count <= anchor && HasRun(available, start, count, device, reserved))
        return start;
    return -1;
  }

  private static long FindNearest(
      IReadOnlyList<long> available,
      long anchor,
      long count,
      byte device,
      IReadOnlySet<(byte Device, long Bucket)> reserved) {
    foreach (var start in available.OrderBy(b => Math.Abs(b - anchor)))
      if (HasRun(available, start, count, device, reserved))
        return start;
    return -1;
  }

  private static bool HasRun(
      IReadOnlyList<long> available,
      long start,
      long count,
      byte device,
      IReadOnlySet<(byte Device, long Bucket)> reserved) {
    for (var n = 0L; n < count; ++n)
      if (reserved.Contains((device, start + n)) || !available.Contains(start + n))
        return false;
    return true;
  }

  private static long AnchorFor(
      BcacheFsPhysicalAllocation metadata,
      IReadOnlyList<(BcacheFsPhysicalAllocation Allocation, long TargetBucket)> dataTargets) {
    if (dataTargets.Count == 0) return long.MaxValue / 2;
    if (metadata.BtreeId == BcacheFsBtreeId.Extents && metadata.Position is { } p) {
      var sameOrNext = dataTargets
        .Where(t => (t.Allocation.Position?.Inode ?? ulong.MaxValue) >= p.Inode)
        .Select(t => t.TargetBucket)
        .DefaultIfEmpty(dataTargets[0].TargetBucket)
        .Min();
      return sameOrNext;
    }
    return dataTargets.Min(t => t.TargetBucket);
  }

  private static int MetadataPriority(BcacheFsPhysicalAllocation allocation)
    => allocation.BtreeId switch {
      BcacheFsBtreeId.Extents => 0,
      BcacheFsBtreeId.Inodes => 1,
      BcacheFsBtreeId.Dirents => 2,
      BcacheFsBtreeId.Xattrs => 3,
      BcacheFsBtreeId.Subvolumes => 4,
      BcacheFsBtreeId.Snapshots => 5,
      BcacheFsBtreeId.Alloc => 90,
      BcacheFsBtreeId.Freespace => 91,
      BcacheFsBtreeId.Backpointers => 92,
      BcacheFsBtreeId.Accounting => 93,
      _ => 50,
    };

  private static double PlacementScore(
      BcacheFsPhysicalAllocation allocation,
      long targetBucket,
      int bucketSectors) {
    var sourceBucket = allocation.Sector / bucketSectors;
    var distance = Math.Abs(targetBucket - sourceBucket);
    var metadataBonus = allocation.Kind == BcacheFsPhysicalKind.BtreeNode ? 0.25 : 0;
    return distance + metadataBonus;
  }

  private static IReadOnlyList<BcacheFsPlacementStep> ResolveDependencies(
      IReadOnlyList<BcacheFsPlannedMove> moves,
      BcacheFsClusterMap map) {
    // All destinations currently come from allocator-free buckets, so there are
    // no source/destination cycles. Keep the dependency representation explicit:
    // the executor also consumes plans produced after future compaction passes
    // where a target may intentionally be another source bucket.
    var result = new List<BcacheFsPlacementStep>(moves.Count);
    foreach (var move in moves.OrderBy(m => m.Role).ThenBy(m => m.TargetDevice).ThenBy(m => m.TargetSector))
      result.Add(new BcacheFsPlacementStep(BcacheFsPlacementStepKind.Copy, move, -1));
    return result;
  }

  private static IEnumerable<long> Enumerate(BcacheFsClusterRun run, bool reverse) {
    if (!reverse) {
      for (var bucket = run.FirstBucket; bucket < run.EndBucket; ++bucket) yield return bucket;
      yield break;
    }
    for (var bucket = run.EndBucket - 1; bucket >= run.FirstBucket; --bucket) yield return bucket;
  }

  private static IEnumerable<long> OrderAroundMiddle(IReadOnlyList<long> candidates) {
    if (candidates.Count == 0) yield break;
    var ordered = candidates.OrderBy(b => b).ToArray();
    var middle = ordered[ordered.Length / 2];
    foreach (var bucket in ordered.OrderBy(b => Math.Abs(b - middle))) yield return bucket;
  }

  private static bool InHole(long bucket, int bucketSectors, DefragOptions options) {
    if (options.Mode != DefragMode.CarveHole || options.HoleSize <= 0) return false;
    var holeAt = options.HoleAt >= 0 ? options.HoleAt : 0;
    var start = bucket * (long)bucketSectors * SectorSize;
    var end = start + (long)bucketSectors * SectorSize;
    return start < holeAt + options.HoleSize && holeAt < end;
  }

  private static long BucketsFor(long sectors, int bucketSectors)
    => Math.Max(1, (sectors + bucketSectors - 1) / bucketSectors);

  private static void Reserve(HashSet<(byte Device, long Bucket)> reserved, byte device, long start, long count) {
    for (var i = 0L; i < count; ++i) reserved.Add((device, start + i));
  }

  private sealed class NullableBposComparer : IComparer<Bpos?> {
    internal static readonly NullableBposComparer Instance = new();
    public int Compare(Bpos? x, Bpos? y) {
      if (x is null) return y is null ? 0 : 1;
      if (y is null) return -1;
      return BcacheFsFormat.Compare(x.Value, y.Value);
    }
  }
}

internal enum BcacheFsPlacementRole : byte {
  Metadata,
  UserData,
}

internal sealed record BcacheFsPlannedMove(
  BcacheFsPhysicalAllocation Source,
  byte TargetDevice,
  long TargetSector,
  BcacheFsPlacementRole Role,
  double Score) {
  internal long Sectors => this.Source.Sectors;
}

internal enum BcacheFsPlacementStepKind : byte {
  Copy,
  Park,
  Unpark,
  PublishRoots,
  Reclaim,
}

internal sealed record BcacheFsPlacementStep(
  BcacheFsPlacementStepKind Kind,
  BcacheFsPlannedMove Move,
  int StagingSlot);

internal sealed record BcacheFsPlacementPlan(
  IReadOnlyList<BcacheFsPlannedMove> Moves,
  IReadOnlyList<BcacheFsPlacementStep> Steps,
  IReadOnlyList<string> Diagnostics,
  IReadOnlyList<string> MapDiagnostics,
  bool MovesMetadata,
  int InterleaveStride) {
  internal bool Complete => this.Diagnostics.Count == 0;
}
