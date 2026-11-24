#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// The accounting tree a volume at metadata version 1.39 keeps its space accounts in, derived
/// from what the volume holds, as the superblock's usage entries were before it.
/// </summary>
/// <remarks>
/// <para>Every key is a <c>KEY_TYPE_accounting</c> whose position is a
/// <c>struct disk_accounting_pos</c> — a type byte and that type's fields, packed — read as one
/// 20-byte big-endian number spread over inode, offset and snapshot; its value is one to three
/// counters. A key whose counters are all zero is not kept.</para>
///
/// <para>The counters, as <c>accounting_format.h</c> of bcachefs-tools 1.39.6 describes them and
/// as its fsck checks them: the number of inodes; per replicas entry the sectors its copies
/// occupy; per member and data type the buckets, live sectors and fragmentation (the dev_usage
/// triples); per compression type the extents and their uncompressed and on-disk sectors; per
/// snapshot and snapshot-carrying tree the keys, their bytes and the data sectors; per tree its
/// node sectors, nodes and interior nodes; per inode its extents, their size and their on-disk
/// sectors.</para>
/// </remarks>
internal static class BcacheFsAccounting {
  private const byte NrInodes = 0;
  private const byte Replicas = 2;
  private const byte DevDataType = 3;
  private const byte Compression = 4;
  private const byte Snapshot = 5;
  private const byte Btree = 6;
  private const byte Inum = 8;

  /// <summary>The trees whose keys carry a snapshot and are accounted per snapshot.</summary>
  private static readonly int[] SnapshotTrees = [BtreeExtents, BtreeInodes, BtreeDirents, BtreeXattrs];

  internal static List<Key> Build(
      BcacheFsAllocationBuilder.VolumeUsage usage,
      IReadOnlyList<BcacheFsAllocationBuilder.Node> nodes,
      IReadOnlyDictionary<int, List<Key>> trees) {
    var keys = new List<Key>();

    void Add(byte[] position, params ulong[] counters) {
      if (counters.All(c => c == 0)) return;
      var value = new byte[8 * counters.Length];
      for (var i = 0; i < counters.Length; ++i) BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8 * i), counters[i]);
      keys.Add(new Key(KeyAccounting, PositionOf(position), 0, value));
    }

    List<Key> Tree(int id) => trees.TryGetValue(id, out var list) ? list : [];

    // The inodes.
    Add([NrInodes], (ulong)Tree(BtreeInodes).Count(k => k.Type is KeyInodeV3 or (byte)BcacheFsKeyType.Inode or (byte)BcacheFsKeyType.InodeV2));

    // Per replicas entry: b-tree nodes and file data; the journal is not counted here.
    foreach (var (set, sectors) in usage.Replicas)
      if (set.DataType is DataBtree or DataUser)
        Add([Replicas, set.DataType, (byte)set.Devices.Length, 1, .. set.Devices], sectors);

    // Per member and data type: buckets, live sectors, fragmentation.
    foreach (var (device, deviceUsage) in usage.Devices)
      for (byte t = 0; t < DataTypeCount; ++t)
        Add([DevDataType, device, t], deviceUsage.Buckets[t], deviceUsage.Sectors[t], deviceUsage.Fragmented[t]);

    // Per snapshot, per tree: keys (whiteouts aside), their bytes, and for extents the data sectors.
    foreach (var id in SnapshotTrees)
      foreach (var snapshot in Tree(id).Where(k => k.Type is not (KeyWhiteout or (byte)BcacheFsKeyType.HashWhiteout
                 or (byte)BcacheFsKeyType.ExtentWhiteout)).GroupBy(k => k.Position.Snapshot)) {
        var position = new byte[9];
        position[0] = Snapshot;
        BinaryPrimitives.WriteUInt32LittleEndian(position.AsSpan(1), snapshot.Key);
        BinaryPrimitives.WriteUInt32LittleEndian(position.AsSpan(5), (uint)id);
        Add(position, (ulong)snapshot.Count(), (ulong)snapshot.Sum(k => (long)k.Bytes),
          id == BtreeExtents ? (ulong)snapshot.Sum(DiskSectors) : 0);
      }

    // Per tree: the sectors its nodes occupy (every copy), its nodes, its interior nodes.
    foreach (var tree in nodes.GroupBy(n => n.Btree)) {
      var logical = tree.GroupBy(n => (n.Level, n.MaxKey)).ToList();
      var position = new byte[5];
      position[0] = Btree;
      BinaryPrimitives.WriteUInt32LittleEndian(position.AsSpan(1), (uint)tree.Key);
      Add(position, (ulong)tree.Count() * (ulong)NodeSectors, (ulong)logical.Count, (ulong)logical.Count(g => g.Key.Level != 0));
    }

    // Per inode: its extents, their size, their on-disk sectors.
    foreach (var inode in Tree(BtreeExtents).Where(k => k.Type is KeyExtent or KeyInlineData or KeyReflinkP)
               .GroupBy(k => k.Position.Inode)) {
      var position = new byte[9];
      position[0] = Inum;
      BinaryPrimitives.WriteUInt64LittleEndian(position.AsSpan(1), inode.Key);
      Add(position, (ulong)inode.Count(), (ulong)inode.Sum(k => (long)k.Size), (ulong)inode.Sum(DiskSectors));
    }

    // Per compression type: the extents, their uncompressed size, their compressed size on disk.
    foreach (var type in Tree(BtreeExtents).Concat(Tree(BtreeReflink).Select(BcacheFsAllocationBuilder.IndirectAsExtent).OfType<Key>())
               .Select(k => (Key: k, Type: CompressionOf(k)))
               .Where(x => x.Type != 0)
               .GroupBy(x => x.Type))
      Add([Compression, type.Key], (ulong)type.Count(), (ulong)type.Sum(x => (long)x.Key.Size),
        (ulong)type.Sum(x => BcacheFsAllocationBuilder.Footprints(x.Key).FirstOrDefault().DiskSectors));

    keys.Sort((a, b) => Compare(a.Position, b.Position));
    return keys;
  }

  /// <summary>The sectors an extent's copies occupy on their members, added up.</summary>
  private static long DiskSectors(Key extent) => BcacheFsAllocationBuilder.Footprints(extent).Sum(f => (long)f.DiskSectors);

  /// <summary>
  /// The compression type of an extent's checksum entry, or zero when it has none; an extent the
  /// writer tried and failed to compress is accounted as "incompressible", a type of its own.
  /// </summary>
  private static byte CompressionOf(Key extent) {
    if (extent.Type != KeyExtent || !BcacheFsExtentCodec.TryParseEntries(extent.Value, out var entries, out _)) return 0;
    foreach (var entry in entries)
      if (entry.KnownType is BcacheFsExtentEntryType.Crc32 or BcacheFsExtentEntryType.Crc64 or BcacheFsExtentEntryType.Crc128
          && BcacheFsExtentCodec.TryReadExtentCrc(entry, out var crc, out _) && crc!.CompressionType != BcacheFsCompressionType.None)
        return (byte)crc.CompressionType;
    return 0;
  }

  /// <summary>
  /// A <c>disk_accounting_pos</c> as the key position it is stored at: the 20 bytes, zero-padded,
  /// read big-endian as inode, offset and snapshot.
  /// </summary>
  internal static Bpos PositionOf(ReadOnlySpan<byte> accountingPos) {
    Span<byte> padded = stackalloc byte[20];
    accountingPos.CopyTo(padded);
    return new Bpos(
      BinaryPrimitives.ReadUInt64BigEndian(padded),
      BinaryPrimitives.ReadUInt64BigEndian(padded[8..]),
      BinaryPrimitives.ReadUInt32BigEndian(padded[16..]));
  }
}
