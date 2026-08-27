#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Publishes a new clean metadata generation at arbitrary free buckets for the
/// single-device writable profile. All logical keys are materialized first; new
/// trees are written COW, roots are published last, and only then are the old
/// metadata buckets reclaimed. Allocation, freespace, accounting, backpointers
/// and the member btree bitmap are generated from the future placement itself.
/// </summary>
internal static class BcacheFsMetadataRelocator {
  private const int JournalFirstBucket = 33;
  private const int JournalBuckets = 16;
  private const int FirstMetadataBucket = JournalFirstBucket + JournalBuckets;

  private static readonly int[] Btrees = [
    BtreeExtents, BtreeInodes, BtreeDirents,
    BtreeSubvolumes, BtreeSnapshots, BtreeSnapshotTrees, BtreeLoggedOps,
    BtreeAlloc, BtreeBucketGens, BtreeFreespace, BtreeAccounting, BtreeBackpointers,
  ];

  internal static BcacheFsMetadataRelocationResult Relocate(Stream image, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    if (options.MetadataZonePlacement == MetadataZone.Unchanged)
      return new BcacheFsMetadataRelocationResult([], [], true);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("bcachefs metadata relocation needs a readable, writable, seekable stream.", nameof(image));

    var volume = BcacheFsVolume.Open(image);
    if (!volume.Valid) throw new InvalidDataException(volume.Status);
    if (volume.BucketSectorCount != BucketSectors)
      throw new NotSupportedException(
        $"bcachefs writable profile uses {BucketSectors}-sector buckets; volume uses {volume.BucketSectorCount}.");

    var trees = ReadTrees(volume);
    var oldMetadata = Btrees.SelectMany(volume.NodeSectors)
      .Select(s => s / BucketSectors).ToHashSet();
    var userSectors = UserSectorsByBucket(trees[BtreeExtents]);
    var diagnostics = new List<string>();

    Dictionary<int, IReadOnlyList<BcacheFsTreeNodeShape>> shapes = [];
    Dictionary<int, long[]> placements = [];
    for (var attempt = 0; attempt < 16; ++attempt) {
      shapes = Btrees.ToDictionary(id => id,
        id => BcacheFsTreeLayout.DescribeNodes(id, trees[id]));
      var totalNodes = shapes.Values.Sum(s => s.Count);
      var targets = ChooseTargets(volume, oldMetadata, userSectors.Keys, totalNodes, options, diagnostics);
      placements = AssignTargets(shapes, targets, options);

      RebuildPhysicalTrees(volume, trees, shapes, placements, userSectors);
      var nextShapes = Btrees.ToDictionary(id => id,
        id => BcacheFsTreeLayout.DescribeNodes(id, trees[id]));
      if (ShapeSignature(shapes) == ShapeSignature(nextShapes)) {
        shapes = nextShapes;
        break;
      }
      if (attempt == 15)
        throw new InvalidOperationException("bcachefs metadata placement did not reach a fixed point.");
    }

    // The final physical keys must be generated from the final shape/placement,
    // not from the penultimate fixed-point probe.
    RebuildPhysicalTrees(volume, trees, shapes, placements, userSectors);
    var finalShapes = Btrees.ToDictionary(id => id,
      id => BcacheFsTreeLayout.DescribeNodes(id, trees[id]));
    if (ShapeSignature(shapes) != ShapeSignature(finalShapes))
      throw new InvalidOperationException("bcachefs metadata tree shape changed after placement convergence.");

    var targetSet = placements.Values.SelectMany(x => x).ToHashSet();
    foreach (var bucket in targetSet)
      ZeroRange(image, checked(bucket * (long)BucketBytes), BucketBytes);

    var roots = new List<BcacheFsWrittenTree>(Btrees.Length);
    foreach (var btree in Btrees) {
      var buckets = ((IEnumerable<long>)placements[btree]).GetEnumerator();
      var written = BcacheFsTreeLayout.Write(image, volume.InternalMagic, btree, trees[btree], buckets);
      VerifyWrittenShape(written, finalShapes[btree], placements[btree]);
      roots.Add(written);
    }

    PatchSuperblocks(image, roots, targetSet);
    image.Flush();

    // Once a new clean root set is durable, old metadata that is no longer part
    // of the generation is dead and may be wiped. Do this after publication so
    // a failure before the root switch always leaves the old generation intact.
    foreach (var bucket in oldMetadata.Except(targetSet))
      ZeroRange(image, checked(bucket * (long)BucketBytes), BucketBytes);
    image.Flush();

    image.Position = 0;
    var core = BcacheFsCoreVolume.Open(image);
    if (!core.Recoverable)
      throw new InvalidDataException("bcachefs relocated metadata did not reopen as a recoverable volume: "
        + string.Join("; ", core.Diagnostics));
    foreach (var id in BcacheFsOnDiskCatalog.KnownBtrees) {
      if (core.Root(id) == null) continue;
      var tree = BcacheFsBtreeReader.ReadTree(core, id);
      if (!tree.Complete)
        throw new InvalidDataException($"bcachefs relocated {id} tree is incomplete: {string.Join("; ", tree.Diagnostics)}");
    }

    return new BcacheFsMetadataRelocationResult(
      oldMetadata.OrderBy(x => x).ToArray(),
      targetSet.OrderBy(x => x).ToArray(),
      true);
  }

  private static Dictionary<int, List<Key>> ReadTrees(BcacheFsVolume volume) {
    var result = new Dictionary<int, List<Key>>();
    foreach (var btree in Btrees) {
      if (!volume.Roots.ContainsKey(btree))
        throw new NotSupportedException($"bcachefs writable profile is missing btree {btree}.");
      result[btree] = volume.Keys(btree)
        .Select(e => new Key(e.Type, e.Position, e.Size, e.Value))
        .OrderBy(k => k, Comparer<Key>.Create((a, b) => Compare(a.Position, b.Position)))
        .ToList();
    }
    return result;
  }

  private static long[] ChooseTargets(
      BcacheFsVolume volume,
      IReadOnlySet<long> oldMetadata,
      IEnumerable<long> userBuckets,
      int count,
      DefragOptions options,
      List<string> diagnostics) {
    var totalBuckets = volume.DeviceSectors / BucketSectors;
    var tailSbFirst = (volume.DeviceSectors - SbSlotSectors) / BucketSectors;
    var user = userBuckets.ToHashSet();
    var forbidden = new HashSet<long>(oldMetadata);
    foreach (var b in user) forbidden.Add(b);
    for (var b = 0L; b < FirstMetadataBucket; ++b) forbidden.Add(b);
    for (var b = tailSbFirst; b < totalBuckets; ++b) forbidden.Add(b);

    var free = Enumerable.Range(0, checked((int)Math.Min(totalBuckets, int.MaxValue)))
      .Select(i => (long)i)
      .Where(b => b >= FirstMetadataBucket && b < tailSbFirst && !forbidden.Contains(b))
      .ToArray();
    if (free.Length < count)
      throw new IOException(
        $"bcachefs needs {count} COW metadata buckets but only {free.Length} are free outside the current generation.");

    IEnumerable<long> ordered = options.MetadataZonePlacement switch {
      MetadataZone.Back => free.OrderByDescending(b => b),
      MetadataZone.Middle => OrderAround(free, totalBuckets / 2),
      MetadataZone.BeforeContent => BeforeContentOrder(free, user, options.InterleaveStride),
      _ => free.OrderBy(b => b),
    };

    var targets = ordered.Take(count).ToArray();
    if (targets.Length != count)
      throw new IOException("bcachefs metadata target selection exhausted free buckets.");
    if (options.MetadataZonePlacement == MetadataZone.BeforeContent && user.Count != 0
        && !targets.Any(t => user.Any(u => t < u && u - t <= Math.Max(2, options.InterleaveStride))))
      diagnostics.Add("no free holes exist inside the packed data run; metadata fell back to the nearest available buckets.");
    return targets;
  }

  private static Dictionary<int, long[]> AssignTargets(
      IReadOnlyDictionary<int, IReadOnlyList<BcacheFsTreeNodeShape>> shapes,
      IReadOnlyList<long> targets,
      DefragOptions options) {
    var result = Btrees.ToDictionary(id => id, _ => new List<long>());
    var queues = Btrees.ToDictionary(id => id, id => new Queue<BcacheFsTreeNodeShape>(shapes[id]));
    var order = MetadataTreeOrder(options.MetadataZonePlacement).ToArray();
    var target = 0;

    // Round-robin tree assignment intentionally interleaves independent metadata
    // trees when the physical policy requests BeforeContent. Other zones keep each
    // tree clustered, which minimizes metadata seeks while still moving the zone.
    if (options.MetadataZonePlacement == MetadataZone.BeforeContent) {
      while (queues.Values.Any(q => q.Count != 0))
        foreach (var btree in order)
          if (queues[btree].Count != 0) {
            queues[btree].Dequeue();
            result[btree].Add(targets[target++]);
          }
    } else {
      foreach (var btree in order)
        while (queues[btree].Count != 0) {
          queues[btree].Dequeue();
          result[btree].Add(targets[target++]);
        }
    }

    return result.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
  }

  private static void RebuildPhysicalTrees(
      BcacheFsVolume volume,
      Dictionary<int, List<Key>> trees,
      IReadOnlyDictionary<int, IReadOnlyList<BcacheFsTreeNodeShape>> shapes,
      IReadOnlyDictionary<int, long[]> placements,
      IReadOnlyDictionary<long, uint> userSectors) {
    var alloc = trees[BtreeAlloc];
    var freespace = trees[BtreeFreespace];
    var accounting = trees[BtreeAccounting];
    var backpointers = trees[BtreeBackpointers];
    alloc.Clear();
    freespace.Clear();
    backpointers.Clear();

    var metadataBuckets = placements.Values.SelectMany(x => x).ToHashSet();
    var totalBuckets = volume.DeviceSectors / BucketSectors;
    var bucketsOf = new ulong[DataUser + 1];
    var sectorsOf = new ulong[DataUser + 1];
    var runStart = -1L;

    for (var bucket = 0L; bucket <= totalBuckets; ++bucket) {
      var free = bucket < totalBuckets;
      if (free) {
        var (type, sectors) = BucketContents(volume.DeviceSectors, bucket, metadataBuckets, userSectors);
        free = type == DataFree;
        ++bucketsOf[type];
        sectorsOf[type] += sectors;
        if (!free) alloc.Add(AllocKey(bucket, type, sectors));
      }
      if (free) {
        if (runStart < 0) runStart = bucket;
      } else {
        if (runStart >= 0) freespace.Add(FreespaceKey(runStart, bucket));
        runStart = -1;
      }
    }

    foreach (var extent in trees[BtreeExtents]) {
      var (sector, crcOffset) = ExtentLocation(extent);
      backpointers.Add(ExtentBackpointerKey(sector, crcOffset, (int)extent.Size, extent.Position));
    }

    foreach (var btree in Btrees) {
      var treeShapes = shapes[btree];
      var treeBuckets = placements[btree];
      if (treeShapes.Count != treeBuckets.Length)
        throw new InvalidOperationException($"bcachefs tree {btree} shape/placement count differs.");
      for (var i = 0; i < treeShapes.Count; ++i)
        backpointers.Add(NodeBackpointerKey(treeBuckets[i], btree,
          treeShapes[i].Level, BucketSectors, treeShapes[i].MaxKey));
    }

    var preservedAccounting = accounting
      .Where(k => AccountingTag(k) is not (AccountingDevDataType or AccountingReplicas or AccountingBtree))
      .ToArray();
    accounting.Clear();
    accounting.AddRange(preservedAccounting);
    for (byte type = DataFree; type <= DataUser; ++type)
      if (bucketsOf[type] != 0)
        accounting.Add(DevDataTypeKey(type, bucketsOf[type], sectorsOf[type]));

    ulong btreeSectors = 0;
    foreach (var btree in Btrees) {
      var nodes = (ulong)shapes[btree].Count;
      btreeSectors += nodes * BucketSectors;
      accounting.Add(BtreeAccountingKey(btree, nodes, shapes[btree].Count(n => n.Level != 0)));
    }
    accounting.Add(ReplicasKey(DataBtree, btreeSectors));
    if (sectorsOf[DataUser] != 0)
      accounting.Add(ReplicasKey(DataUser, sectorsOf[DataUser]));

    foreach (var list in trees.Values)
      list.Sort((a, b) => Compare(a.Position, b.Position));
  }

  private static (byte Type, uint Sectors) BucketContents(
      long deviceSectors,
      long bucket,
      IReadOnlySet<long> metadataBuckets,
      IReadOnlyDictionary<long, uint> userSectors) {
    var sbEndSector = PrimarySbSector + 2L * SbSlotSectors;
    if (bucket < sbEndSector / BucketSectors) return (DataSb, BucketSectors);
    if (bucket == sbEndSector / BucketSectors)
      return (DataSb, (uint)(sbEndSector % BucketSectors));
    var firstLastSbBucket = (deviceSectors - SbSlotSectors) / BucketSectors;
    if (bucket >= firstLastSbBucket) return (DataSb, BucketSectors);
    if (bucket >= JournalFirstBucket && bucket < JournalFirstBucket + JournalBuckets)
      return (DataJournal, BucketSectors);
    if (metadataBuckets.Contains(bucket)) return (DataBtree, BucketSectors);
    if (userSectors.TryGetValue(bucket, out var sectors)) return (DataUser, sectors);
    return (DataFree, 0);
  }

  private static Dictionary<long, uint> UserSectorsByBucket(IEnumerable<Key> extents) {
    var result = new Dictionary<long, uint>();
    foreach (var extent in extents) {
      var (sector, _) = ExtentLocation(extent);
      if (sector % BucketSectors != 0)
        throw new NotSupportedException("bcachefs writable profile metadata relocation requires bucket-aligned user extents.");
      var bucket = sector / BucketSectors;
      result.TryGetValue(bucket, out var current);
      var next = checked(current + extent.Size);
      if (next > BucketSectors)
        throw new InvalidDataException($"bcachefs bucket {bucket} is overcommitted ({next} sectors).");
      result[bucket] = next;
    }
    return result;
  }

  private static (long Sector, uint CrcOffset) ExtentLocation(Key extent) {
    uint crcOffset = 0;
    for (var i = 0; i + 8 <= extent.Value.Length;) {
      var word = BinaryPrimitives.ReadUInt64LittleEndian(extent.Value.AsSpan(i));
      if (word == 0) throw new InvalidDataException("bcachefs extent entry has no type bit.");
      var type = System.Numerics.BitOperations.TrailingZeroCount(word);
      switch (type) {
        case 0:
          return (PointerSector(word), crcOffset);
        case 1:
          crcOffset = (uint)((word >> 16) & 0x7F);
          i += 8;
          break;
        case 2:
          crcOffset = (uint)((word >> 21) & 0x1FF);
          i += 16;
          break;
        case 3:
          crcOffset = (uint)((word >> 30) & 0x1FFF);
          i += 24;
          break;
        default:
          i += 8;
          break;
      }
    }
    throw new InvalidDataException("bcachefs extent contains no physical pointer.");
  }

  private static Key AllocKey(long bucket, byte dataType, uint dirtySectors) {
    var value = new byte[64];
    value[14] = dataType;
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16), dirtySectors);
    return new Key(KeyAllocV4, new Bpos(0, (ulong)bucket, 0), 0, value);
  }

  private static Key FreespaceKey(long first, long end)
    => new(KeySet, new Bpos(0, (ulong)end, 0), checked((uint)(end - first)), []);

  private static Key ExtentBackpointerKey(long sector, uint crcOffset, int sectors, Bpos position) {
    var value = new byte[32];
    value[0] = (byte)BtreeExtents;
    value[2] = DataUser;
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), (uint)sectors);
    WriteBpos(value.AsSpan(12), position);
    return new Key(KeyBackpointer,
      new Bpos(0, ((ulong)sector << ExtentBpShift) + crcOffset, 0), 0, value);
  }

  private static Key NodeBackpointerKey(long bucket, int btree, int level, int sectors, Bpos position) {
    var value = new byte[32];
    value[0] = (byte)btree;
    value[1] = (byte)level;
    value[2] = DataBtree;
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), (uint)sectors);
    WriteBpos(value.AsSpan(12), position);
    return new Key(KeyBackpointer,
      new Bpos(0, (ulong)(bucket * BucketSectors) << ExtentBpShift, 0), 0, value);
  }

  private static byte AccountingTag(Key key) => (byte)(key.Position.Inode >> 56);

  private static Key AccountingKey(ReadOnlySpan<byte> position, params ulong[] counters) {
    Span<byte> s = stackalloc byte[20];
    s.Clear();
    position.CopyTo(s);
    var inode = BinaryPrimitives.ReadUInt64BigEndian(s);
    var offset = BinaryPrimitives.ReadUInt64BigEndian(s[8..]);
    var snapshot = BinaryPrimitives.ReadUInt32BigEndian(s[16..]);
    var value = new byte[8 * counters.Length];
    for (var i = 0; i < counters.Length; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(i * 8), counters[i]);
    return new Key(KeyAccounting, new Bpos(inode, offset, snapshot), 0, value);
  }

  private static Key DevDataTypeKey(byte type, ulong buckets, ulong sectors)
    => AccountingKey([AccountingDevDataType, 0, type], buckets, sectors,
      type == DataFree ? 0 : buckets * BucketSectors - sectors);

  private static Key ReplicasKey(byte type, ulong sectors)
    => AccountingKey([AccountingReplicas, type, 1, 1, 0], sectors);

  private static Key BtreeAccountingKey(int btree, ulong nodes, int interiorNodes) {
    Span<byte> pos = stackalloc byte[5];
    pos[0] = AccountingBtree;
    BinaryPrimitives.WriteUInt32LittleEndian(pos[1..], (uint)btree);
    return AccountingKey(pos, nodes * BucketSectors, nodes, (ulong)interiorNodes);
  }

  private static void PatchSuperblocks(
      Stream image,
      IReadOnlyList<BcacheFsWrittenTree> roots,
      IReadOnlySet<long> metadataBuckets) {
    var deviceSectors = image.Length / SectorSize;
    long[] slots = [PrimarySbSector, PrimarySbSector + SbSlotSectors, deviceSectors - SbSlotSectors];

    image.Position = PrimarySbSector * SectorSize + 112;
    Span<byte> seqBytes = stackalloc byte[8];
    image.ReadExactly(seqBytes);
    var seq = BinaryPrimitives.ReadUInt64LittleEndian(seqBytes) + 1;

    foreach (var slot in slots) {
      var fixedPart = new byte[SbFixedBytes];
      image.Position = slot * SectorSize;
      image.ReadExactly(fixedPart);
      if (!fixedPart.AsSpan(24, 16).SequenceEqual(Magic))
        throw new InvalidDataException($"bcachefs missing superblock copy at sector {slot}.");
      var u64s = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(124));
      var sb = new byte[SbFixedBytes + checked((int)u64s * 8)];
      image.Position = slot * SectorSize;
      image.ReadExactly(sb);
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(112), seq);

      var seenRoots = 0;
      foreach (var (type, offset, length) in Sections(sb)) {
        if (type == FieldMembersV2)
          PatchMemberBitmap(sb.AsSpan(offset, length), metadataBuckets);
        if (type != FieldClean) continue;
        var cursor = offset + 24;
        var end = offset + length;
        while (cursor + 8 <= end) {
          var words = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(cursor));
          var entryType = sb[cursor + 4];
          var total = (words + 1) * 8;
          if (words == 0 || cursor + total > end) break;
          if (entryType == 1) {
            var btree = sb[cursor + 2];
            var replacement = roots.FirstOrDefault(r => r.Btree == btree);
            if (replacement != null) {
              if (replacement.RootPointer.Bytes != words * 8)
                throw new InvalidDataException($"bcachefs root {btree} changed encoded size.");
              sb[cursor + 3] = (byte)replacement.Level;
              WriteKey(sb.AsSpan(cursor + 8, replacement.RootPointer.Bytes), replacement.RootPointer);
              ++seenRoots;
            }
          }
          cursor += total;
        }
      }
      if (seenRoots != roots.Count)
        throw new InvalidDataException($"bcachefs superblock exposed {seenRoots}/{roots.Count} writable roots.");

      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(104), (ulong)slot);
      var checksum = MetadataChecksum(sb.AsSpan(16));
      BinaryPrimitives.WriteUInt64LittleEndian(sb, checksum);
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(8), 0);
      image.Position = slot * SectorSize;
      image.Write(sb);
    }
  }

  private static IEnumerable<(uint Type, int Offset, int Length)> Sections(byte[] sb) {
    var offset = SbFixedBytes;
    while (offset + 8 <= sb.Length) {
      var words = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(offset));
      if (words == 0) yield break;
      var length = checked((int)words * 8);
      if (offset + length > sb.Length) yield break;
      yield return (BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(offset + 4)), offset, length);
      offset += length;
    }
  }

  private static void PatchMemberBitmap(Span<byte> section, IReadOnlySet<long> metadataBuckets) {
    if (section.Length < 16 + 136 || metadataBuckets.Count == 0) return;
    var member = section[16..];
    var maxSector = checked((metadataBuckets.Max() + 1) * (long)BucketSectors - 1);
    var shift = 0;
    while ((maxSector >> shift) >= 64 && shift < 58) ++shift;
    var bitmap = 0UL;
    foreach (var bucket in metadataBuckets) {
      var first = bucket * (long)BucketSectors >> shift;
      var last = ((bucket + 1) * (long)BucketSectors - 1) >> shift;
      if (last >= 64)
        throw new NotSupportedException("bcachefs metadata placement cannot be represented by member btree bitmap.");
      for (var bit = first; bit <= last; ++bit) bitmap |= 1UL << (int)bit;
    }
    member[28] = (byte)shift;
    BinaryPrimitives.WriteUInt64LittleEndian(member[128..], bitmap);
  }

  private static IEnumerable<long> OrderAround(IEnumerable<long> values, long anchor)
    => values.OrderBy(v => Math.Abs(v - anchor)).ThenBy(v => v);

  private static IEnumerable<long> BeforeContentOrder(
      IReadOnlyList<long> free,
      IReadOnlySet<long> user,
      int stride) {
    if (user.Count == 0) return free.OrderBy(b => b);
    var orderedUsers = user.OrderBy(b => b).ToArray();
    var anchors = orderedUsers.Where((_, i) => i % Math.Max(1, stride) == 0).ToArray();
    return free.OrderBy(b => anchors.Min(a => b <= a ? a - b : (b - a) * 4)).ThenBy(b => b);
  }

  private static IEnumerable<int> MetadataTreeOrder(MetadataZone zone) {
    int[] locality = [
      BtreeExtents, BtreeInodes, BtreeDirents,
      BtreeSubvolumes, BtreeSnapshots, BtreeSnapshotTrees, BtreeLoggedOps,
      BtreeAlloc, BtreeFreespace, BtreeBackpointers, BtreeAccounting, BtreeBucketGens,
    ];
    return zone == MetadataZone.Back ? locality.Reverse() : locality;
  }

  private static string ShapeSignature(
      IReadOnlyDictionary<int, IReadOnlyList<BcacheFsTreeNodeShape>> shapes)
    => string.Join("|", Btrees.SelectMany(id => shapes[id].Select(s =>
      $"{id}:{s.Level}:{s.MinKey.Inode}:{s.MinKey.Offset}:{s.MinKey.Snapshot}:{s.MaxKey.Inode}:{s.MaxKey.Offset}:{s.MaxKey.Snapshot}")));

  private static void VerifyWrittenShape(
      BcacheFsWrittenTree written,
      IReadOnlyList<BcacheFsTreeNodeShape> expected,
      IReadOnlyList<long> buckets) {
    if (written.Nodes.Count != expected.Count || buckets.Count != expected.Count)
      throw new InvalidOperationException($"bcachefs tree {written.Btree} wrote an unexpected node count.");
    var actual = written.Nodes.OrderBy(n => n.Level).ThenBy(n => Compare(n.MinKey, Bpos.Min)).ToArray();
    // Physical order is intentionally independent of logical order. Validate by
    // multiset of level/range tuples instead of array position.
    foreach (var shape in expected)
      if (!written.Nodes.Any(n => n.Level == shape.Level
          && Compare(n.MinKey, shape.MinKey) == 0 && Compare(n.MaxKey, shape.MaxKey) == 0))
        throw new InvalidOperationException($"bcachefs tree {written.Btree} lost range {shape.MinKey}..{shape.MaxKey}.");
  }

  private static void ZeroRange(Stream image, long offset, long length) {
    if (length <= 0) return;
    var zero = new byte[Math.Min(BucketBytes, 1 << 20)];
    image.Position = offset;
    while (length > 0) {
      var take = (int)Math.Min(zero.Length, length);
      image.Write(zero, 0, take);
      length -= take;
    }
  }
}

internal sealed record BcacheFsMetadataRelocationResult(
  IReadOnlyList<long> OldBuckets,
  IReadOnlyList<long> NewBuckets,
  bool Complete);
