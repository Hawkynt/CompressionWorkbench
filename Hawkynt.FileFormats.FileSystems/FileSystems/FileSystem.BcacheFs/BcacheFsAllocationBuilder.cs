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

    /// <summary>Sectors per bucket of this member, where members differ; zero for the volume's.</summary>
    internal int MemberBucketSectors { get; init; }

    /// <summary>Sectors per bucket of this member.</summary>
    internal int BucketSize => this.MemberBucketSectors > 0 ? this.MemberBucketSectors : BucketSectors;

    internal long Buckets => this.DeviceSectors / this.BucketSize;
  }

  /// <summary>
  /// One written copy of a b-tree node: which tree, how deep, the range it ends at, and where.
  /// The copies of one node share tree, level and range.
  /// </summary>
  internal readonly record struct Node(int Btree, int Level, Bpos MaxKey, long Sector, byte Device = 0);

  /// <summary>A bucket's generation, and the oldest one a pointer into it may still carry.</summary>
  internal readonly record struct Generation(byte Gen, byte Oldest);

  /// <summary>Per data type, as the dev_usage entry lists them.</summary>
  internal sealed class Usage {
    internal ulong[] Buckets { get; } = new ulong[DataTypeCount];
    internal ulong[] Sectors { get; } = new ulong[DataTypeCount];
    internal ulong[] Fragmented { get; } = new ulong[DataTypeCount];

  }

  /// <summary>One replicas entry: a data type and the set of members holding a copy, in ascending order.</summary>
  internal sealed record ReplicaSet(byte DataType, byte[] Devices) {
    internal string Name => $"{this.DataType}:{string.Join(",", this.Devices)}";
    public bool Equals(ReplicaSet? other) => other is not null && this.Name == other.Name;
    public override int GetHashCode() => this.Name.GetHashCode(StringComparison.Ordinal);
  }

  /// <summary>
  /// What the clean section and the journal say about the space: per member the dev_usage
  /// triples, and per replicas entry the sectors its copies occupy, added over the copies.
  /// </summary>
  internal sealed class VolumeUsage {
    internal Dictionary<byte, Usage> Devices { get; } = [];
    internal Dictionary<ReplicaSet, ulong> Replicas { get; } = [];

    internal void Add(ReplicaSet set, ulong sectors) => this.Replicas[set] = this.Replicas.GetValueOrDefault(set) + sectors;
  }

  internal sealed record Result(
    List<Key> Alloc,
    List<Key> Freespace,
    List<Key> Lru,
    List<Key> Backpointers,
    List<Key> BucketGens,
    VolumeUsage Usage,
    IReadOnlyDictionary<byte, byte[]> DataTypes,
    IReadOnlyDictionary<byte, uint[]> DirtySectors) {
    /// <summary>The first member's bucket data types, for a single-device volume.</summary>
    internal byte[] DataType => this.DataTypes[0];
  }

  /// <summary>
  /// Builds the derived trees for a device holding <paramref name="nodes" /> and
  /// <paramref name="extents" />.
  /// </summary>
  /// <param name="generations">Buckets whose generation is not zero.</param>
  internal static Result Build(
      Geometry geometry,
      IReadOnlyList<Node> nodes,
      IEnumerable<Key> extents,
      IReadOnlyDictionary<long, Generation>? generations = null,
      IEnumerable<Key>? indirect = null)
    => Build(new Dictionary<byte, Geometry> { [0] = geometry }, nodes, extents, generations, indirect);

  /// <summary>
  /// Builds the derived trees for the members <paramref name="devices" /> describes, holding
  /// <paramref name="nodes" /> (every copy) and <paramref name="extents" /> (every pointer).
  /// </summary>
  /// <param name="generations">Buckets whose generation is not zero, by <see cref="BcacheFsBucketId" />.</param>
  internal static Result Build(
      IReadOnlyDictionary<byte, Geometry> devices,
      IReadOnlyList<Node> nodes,
      IEnumerable<Key> extents,
      IReadOnlyDictionary<long, Generation>? generations = null,
      IEnumerable<Key>? indirect = null) {
    ArgumentNullException.ThrowIfNull(devices);
    ArgumentNullException.ThrowIfNull(nodes);
    ArgumentNullException.ThrowIfNull(extents);
    generations ??= new Dictionary<long, Generation>();

    var types = devices.ToDictionary(d => d.Key, d => new byte[d.Value.Buckets]);
    var dirtyBy = devices.ToDictionary(d => d.Key, d => new uint[d.Value.Buckets]);
    var usage = new VolumeUsage();
    var backpointers = new List<Key>();

    void Mark(byte device, long sector, long sectors, byte dataType) {
      if (!types.TryGetValue(device, out var type))
        throw new InvalidOperationException($"bcachefs pointer names member {device}, which this volume does not have.");
      var dirty = dirtyBy[device];
      var size = devices[device].BucketSize;
      while (sectors > 0) {
        var bucket = sector / size;
        if (bucket >= type.LongLength) return;
        var inBucket = Math.Min(sectors, (bucket + 1) * size - sector);
        if (type[bucket] != DataFree && type[bucket] != dataType)
          throw new InvalidOperationException(
            $"bcachefs bucket {bucket} of member {device} would hold both data type {type[bucket]} and {dataType}.");
        type[bucket] = dataType;
        dirty[bucket] = checked(dirty[bucket] + (uint)inBucket);
        sector += inBucket;
        sectors -= inBucket;
      }
    }

    foreach (var (device, geometry) in devices) {
      // The superblock region is everything before the first slot plus every slot
      // at its full advertised size, not just the bytes a superblock fills.
      Mark(device, 0, PrimarySbSector, DataSb);
      foreach (var slot in geometry.SuperblockSectors) Mark(device, slot, SbSlotSectors, DataSb);

      // A journal bucket counts whole, written or not.
      foreach (var (start, count) in geometry.JournalBuckets)
        for (var b = start; b < start + count; ++b) Mark(device, b * geometry.BucketSize, geometry.BucketSize, DataJournal);
    }

    // A node costs its whole node size whatever was written of it, and the pointer
    // that names it lives one level up — which is the level its backpointer gives.
    // Its copies make one replicas entry, charged the node size once per copy.
    foreach (var copies in nodes.GroupBy(n => (n.Btree, n.Level, n.MaxKey))) {
      foreach (var node in copies) {
        Mark(node.Device, node.Sector, NodeSectors, DataBtree);
        backpointers.Add(BackpointerKey(node.Sector, node.Btree, node.Level + 1, DataBtree, NodeSectors, node.MaxKey,
          device: node.Device, bucketGen: generations.GetValueOrDefault(BcacheFsBucketId.OfSector(node.Device, node.Sector)).Gen));
      }
      usage.Add(new ReplicaSet(DataBtree, [.. copies.Select(n => n.Device).Distinct().Order()]),
        (ulong)NodeSectors * (ulong)copies.Count());
    }

    // An extent costs what it occupies on the device: its own size, or for a
    // compressed one that share of the compressed region. Its backpointer adds the
    // offset into the checksummed region below the shifted sector, so two keys
    // left pointing into one region by a partial overwrite stay distinct. Every
    // dirty pointer is a copy on its own member.
    void Charge(Key extent, int btree, Bpos target) {
      var copies = Footprints(extent);
      if (copies.Count == 0) return;
      foreach (var at in copies) {
        Mark(at.Device, at.Sector, at.DiskSectors, DataUser);
        backpointers.Add(BackpointerKey(at.Sector, btree, 0, DataUser, at.DiskSectors, target, at.RegionOffset, at.Device,
          generations.GetValueOrDefault(BcacheFsBucketId.OfSector(at.Device, at.Sector)).Gen));
      }
      usage.Add(new ReplicaSet(DataUser, [.. copies.Select(c => c.Device).Distinct().Order()]),
        (ulong)copies.Sum(c => (long)c.DiskSectors));
    }

    foreach (var extent in extents) Charge(extent, BtreeExtents, extent.Position);

    // An indirect extent occupies the device like any extent, and its backpointer names the
    // reflink tree and the indirect key, not the files that reach it.
    foreach (var key in indirect ?? [])
      if (IndirectAsExtent(key) is { } extent) Charge(extent, BtreeReflink, key.Position);

    var alloc = new List<Key>();
    var freespace = new List<Key>();
    var lru = new List<Key>();
    var gens = new List<Key>();

    foreach (var device in devices.Keys.Order()) {
      var type = types[device];
      var dirty = dirtyBy[device];
      var buckets = type.LongLength;
      var size = devices[device].BucketSize;
      var deviceUsage = usage.Devices[device] = new Usage();
      Generation GenerationOf(long bucket) => generations.GetValueOrDefault(BcacheFsBucketId.Of(device, bucket));
      var runStart = -1L;
      var runBits = 0UL;

      for (long b = 0; b <= buckets; ++b) {
        var free = b < buckets && type[b] == DataFree;
        if (b < buckets) {
          var t = type[b];
          var gen = GenerationOf(b);
          ++deviceUsage.Buckets[t];
          deviceUsage.Sectors[t] += dirty[b];
          if (t != DataFree) deviceUsage.Fragmented[t] += (ulong)(size - dirty[b]);

          if (t != DataFree || gen.Gen != 0 || gen.Oldest != 0) {
            var fragmentation = IsMovable(t) && dirty[b] < size
              ? ((ulong)dirty[b] << 31) / (ulong)size
              : 0;
            alloc.Add(AllocKey(b, t, dirty[b], gen, fragmentation, device));
            // An LRU entry names its bucket as one word: the member above bit 48. From 1.39
            // each member has a fragmentation LRU of its own.
            var lruId = BcacheFsBucketGeometry.Accounting ? LruBucketFragmentationStart + device : LruFragmentationId;
            if (fragmentation != 0)
              lru.Add(new Key(KeySet, new Bpos((lruId << LruTimeBits) | fragmentation,
                (ulong)BcacheFsBucketId.Of(device, b), 0), 0, []));
          }
        }

        // A free bucket's freespace position carries the high bits of how far its
        // generation has run ahead of the oldest pointer, so a run of free buckets
        // breaks wherever those bits change.
        var bits = free ? FreespaceGenBits(GenerationOf(b)) : 0;
        if (runStart >= 0 && (!free || bits != runBits)) {
          freespace.Add(FreespaceKey(runStart, b, runBits, device));
          runStart = -1;
        }
        if (free && runStart < 0) {
          runStart = b;
          runBits = bits;
        }
      }

      for (long first = 0; first < buckets; first += BucketGensNr) {
        var value = new byte[BucketGensNr];
        var any = false;
        for (var i = 0; i < BucketGensNr && first + i < buckets; ++i) {
          value[i] = GenerationOf(first + i).Gen;
          any |= value[i] != 0;
        }
        // A missing key reads as every generation zero, which is what a bucket
        // that was never reused has; only a run holding a reused bucket needs one.
        if (any) gens.Add(new Key(KeyBucketGens, new Bpos(device, (ulong)(first / BucketGensNr), 0), 0, value));
      }
    }

    backpointers.Sort((a, c) => Compare(a.Position, c.Position));
    lru.Sort((a, c) => Compare(a.Position, c.Position));
    return new Result(alloc, freespace, lru, backpointers, gens, usage, types, dirtyBy);
  }

  /// <summary>
  /// Where one extent sits on the device and what it costs there.
  /// </summary>
  /// <param name="Sector">The sector its pointer names: the start of its checksummed region.</param>
  /// <param name="RegionOffset">How far into that region, in uncompressed sectors, its live data starts.</param>
  /// <param name="RegionSectors">How long the region is on the device (the compressed size).</param>
  /// <param name="DiskSectors">The sectors it is charged with: its size, or its share of a compressed region.</param>
  /// <param name="Compressed">Whether the region is compressed.</param>
  /// <param name="Encrypted">Whether the region is encrypted (a nonce, or a ChaCha20/Poly1305 checksum).</param>
  internal readonly record struct ExtentFootprint(
    long Sector, int RegionOffset, int RegionSectors, int DiskSectors, bool Compressed, bool Encrypted, byte Device = 0);

  /// <summary>
  /// The footprint of a <c>KEY_TYPE_extent</c> with one dirty pointer, or null for
  /// any other key (inline data, reservations) or an extent with none.
  /// </summary>
  /// <remarks>
  /// The disk cost is <c>ptr_disk_sectors</c>: the key's size, or for a compressed
  /// region its size scaled by compressed over uncompressed, rounded up.
  /// </remarks>
  /// <summary>
  /// A <c>reflink_v</c> as the extent it describes: the same position and size, its value without
  /// the leading 64-bit refcount. Null for any other key (indirect inline data has no footprint).
  /// </summary>
  internal static Key? IndirectAsExtent(Key key)
    => key.Type == KeyReflinkV && key.Value.Length > 8 ? new Key(KeyExtent, key.Position, key.Size, key.Value[8..]) : null;

  internal static ExtentFootprint? Footprint(Key extent) {
    var all = Footprints(extent);
    return all.Count == 0 ? null : all[0];
  }

  /// <summary>
  /// Every dirty copy of a <c>KEY_TYPE_extent</c>: one footprint per pointer that is not a cached
  /// copy, each on its own member, all sharing the checksum entry in front of them. Empty for any
  /// other key.
  /// </summary>
  internal static IReadOnlyList<ExtentFootprint> Footprints(Key extent) {
    var result = new List<ExtentFootprint>();
    if (extent.Type != KeyExtent) return result;
    if (!BcacheFsExtentCodec.TryParseEntries(extent.Value, out var entries, out var error))
      throw new InvalidDataException($"bcachefs extent at {extent.Position}: {error}");

    BcacheFsExtentCrc? crc = null;
    foreach (var entry in entries) {
      switch (entry.KnownType) {
        case BcacheFsExtentEntryType.Crc32 or BcacheFsExtentEntryType.Crc64 or BcacheFsExtentEntryType.Crc128:
          if (!BcacheFsExtentCodec.TryReadExtentCrc(entry, out crc, out error))
            throw new InvalidDataException($"bcachefs extent at {extent.Position}: {error}");
          break;
        case BcacheFsExtentEntryType.Pointer: {
          var word = BinaryPrimitives.ReadUInt64LittleEndian(entry.RawBytes);
          if ((word & (1UL << 1)) != 0) continue;                             // a cached copy
          var compressed = crc?.Compressed ?? false;
          var disk = compressed
            ? (int)(((ulong)extent.Size * crc!.CompressedSize + crc.UncompressedSize - 1) / crc.UncompressedSize)
            : (int)extent.Size;
          var encrypted = crc is { } c && (c.Nonce != 0
            || c.ChecksumType is BcacheFsChecksumType.ChaCha20Poly1305_80 or BcacheFsChecksumType.ChaCha20Poly1305_128);
          result.Add(new ExtentFootprint(PointerSector(word), (int)(crc?.Offset ?? 0),
            (int)(crc?.CompressedSize ?? extent.Size), disk, compressed, encrypted, (byte)((word >> 48) & 0xFF)));
          break;
        }
      }
    }
    return result;
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
  internal static Key AllocKey(long bucket, byte dataType, uint dirtySectors, Generation gen, ulong fragmentation, byte device = 0) {
    // 1.39 drops the stored fragmentation word for journal_seq_empty and adds stripe_sectors;
    // the LRU entry is computed from dirty_sectors instead.
    var modern = BcacheFsBucketGeometry.Accounting;
    var value = new byte[modern ? AllocV4BytesV139 : AllocV4Bytes];
    var flags = (uint)(value.Length / 8) << 2;                                // backpointers_start
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
    if (!modern) BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(48), fragmentation);
    return new Key(KeyAllocV4, new Bpos(device, (ulong)bucket, 0), 0, value);
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
  internal static Key BackpointerKey(long sector, int btree, int level, byte dataType, int sectors, Bpos target,
      int regionOffset = 0, byte device = 0, byte bucketGen = 0) {
    var value = new byte[32];
    if (BcacheFsBucketGeometry.Accounting) {
      // 1.14 on: the bucket's generation where 1.3 kept the offset into the bucket, then flags,
      // then the length; the key shifts the sector by the superblock's extent_bp_shift.
      value[0] = (byte)btree;
      value[1] = (byte)level;
      value[2] = dataType;
      value[3] = bucketGen;
      BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), (uint)sectors);
      WriteBpos(value.AsSpan(12), target);
      return new Key(KeyBackpointer,
        new Bpos(device, ((ulong)sector << BcacheFsBucketGeometry.ExtentBpShift) + (ulong)regionOffset, 0), 0, value);
    }
    value[0] = (byte)btree;
    value[1] = (byte)level;
    value[2] = dataType;
    var bucketOffset = ((ulong)(sector % BcacheFsBucketGeometry.SectorsOf(device)) << BackpointerShift) + (ulong)regionOffset;
    for (var i = 0; i < 5; ++i) value[3 + i] = (byte)(bucketOffset >> (8 * i));
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), (uint)sectors);
    WriteBpos(value.AsSpan(12), target);
    return new Key(KeyBackpointer, new Bpos(device, ((ulong)sector << BackpointerShift) + (ulong)regionOffset, 0), 0, value);
  }

  /// <summary>A run of free buckets: the freespace tree keys a range by where it ends.</summary>
  private static Key FreespaceKey(long first, long end, ulong genBits, byte device) =>
    new(KeySet, new Bpos(device, (ulong)end | genBits, 0), (uint)(end - first), []);
}
