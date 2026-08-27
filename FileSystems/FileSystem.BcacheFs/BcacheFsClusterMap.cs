#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Physical, per-member view of a recovered bcachefs filesystem. Unlike the
/// generic defrag block map this represents allocation buckets, superblocks,
/// journal ranges, b-tree nodes and every discoverable data replica separately.
/// It is deliberately conservative: discrepancies are retained as diagnostics
/// and make the affected buckets non-allocatable instead of guessing ownership.
/// </summary>
internal sealed class BcacheFsClusterMap {
  private readonly Dictionary<(byte Device, long Bucket), BcacheFsClusterBucket> _buckets;

  private BcacheFsClusterMap(
      BcacheFsCoreVolume volume,
      Dictionary<(byte Device, long Bucket), BcacheFsClusterBucket> buckets,
      IReadOnlyList<BcacheFsPhysicalAllocation> allocations,
      IReadOnlyList<string> diagnostics) {
    this.Volume = volume;
    this._buckets = buckets;
    this.Allocations = allocations;
    this.Diagnostics = diagnostics;
  }

  internal BcacheFsCoreVolume Volume { get; }
  internal IReadOnlyList<BcacheFsPhysicalAllocation> Allocations { get; }
  internal IReadOnlyList<string> Diagnostics { get; }

  internal IEnumerable<BcacheFsClusterBucket> Buckets
    => this._buckets.Values.OrderBy(b => b.Device).ThenBy(b => b.Bucket);

  internal BcacheFsClusterBucket? Bucket(byte device, long bucket)
    => this._buckets.GetValueOrDefault((device, bucket));

  internal IEnumerable<BcacheFsClusterRun> FreeRuns(byte device, BcacheFsDataType targetType) {
    var member = Member(device);
    if (member == null || !CanWriteType(member, targetType)) yield break;

    var free = this.Buckets
      .Where(b => b.Device == device && b.Reusable && b.Overlays.Count == 0)
      .OrderBy(b => b.Bucket)
      .ToArray();
    if (free.Length == 0) yield break;

    var start = free[0].Bucket;
    var previous = start;
    for (var i = 1; i < free.Length; ++i) {
      if (free[i].Bucket == previous + 1) {
        previous = free[i].Bucket;
        continue;
      }
      yield return new BcacheFsClusterRun(device, start, previous - start + 1);
      start = previous = free[i].Bucket;
    }
    yield return new BcacheFsClusterRun(device, start, previous - start + 1);
  }

  internal bool IsTargetRangeFree(
      byte device,
      long sector,
      long sectors,
      BcacheFsDataType targetType,
      IReadOnlySet<(byte Device, long Bucket)>? additionallyReserved = null) {
    if (sectors <= 0) return false;
    var member = Member(device);
    if (member == null || !CanWriteType(member, targetType) || member.BucketSizeSectors == 0)
      return false;
    var first = Math.DivRem(sector, member.BucketSizeSectors, out var sectorOffset);
    if (sectorOffset != 0) return false;
    var count = (sectors + member.BucketSizeSectors - 1) / member.BucketSizeSectors;
    for (var i = 0L; i < count; ++i) {
      var key = (device, first + i);
      if (additionallyReserved?.Contains(key) == true) return false;
      if (!this._buckets.TryGetValue(key, out var bucket) || !bucket.Reusable || bucket.Overlays.Count != 0)
        return false;
    }
    return true;
  }

  internal BcacheFsMemberRecord? Member(byte device)
    => device < this.Volume.Members.Count ? this.Volume.Members[device] : null;

  internal static bool CanWriteType(BcacheFsMemberRecord member, BcacheFsDataType type) {
    if (member.State != BcacheFsMemberState.ReadWrite || member.BucketSizeSectors == 0)
      return false;
    var bit = (int)type;
    if (bit is < 0 or >= 5) return false;
    return (member.DataAllowed & (1 << bit)) != 0;
  }

  internal static BcacheFsClusterMap Build(BcacheFsCoreVolume volume) {
    ArgumentNullException.ThrowIfNull(volume);
    var diagnostics = new List<string>(volume.Diagnostics);
    var buckets = new Dictionary<(byte Device, long Bucket), BcacheFsClusterBucket>();
    var allocations = new List<BcacheFsPhysicalAllocation>();

    for (var dev = 0; dev < volume.Members.Count; ++dev) {
      var member = volume.Members[dev];
      if (member.BucketCount > long.MaxValue) {
        diagnostics.Add($"member {dev} bucket count exceeds signed addressing.");
        continue;
      }
      for (var bucket = (long)member.FirstBucket; bucket < (long)member.BucketCount; ++bucket)
        buckets[((byte)dev, bucket)] = new BcacheFsClusterBucket(
          (byte)dev, bucket, member.BucketSizeSectors,
          BcacheFsDataType.Unknown, false, 0, 0, []);
    }

    ApplyAllocationTree(volume, buckets, diagnostics);
    ApplyFreespaceTree(volume, buckets, diagnostics);
    ApplySuperblocks(volume, buckets, allocations, diagnostics);
    ApplyJournals(volume, buckets, allocations, diagnostics);
    ApplyBtrees(volume, buckets, allocations, diagnostics);
    ApplyExtentReplicas(volume, buckets, allocations, diagnostics);
    ValidateOverlays(buckets, diagnostics);

    return new BcacheFsClusterMap(volume, buckets, allocations, diagnostics);
  }

  private static void ApplyAllocationTree(
      BcacheFsCoreVolume volume,
      Dictionary<(byte Device, long Bucket), BcacheFsClusterBucket> buckets,
      List<string> diagnostics) {
    var tree = BcacheFsBtreeReader.ReadTree(volume, BcacheFsBtreeId.Alloc);
    diagnostics.AddRange(tree.Diagnostics.Select(d => "alloc: " + d));
    if (!tree.Complete) {
      diagnostics.Add("allocation tree is incomplete; unknown buckets are not reusable.");
      return;
    }

    foreach (var key in tree.MaterializedLeafSlots) {
      if (key.Type != BcacheFsKeyType.AllocV4 || key.Value.Length < 24) continue;
      if (key.Position.Inode > byte.MaxValue || key.Position.Offset > long.MaxValue) {
        diagnostics.Add($"alloc key {key.Position.Inode}:{key.Position.Offset} is outside supported physical addressing.");
        continue;
      }
      var dev = (byte)key.Position.Inode;
      var bucket = (long)key.Position.Offset;
      if (!buckets.TryGetValue((dev, bucket), out var current)) continue;

      var rawType = key.Value[14];
      var dataType = Enum.IsDefined(typeof(BcacheFsDataType), rawType)
        ? (BcacheFsDataType)rawType
        : BcacheFsDataType.Unknown;
      var dirtySectors = BinaryPrimitives.ReadUInt32LittleEndian(key.Value.AsSpan(16));
      var cachedSectors = BinaryPrimitives.ReadUInt32LittleEndian(key.Value.AsSpan(20));
      var journalSeqEmpty = key.Value.Length >= 56
        ? BinaryPrimitives.ReadUInt64LittleEndian(key.Value.AsSpan(48))
        : 0UL;
      var reusable = dataType == BcacheFsDataType.Free && dirtySectors == 0 && cachedSectors == 0
        && journalSeqEmpty == 0;
      buckets[(dev, bucket)] = current with {
        DataType = dataType,
        Reusable = reusable,
        DirtySectors = dirtySectors,
        CachedSectors = cachedSectors,
      };
    }
  }

  /// <summary>
  /// The alloc tree normally omits plain free buckets; the freespace tree is the
  /// authoritative run index for those holes. Overlay it instead of interpreting
  /// a missing alloc_v4 key as either free or occupied. If both trees make
  /// incompatible claims, keep the bucket quarantined and report the conflict.
  /// </summary>
  private static void ApplyFreespaceTree(
      BcacheFsCoreVolume volume,
      Dictionary<(byte Device, long Bucket), BcacheFsClusterBucket> buckets,
      List<string> diagnostics) {
    var tree = BcacheFsBtreeReader.ReadTree(volume, BcacheFsBtreeId.Freespace);
    diagnostics.AddRange(tree.Diagnostics.Select(d => "freespace: " + d));
    if (!tree.Complete) {
      diagnostics.Add("freespace tree is incomplete; omitted alloc buckets remain non-reusable.");
      return;
    }

    foreach (var key in tree.MaterializedLeafSlots) {
      if (key.Type != BcacheFsKeyType.Set || key.Size == 0) continue;
      if (key.Position.Inode > byte.MaxValue || key.Position.Offset > long.MaxValue) {
        diagnostics.Add($"freespace key {key.Position.Inode}:{key.Position.Offset} is outside supported physical addressing.");
        continue;
      }

      var dev = (byte)key.Position.Inode;
      var end = (long)key.Position.Offset;
      var start = end - key.Size;
      if (start < 0 || end < start) {
        diagnostics.Add($"freespace key dev {dev} has invalid run {start}..{end}.");
        continue;
      }

      for (var bucket = start; bucket < end; ++bucket) {
        if (!buckets.TryGetValue((dev, bucket), out var current)) {
          diagnostics.Add($"freespace run dev {dev} bucket {bucket} lies outside member geometry.");
          continue;
        }

        if (current.DataType is not (BcacheFsDataType.Unknown or BcacheFsDataType.Free)
            || current.DirtySectors != 0 || current.CachedSectors != 0) {
          diagnostics.Add(
            $"device {dev} bucket {bucket} is in the freespace tree but alloc says " +
            $"{current.DataType} dirty={current.DirtySectors} cached={current.CachedSectors}.");
          buckets[(dev, bucket)] = current with { Reusable = false };
          continue;
        }

        buckets[(dev, bucket)] = current with {
          DataType = BcacheFsDataType.Free,
          Reusable = true,
          DirtySectors = 0,
          CachedSectors = 0,
        };
      }
    }
  }

  private static void ApplySuperblocks(
      BcacheFsCoreVolume volume,
      Dictionary<(byte Device, long Bucket), BcacheFsClusterBucket> buckets,
      List<BcacheFsPhysicalAllocation> allocations,
      List<string> diagnostics) {
    foreach (var (dev, set) in volume.DeviceSuperblocks) {
      var member = dev < volume.Members.Count ? volume.Members[dev] : null;
      if (member == null || member.BucketSizeSectors == 0) continue;
      foreach (var copy in set.Copies.Where(c => c.StructurallyValid)) {
        var sectors = Math.Max(1L, (copy.RawBytes.LongLength + SectorSize - 1) / SectorSize);
        AddAllocation(buckets, allocations, diagnostics, new BcacheFsPhysicalAllocation(
          BcacheFsPhysicalKind.Superblock, dev, copy.Sector, sectors,
          BcacheFsDataType.Superblock, false, null, null, null,
          $"superblock seq {copy.Sequence}"));
      }
    }
  }

  private static void ApplyJournals(
      BcacheFsCoreVolume volume,
      Dictionary<(byte Device, long Bucket), BcacheFsClusterBucket> buckets,
      List<BcacheFsPhysicalAllocation> allocations,
      List<string> diagnostics) {
    foreach (var (dev, set) in volume.DeviceSuperblocks) {
      var current = set.Current;
      var member = dev < volume.Members.Count ? volume.Members[dev] : null;
      if (current == null || member == null || member.BucketSizeSectors == 0) continue;
      foreach (var range in current.JournalRanges())
        foreach (var bucket in range.Buckets())
          AddAllocation(buckets, allocations, diagnostics, new BcacheFsPhysicalAllocation(
            BcacheFsPhysicalKind.Journal, dev,
            checked(bucket * (long)member.BucketSizeSectors), member.BucketSizeSectors,
            BcacheFsDataType.Journal, false, null, null, null, "journal bucket"));
    }
  }

  private static void ApplyBtrees(
      BcacheFsCoreVolume volume,
      Dictionary<(byte Device, long Bucket), BcacheFsClusterBucket> buckets,
      List<BcacheFsPhysicalAllocation> allocations,
      List<string> diagnostics) {
    foreach (var id in BcacheFsOnDiskCatalog.KnownBtrees) {
      if (volume.Root(id) == null) continue;
      var tree = BcacheFsBtreeReader.ReadTree(volume, id);
      diagnostics.AddRange(tree.Diagnostics.Select(d => $"{id}: {d}"));
      foreach (var node in tree.Nodes) {
        var sectors = volume.Superblock.BtreeNodeSectors;
        if (sectors <= 0) continue;
        AddAllocation(buckets, allocations, diagnostics, new BcacheFsPhysicalAllocation(
          BcacheFsPhysicalKind.BtreeNode,
          node.PhysicalPointer.Device,
          node.PhysicalPointer.Sector,
          sectors,
          BcacheFsDataType.Btree,
          true,
          id,
          node.Level,
          node.MinKey,
          $"{id} L{node.Level} {Format(node.MinKey)}..{Format(node.MaxKey)}"));
      }
    }
  }

  private static void ApplyExtentReplicas(
      BcacheFsCoreVolume volume,
      Dictionary<(byte Device, long Bucket), BcacheFsClusterBucket> buckets,
      List<BcacheFsPhysicalAllocation> allocations,
      List<string> diagnostics) {
    foreach (var id in new[] { BcacheFsBtreeId.Extents, BcacheFsBtreeId.Reflink }) {
      if (volume.Root(id) == null) continue;
      var tree = BcacheFsBtreeReader.ReadTree(volume, id);
      if (!tree.Complete) continue;
      foreach (var key in tree.MaterializedLeafSlots) {
        if (key.Type is not (BcacheFsKeyType.Extent or BcacheFsKeyType.ReflinkP)) continue;
        if (!BcacheFsExtentCodec.TryParseEntries(key.Value, volume.Superblock, out var entries, out var error)) {
          diagnostics.Add($"{id} {Format(key.Position)}: {error}");
          continue;
        }

        BcacheFsExtentCrc? currentCrc = null;
        foreach (var entry in entries) {
          if (entry.KnownType is BcacheFsExtentEntryType.Crc32 or BcacheFsExtentEntryType.Crc64 or BcacheFsExtentEntryType.Crc128) {
            if (BcacheFsExtentCodec.TryReadExtentCrc(entry, out var crc, out _)) currentCrc = crc;
            continue;
          }
          if (entry.KnownType != BcacheFsExtentEntryType.Pointer) continue;
          var word = key.BigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(entry.RawBytes)
            : BinaryPrimitives.ReadUInt64LittleEndian(entry.RawBytes);
          var ptr = new BcacheFsExtentPointer(
            (byte)((word >> 48) & 0xFF),
            (long)((word >> 4) & ((1UL << 44) - 1)),
            (byte)(word >> 56),
            (word & 2) != 0,
            (word & 4) != 0,
            (word & 8) != 0,
            word);
          if (ptr.Unused || ptr.Unwritten) continue;

          var sectors = currentCrc?.CompressedSize ?? key.Size;
          if (sectors == 0) sectors = 1;
          AddAllocation(buckets, allocations, diagnostics, new BcacheFsPhysicalAllocation(
            BcacheFsPhysicalKind.UserExtent,
            ptr.Device,
            ptr.Sector,
            sectors,
            ptr.Cached ? BcacheFsDataType.Cached : BcacheFsDataType.User,
            !ptr.Cached,
            id,
            0,
            key.Position,
            $"{id} {Format(key.Position)} replica dev {ptr.Device}"));
        }
      }
    }
  }

  private static void AddAllocation(
      Dictionary<(byte Device, long Bucket), BcacheFsClusterBucket> buckets,
      List<BcacheFsPhysicalAllocation> allocations,
      List<string> diagnostics,
      BcacheFsPhysicalAllocation allocation) {
    allocations.Add(allocation);
    var memberBucketSize = buckets.Values.FirstOrDefault(b => b.Device == allocation.Device)?.BucketSizeSectors ?? 0;
    if (memberBucketSize == 0) {
      diagnostics.Add($"{allocation.Label}: member {allocation.Device} has no usable bucket geometry.");
      return;
    }

    var first = allocation.Sector / memberBucketSize;
    var last = checked((allocation.Sector + allocation.Sectors - 1) / memberBucketSize);
    for (var bucket = first; bucket <= last; ++bucket) {
      if (!buckets.TryGetValue((allocation.Device, bucket), out var current)) {
        diagnostics.Add($"{allocation.Label}: device {allocation.Device} sector {allocation.Sector} lies outside member buckets.");
        continue;
      }
      var overlays = current.Overlays.ToList();
      overlays.Add(allocation);
      buckets[(allocation.Device, bucket)] = current with { Overlays = overlays };
    }
  }

  private static void ValidateOverlays(
      Dictionary<(byte Device, long Bucket), BcacheFsClusterBucket> buckets,
      List<string> diagnostics) {
    foreach (var (key, bucket) in buckets.ToArray()) {
      var current = bucket;
      if (current.Overlays.Count != 0 && current.Reusable) {
        diagnostics.Add($"device {current.Device} bucket {current.Bucket} is marked free but referenced by live metadata/data.");
        current = current with { Reusable = false };
      }

      var incompatible = current.Overlays
        .Select(a => a.DataType)
        .Where(t => t != BcacheFsDataType.Unknown)
        .Distinct()
        .ToArray();
      if (incompatible.Length > 1 && incompatible.Any(t => t is BcacheFsDataType.Superblock or BcacheFsDataType.Journal or BcacheFsDataType.Btree)) {
        diagnostics.Add($"device {current.Device} bucket {current.Bucket} has conflicting live owners: {string.Join(", ", incompatible)}.");
        current = current with { Reusable = false };
      }
      buckets[key] = current;
    }
  }

  private static string Format(Bpos position)
    => $"{position.Inode}:{position.Offset}:{position.Snapshot}";
}

internal enum BcacheFsDataType : byte {
  Free = 0,
  Superblock = 1,
  Journal = 2,
  Btree = 3,
  User = 4,
  Cached = 5,
  Parity = 6,
  Stripe = 7,
  NeedGcGens = 8,
  NeedDiscard = 9,
  Unstriped = 10,
  Unknown = byte.MaxValue,
}

internal enum BcacheFsPhysicalKind : byte {
  Superblock,
  Journal,
  BtreeNode,
  UserExtent,
  Stripe,
  Reserved,
  Unknown,
}

internal sealed record BcacheFsPhysicalAllocation(
  BcacheFsPhysicalKind Kind,
  byte Device,
  long Sector,
  long Sectors,
  BcacheFsDataType DataType,
  bool Movable,
  BcacheFsBtreeId? BtreeId,
  byte? Level,
  Bpos? Position,
  string Label) {
  internal long EndSector => checked(this.Sector + this.Sectors);
}

internal sealed record BcacheFsClusterBucket(
  byte Device,
  long Bucket,
  int BucketSizeSectors,
  BcacheFsDataType DataType,
  bool Reusable,
  uint DirtySectors,
  uint CachedSectors,
  IReadOnlyList<BcacheFsPhysicalAllocation> Overlays) {
  internal long FirstSector => checked(this.Bucket * (long)this.BucketSizeSectors);
  internal long EndSector => checked(this.FirstSector + this.BucketSizeSectors);
}

internal readonly record struct BcacheFsClusterRun(byte Device, long FirstBucket, long BucketCount) {
  internal long EndBucket => checked(this.FirstBucket + this.BucketCount);
}
