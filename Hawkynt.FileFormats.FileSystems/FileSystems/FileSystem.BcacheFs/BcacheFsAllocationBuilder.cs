#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Derives everything a single-device volume says about its own space from what
/// actually occupies it: the alloc, freespace, LRU, backpointers and bucket_gens
/// trees, and the usage totals the superblock's clean section carries.
/// </summary>
/// <remarks>
/// <para>Every writer in this package — whole-volume creation, the in-place
/// commit and metadata relocation — publishes these from one description of the
/// device, because they are the same facts said several ways and the checker
/// cross-examines every pair: an alloc key against the extents and nodes in its
/// bucket, a freespace key against an alloc key, a backpointer against the
/// pointer it names, and the clean section's totals against all of them added
/// up. Building them in one place is how they stay in agreement.</para>
///
/// <para>The rules are those of metadata version 1.3 and are written down, with
/// what was measured against <c>bcachefs format</c> and what was read, in
/// <c>docs/BCACHEFS-ON-DISK.md</c>.</para>
/// </remarks>
internal static class BcacheFsAllocationBuilder {

  /// <summary>Where the fixed structures of the device are.</summary>
  /// <param name="DeviceSectors">The device's length, in sectors.</param>
  /// <param name="SuperblockSectors">Every superblock slot the layout advertises.</param>
  /// <param name="JournalBuckets">The journal, as runs of buckets.</param>
  internal sealed record Geometry(
    long DeviceSectors,
    IReadOnlyList<long> SuperblockSectors,
    IReadOnlyList<(long Start, long Count)> JournalBuckets) {

    internal long Buckets => this.DeviceSectors / BucketSectors;
  }

  /// <summary>One written b-tree node: which tree, how deep, the range it ends at, and where.</summary>
  internal readonly record struct Node(int Btree, int Level, Bpos MaxKey, long Sector);

  /// <summary>A bucket's generation, and the oldest one a pointer into it may still carry.</summary>
  internal readonly record struct Generation(byte Gen, byte Oldest);

  /// <summary>Per data type, as the dev_usage entry lists them.</summary>
  internal sealed class Usage {
    internal ulong[] Buckets { get; } = new ulong[DataTypeCount];
    internal ulong[] Sectors { get; } = new ulong[DataTypeCount];
    internal ulong[] Fragmented { get; } = new ulong[DataTypeCount];

    /// <summary>Sectors per replicas entry: b-tree nodes and file data.</summary>
    internal ulong BtreeSectors { get; set; }
    internal ulong UserSectors { get; set; }
  }

  internal sealed record Result(
    List<Key> Alloc,
    List<Key> Freespace,
    List<Key> Lru,
    List<Key> Backpointers,
    List<Key> BucketGens,
    Usage Usage,
    byte[] DataType,
    uint[] DirtySectors);

  /// <summary>
  /// Builds the derived trees for a device holding <paramref name="nodes" /> and
  /// <paramref name="extents" />.
  /// </summary>
  /// <param name="generations">Buckets whose generation is not zero.</param>
  internal static Result Build(
      Geometry geometry,
      IReadOnlyList<Node> nodes,
      IEnumerable<Key> extents,
      IReadOnlyDictionary<long, Generation>? generations = null) {
    ArgumentNullException.ThrowIfNull(geometry);
    ArgumentNullException.ThrowIfNull(nodes);
    ArgumentNullException.ThrowIfNull(extents);
    generations ??= new Dictionary<long, Generation>();

    var buckets = geometry.Buckets;
    var type = new byte[buckets];
    var dirty = new uint[buckets];

    void Mark(long sector, long sectors, byte dataType) {
      while (sectors > 0) {
        var bucket = sector / BucketSectors;
        if (bucket >= buckets) return;
        var inBucket = Math.Min(sectors, (bucket + 1) * BucketSectors - sector);
        if (type[bucket] != DataFree && type[bucket] != dataType)
          throw new InvalidOperationException(
            $"bcachefs bucket {bucket} would hold both data type {type[bucket]} and {dataType}.");
        type[bucket] = dataType;
        dirty[bucket] = checked(dirty[bucket] + (uint)inBucket);
        sector += inBucket;
        sectors -= inBucket;
      }
    }

    // The superblock region is everything before the first slot plus every slot
    // at its full advertised size, not just the bytes a superblock fills.
    Mark(0, PrimarySbSector, DataSb);
    foreach (var slot in geometry.SuperblockSectors) Mark(slot, SbSlotSectors, DataSb);

    // A journal bucket counts whole, written or not.
    foreach (var (start, count) in geometry.JournalBuckets)
      for (var b = start; b < start + count; ++b) Mark(b * BucketSectors, BucketSectors, DataJournal);

    var usage = new Usage();
    var backpointers = new List<Key>();

    // A node costs its whole node size whatever was written of it, and the pointer
    // that names it lives one level up — which is the level its backpointer gives.
    foreach (var node in nodes) {
      Mark(node.Sector, BucketSectors, DataBtree);
      usage.BtreeSectors += BucketSectors;
      backpointers.Add(BackpointerKey(node.Sector, node.Btree, node.Level + 1, DataBtree, BucketSectors, node.MaxKey));
    }

    foreach (var extent in extents) {
      if (extent.Type != KeyExtent) continue;
      var sector = ExtentDataSector(extent);
      Mark(sector, extent.Size, DataUser);
      usage.UserSectors += extent.Size;
      backpointers.Add(BackpointerKey(sector, BtreeExtents, 0, DataUser, (int)extent.Size, extent.Position));
    }

    var alloc = new List<Key>();
    var freespace = new List<Key>();
    var lru = new List<Key>();
    var runStart = -1L;
    var runBits = 0UL;

    for (long b = 0; b <= buckets; ++b) {
      var free = b < buckets && type[b] == DataFree;
      if (b < buckets) {
        var t = type[b];
        var gen = generations.GetValueOrDefault(b);
        ++usage.Buckets[t];
        usage.Sectors[t] += dirty[b];
        if (t != DataFree) usage.Fragmented[t] += (ulong)(BucketSectors - dirty[b]);

        if (t != DataFree || gen.Gen != 0 || gen.Oldest != 0) {
          var fragmentation = IsMovable(t) && dirty[b] < BucketSectors
            ? ((ulong)dirty[b] << 31) / BucketSectors
            : 0;
          alloc.Add(AllocKey(b, t, dirty[b], gen, fragmentation));
          if (fragmentation != 0)
            lru.Add(new Key(KeySet, new Bpos((LruFragmentationId << LruTimeBits) | fragmentation, (ulong)b, 0), 0, []));
        }
      }

      // A free bucket's freespace position carries the high bits of how far its
      // generation has run ahead of the oldest pointer, so a run of free buckets
      // breaks wherever those bits change.
      var bits = free ? FreespaceGenBits(generations.GetValueOrDefault(b)) : 0;
      if (runStart >= 0 && (!free || bits != runBits)) {
        freespace.Add(FreespaceKey(runStart, b, runBits));
        runStart = -1;
      }
      if (free && runStart < 0) {
        runStart = b;
        runBits = bits;
      }
    }

    var gens = new List<Key>();
    for (long first = 0; first < buckets; first += BucketGensNr) {
      var value = new byte[BucketGensNr];
      var any = false;
      for (var i = 0; i < BucketGensNr && first + i < buckets; ++i) {
        value[i] = generations.GetValueOrDefault(first + i).Gen;
        any |= value[i] != 0;
      }
      // A missing key reads as every generation zero, which is what a bucket
      // that was never reused has; only a run holding a reused bucket needs one.
      if (any) gens.Add(new Key(KeyBucketGens, new Bpos(0, (ulong)(first / BucketGensNr), 0), 0, value));
    }

    backpointers.Sort((a, c) => Compare(a.Position, c.Position));
    lru.Sort((a, c) => Compare(a.Position, c.Position));
    return new Result(alloc, freespace, lru, backpointers, gens, usage, type, dirty);
  }

  /// <summary>Where an extent's data starts on the device.</summary>
  internal static long ExtentDataSector(Key extent) {
    for (var at = 0; at + 8 <= extent.Value.Length; at += 8) {
      var word = BinaryPrimitives.ReadUInt64LittleEndian(extent.Value.AsSpan(at));
      if (IsPointer(word)) return PointerSector(word);
    }
    throw new InvalidDataException("bcachefs extent without a pointer.");
  }

  private static bool IsMovable(byte dataType) => dataType is DataBtree or DataUser;

  private static ulong FreespaceGenBits(Generation g) => (ulong)(byte)((g.Gen - g.Oldest) >> 4) << 56;

  /// <summary>
  /// One <c>bch_alloc_v4</c>, fifty-six bytes: the generation, what the bucket
  /// holds and how much of it, and the fragmentation index copygc sorts by.
  /// </summary>
  /// <remarks>
  /// A bucket that has had data written to it carries need_discard and
  /// need_inc_gen, as one written by the kernel does: they say what has to happen
  /// before the bucket is used again. An empty bucket must not carry
  /// need_discard, since that makes its data type need_discard rather than free.
  /// </remarks>
  internal static Key AllocKey(long bucket, byte dataType, uint dirtySectors, Generation gen, ulong fragmentation) {
    var value = new byte[AllocV4Bytes];
    var flags = (uint)AllocV4U64s << 2;                                       // backpointers_start
    if (dataType != DataFree) flags |= 0b11;                                  // need_discard, need_inc_gen
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), flags);
    value[12] = gen.Gen;
    value[13] = gen.Oldest;
    value[14] = dataType;
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(16), dirtySectors);
    if (dataType != DataFree) {
      BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(24), 1);          // io_time[READ]
      BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(32), 1);          // io_time[WRITE]
    }
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(48), fragmentation);
    return new Key(KeyAllocV4, new Bpos(0, (ulong)bucket, 0), 0, value);
  }

  /// <summary>
  /// Points back from a stretch of the device to whatever occupies it.
  /// </summary>
  /// <remarks>
  /// Keyed by the sector shifted up by ten, the room below being for an offset
  /// into a compressed extent; the value repeats the offset into the bucket in
  /// the same units, says which tree and level hold the pointer, how many sectors
  /// it covers, and the position of the key that holds it.
  /// </remarks>
  internal static Key BackpointerKey(long sector, int btree, int level, byte dataType, int sectors, Bpos target) {
    var value = new byte[32];
    value[0] = (byte)btree;
    value[1] = (byte)level;
    value[2] = dataType;
    var bucketOffset = (ulong)(sector % BucketSectors) << BackpointerShift;
    for (var i = 0; i < 5; ++i) value[3 + i] = (byte)(bucketOffset >> (8 * i));
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), (uint)sectors);
    WriteBpos(value.AsSpan(12), target);
    return new Key(KeyBackpointer, new Bpos(0, (ulong)sector << BackpointerShift, 0), 0, value);
  }

  /// <summary>A run of free buckets: the freespace tree keys a range by where it ends.</summary>
  private static Key FreespaceKey(long first, long end, ulong genBits) =>
    new(KeySet, new Bpos(0, (ulong)end | genBits, 0), (uint)(end - first), []);
}
