#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ocfs2;

/// <summary>
/// The allocators of an existing OCFS2 volume, read and updated through the image
/// stream one block at a time: the global cluster bitmap with all of its groups,
/// and slot 0's inode allocator. Every change keeps the four places that count a
/// bit in step — the group's bitmap, its free count and longest free run, the
/// chain record the group hangs off, and the allocator dinode's used total —
/// which is what both <c>fsck.ocfs2</c> and the kernel check.
/// </summary>
/// <remarks>
/// <para>Works on any volume with 4 KiB blocks and clusters whose system files
/// are where the system directory says, which covers this package's writer and
/// <c>mkfs.ocfs2</c>'s default small-volume geometry. Anything it cannot keep
/// consistent is refused with <see cref="NotSupportedException"/> before a byte
/// is written: metaecc (every block carries a checksum this does not compute),
/// a journal that still needs replaying, a group built from discontiguous
/// extents.</para>
///
/// <para>Layout facts (<c>fs/ocfs2/ocfs2_fs.h</c>): a group descriptor
/// (<c>GROUP01</c>) carries <c>bg_bits</c> at 0x0A, <c>bg_free_bits_count</c> at
/// 0x0C, <c>bg_chain</c> at 0x0E, <c>bg_contig_free_bits</c> at 0x14,
/// <c>bg_next_group</c> at 0x18, <c>bg_parent_dinode</c> at 0x20,
/// <c>bg_blkno</c> at 0x28 and its bitmap at 0x40. A chain allocator dinode keeps
/// <c>i_used</c> at 0xB8 and its chain list at 0xC0: <c>cl_cpg</c>,
/// <c>cl_bpc</c>, <c>cl_count</c>, <c>cl_next_free_rec</c>, then 16-byte records
/// of <c>c_free</c>, <c>c_total</c>, <c>c_blkno</c>.</para>
/// </remarks>
internal sealed class Ocfs2Allocators {

  internal const int BlockSize = Ocfs2Writer.BlockSize;

  private const int Id1Offset = 0xB8;
  private const int Id2Offset = 0xC0;
  private const int GroupBitsOffset = 0x0A;
  private const int GroupFreeOffset = 0x0C;
  private const int GroupChainOffset = 0x0E;
  private const int GroupContigOffset = 0x14;
  private const int GroupNextOffset = 0x18;
  private const int GroupParentOffset = 0x20;
  private const int GroupBlknoOffset = 0x28;
  private const int GroupBitmapOffset = 0x40;

  /// <summary><c>OCFS2_FEATURE_INCOMPAT_META_ECC</c>.</summary>
  private const uint IncompatMetaEcc = 0x0800;
  /// <summary><c>OCFS2_FEATURE_INCOMPAT_DISCONTIG_BG</c>.</summary>
  private const uint IncompatDiscontigBg = 0x2000;
  /// <summary><c>OCFS2_FEATURE_INCOMPAT_INLINE_DATA</c>.</summary>
  private const uint IncompatInlineData = 0x0040;
  /// <summary><c>OCFS2_JOURNAL_DIRTY_FL</c> in <c>id1.journal1.ij_flags</c>.</summary>
  private const uint JournalDirty = 0x1;
  /// <summary>Bytes of a discontiguous group's bitmap, after which its extent list sits.</summary>
  private const int MaxBgBitmapSize = 256;

  private static readonly byte[] SuperSignature = "OCFSV2"u8.ToArray();
  private static readonly byte[] InodeSignature = "INODE01"u8.ToArray();
  private static readonly byte[] GroupSignature = "GROUP01"u8.ToArray();

  private readonly Stream _image;
  private readonly int _clustersPerGroup;
  private readonly long _firstClusterGroup;

  /// <summary>The generation every structure of the volume is stamped with.</summary>
  public uint Generation { get; }

  /// <summary>Whether a regular file may keep its bytes in its dinode.</summary>
  public bool InlineData { get; }

  /// <summary>Block of the root directory's dinode.</summary>
  public long RootDirBlkno { get; }

  /// <summary>Block of the global bitmap's dinode.</summary>
  public long GlobalBitmapBlkno { get; }

  /// <summary>Block of slot 0's inode allocator dinode.</summary>
  public long InodeAllocBlkno { get; }

  /// <summary>Clusters on the volume.</summary>
  public long TotalClusters { get; }

  private readonly bool _discontigGroups;
  private readonly long _truncateLogBlkno;
  private readonly uint _compat;

  /// <summary><c>OCFS2_FEATURE_COMPAT_BACKUP_SB</c>: superblock copies at 1 GiB, 4 GiB, 16 GiB, ….</summary>
  private const uint CompatBackupSuperblock = 0x0001;
  /// <summary>The first backup superblock's byte offset (<c>OCFS2_BACKUP_SB_START</c>).</summary>
  private const long FirstBackupSuperblockOffset = 1L << 30;

  private Ocfs2Allocators(Stream image, byte[] superblock) {
    this._image = image;
    var sb = Id2Offset;
    var incompat = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(sb + 0x20, 4));
    if ((incompat & IncompatMetaEcc) != 0)
      throw new NotSupportedException("OCFS2: the volume checksums every metadata block (metaecc), which this editor does not compute.");
    if (BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(sb + 0x38, 4)) != Ocfs2Writer.BlockSizeBits ||
        BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(sb + 0x3C, 4)) != Ocfs2Writer.ClusterSizeBits)
      throw new NotSupportedException("OCFS2: only volumes with 4 KiB blocks and 4 KiB clusters are edited in place.");

    this.InlineData = (incompat & IncompatInlineData) != 0;
    this._discontigGroups = (incompat & IncompatDiscontigBg) != 0;
    this.Generation = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(0x60, 4));
    this.RootDirBlkno = (long)BinaryPrimitives.ReadUInt64LittleEndian(superblock.AsSpan(sb + 0x28, 8));
    this._firstClusterGroup = (long)BinaryPrimitives.ReadUInt64LittleEndian(superblock.AsSpan(sb + 0x48, 8));
    this.TotalClusters = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(0x14, 4));
    var systemDir = (long)BinaryPrimitives.ReadUInt64LittleEndian(superblock.AsSpan(sb + 0x30, 8));

    this.GlobalBitmapBlkno = this.SystemFile(systemDir, "global_bitmap");
    this.InodeAllocBlkno = this.SystemFile(systemDir, "inode_alloc:0000");
    this._truncateLogBlkno = this.SystemFile(systemDir, "truncate_log:0000");
    this._compat = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(sb + 0x1C, 4));

    var journal = this.ReadDinode(this.SystemFile(systemDir, "journal:0000"));
    if ((BinaryPrimitives.ReadUInt32LittleEndian(journal.AsSpan(Id1Offset, 4)) & JournalDirty) != 0)
      throw new NotSupportedException("OCFS2: the journal still holds transactions to replay; mount the volume once to recover it first.");

    var bitmap = this.ReadDinode(this.GlobalBitmapBlkno);
    this._clustersPerGroup = BinaryPrimitives.ReadUInt16LittleEndian(bitmap.AsSpan(Id2Offset, 2));
    if (this._clustersPerGroup == 0 || BinaryPrimitives.ReadUInt16LittleEndian(bitmap.AsSpan(Id2Offset + 2, 2)) != 1)
      throw new NotSupportedException("OCFS2: the global bitmap's chain geometry is not one this editor understands.");
  }

  /// <summary>Reads the superblock and the system files the allocators live in.</summary>
  /// <exception cref="InvalidDataException">The image is not an OCFS2 volume.</exception>
  /// <exception cref="NotSupportedException">The volume is one this editor cannot keep consistent.</exception>
  public static Ocfs2Allocators Open(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < (Ocfs2Writer.SuperBlockBlkno + 1L) * BlockSize)
      throw new InvalidDataException("ocfs2: image too small to hold a superblock.");
    var superblock = ReadBlock(image, Ocfs2Writer.SuperBlockBlkno);
    if (!superblock.AsSpan(0, SuperSignature.Length).SequenceEqual(SuperSignature))
      throw new InvalidDataException("ocfs2: superblock dinode lacks the OCFSV2 signature.");
    return new Ocfs2Allocators(image, superblock);
  }

  // ── block I/O ──────────────────────────────────────────────────────────

  public static byte[] ReadBlock(Stream image, long blkno) {
    var buf = new byte[BlockSize];
    image.Position = blkno * BlockSize;
    image.ReadExactly(buf);
    return buf;
  }

  public static void WriteBlock(Stream image, long blkno, ReadOnlySpan<byte> data) {
    if (data.Length != BlockSize)
      throw new ArgumentException("block payload size mismatch", nameof(data));
    image.Position = blkno * BlockSize;
    image.Write(data);
  }

  public byte[] ReadBlock(long blkno) => ReadBlock(this._image, blkno);
  public void WriteBlock(long blkno, ReadOnlySpan<byte> data) => WriteBlock(this._image, blkno, data);

  /// <summary>Reads a block that must be a dinode.</summary>
  public byte[] ReadDinode(long blkno) {
    if (blkno <= 0 || (blkno + 1) * BlockSize > this._image.Length)
      throw new InvalidDataException($"ocfs2: dinode block {blkno} lies outside the image.");
    var block = this.ReadBlock(blkno);
    if (!block.AsSpan(0, InodeSignature.Length).SequenceEqual(InodeSignature))
      throw new InvalidDataException($"ocfs2: block {blkno} lacks the INODE01 signature.");
    return block;
  }

  /// <summary>Finds a system file by name in the (inline) system directory.</summary>
  private long SystemFile(long systemDir, string name) {
    var dir = this.ReadDinode(systemDir);
    if ((BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(0x76, 2)) & 1) == 0)
      throw new NotSupportedException("OCFS2: the system directory is not inline, which this editor does not walk.");
    var wanted = Encoding.ASCII.GetBytes(name);
    var start = Id2Offset + 8;
    for (var at = start; at + 12 <= BlockSize;) {
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(at + 8, 2));
      if (recLen < 12 || at + recLen > BlockSize) break;
      var inode = (long)BinaryPrimitives.ReadUInt64LittleEndian(dir.AsSpan(at, 8));
      var nameLen = dir[at + 10];
      if (inode != 0 && nameLen == wanted.Length && dir.AsSpan(at + 12, nameLen).SequenceEqual(wanted))
        return inode;
      at += recLen;
    }
    throw new InvalidDataException($"ocfs2: the system directory has no '{name}'.");
  }

  // ── groups ─────────────────────────────────────────────────────────────

  private static int Bits(byte[] group) => BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(GroupBitsOffset, 2));
  private static bool Test(byte[] group, int bit) => (group[GroupBitmapOffset + (bit >> 3)] & (1 << (bit & 7))) != 0;

  private static void Flip(byte[] group, int bit, bool used) {
    if (used) group[GroupBitmapOffset + (bit >> 3)] |= (byte)(1 << (bit & 7));
    else group[GroupBitmapOffset + (bit >> 3)] &= (byte)~(1 << (bit & 7));
  }

  /// <summary>Recounts a group's free bits and its longest free run from its bitmap.</summary>
  private static void Recount(byte[] group) {
    int free = 0, contig = 0, run = 0;
    for (var i = Bits(group) - 1; i >= 0; --i) {
      if (Test(group, i)) { run = 0; continue; }
      ++free;
      contig = Math.Max(contig, ++run);
    }
    BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(GroupFreeOffset, 2), (ushort)free);
    BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(GroupContigOffset, 2), (ushort)contig);
  }

  /// <summary>Reads a group descriptor and checks that it is the one expected where it sits.</summary>
  private byte[] ReadGroup(long blkno, long parent) {
    var group = this.ReadBlock(blkno);
    if (!group.AsSpan(0, GroupSignature.Length).SequenceEqual(GroupSignature) ||
        (long)BinaryPrimitives.ReadUInt64LittleEndian(group.AsSpan(GroupBlknoOffset, 8)) != blkno ||
        (long)BinaryPrimitives.ReadUInt64LittleEndian(group.AsSpan(GroupParentOffset, 8)) != parent)
      throw new InvalidDataException($"ocfs2: block {blkno} is not a group descriptor of allocator {parent}.");
    return group;
  }

  /// <summary>Whether a group is laid out as discontiguous extents rather than one run.</summary>
  private bool IsDiscontiguous(byte[] group)
    => this._discontigGroups
       && BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(GroupBitmapOffset + MaxBgBitmapSize + 4, 2)) != 0;

  /// <summary>
  /// Writes back changed groups and moves the allocator dinode's chain records
  /// and used total by the same amounts.
  /// </summary>
  private void Commit(long allocator, Dictionary<long, (byte[] Group, int UsedDelta)> touched) {
    if (touched.Count == 0) return;
    var dinode = this.ReadDinode(allocator);
    long usedDelta = 0;
    foreach (var (blkno, (group, delta)) in touched) {
      Recount(group);
      this.WriteBlock(blkno, group);
      var chain = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(GroupChainOffset, 2));
      var rec = Id2Offset + 0x10 + chain * 16;
      var free = BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(rec, 4));
      BinaryPrimitives.WriteUInt32LittleEndian(dinode.AsSpan(rec, 4), (uint)(free - delta));
      usedDelta += delta;
    }
    var used = BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(Id1Offset, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(dinode.AsSpan(Id1Offset, 4), (uint)(used + usedDelta));
    this.WriteBlock(allocator, dinode);
  }

  // ── global cluster bitmap ──────────────────────────────────────────────

  private int ClusterGroupCount => (int)((this.TotalClusters + this._clustersPerGroup - 1) / this._clustersPerGroup);

  private long ClusterGroupBlkno(long group) => group == 0 ? this._firstClusterGroup : group * this._clustersPerGroup;

  /// <summary>The allocation state of the first <paramref name="count"/> clusters, read group by group.</summary>
  public System.Collections.BitArray ReadClusterBitmap(long count) {
    var map = new System.Collections.BitArray(checked((int)count));
    for (var g = 0; (long)g * this._clustersPerGroup < count; ++g) {
      var group = this.ReadGroup(this.ClusterGroupBlkno(g), this.GlobalBitmapBlkno);
      var first = (long)g * this._clustersPerGroup;
      var bits = (int)Math.Min(Bits(group), count - first);
      for (var i = 0; i < bits; ++i)
        if (Test(group, i)) map[(int)(first + i)] = true;
    }
    return map;
  }

  /// <summary>Whether <paramref name="cluster"/> is marked allocated.</summary>
  public bool IsClusterUsed(long cluster) {
    var group = this.ReadGroup(this.ClusterGroupBlkno(cluster / this._clustersPerGroup), this.GlobalBitmapBlkno);
    return Test(group, (int)(cluster % this._clustersPerGroup));
  }

  /// <summary>
  /// Marks clusters allocated or free, refusing — before anything is written —
  /// to allocate a cluster already taken or free one already free.
  /// </summary>
  public void SetClusters(IEnumerable<(long Start, long Count)> runs, bool used) {
    var touched = new Dictionary<long, (byte[] Group, int UsedDelta)>();
    foreach (var (start, count) in runs)
      for (var c = start; c < start + count; ++c) {
        if (c < 0 || c >= this.TotalClusters)
          throw new InvalidDataException($"ocfs2: cluster {c} lies outside the volume.");
        var blkno = this.ClusterGroupBlkno(c / this._clustersPerGroup);
        if (!touched.TryGetValue(blkno, out var entry))
          entry = (this.ReadGroup(blkno, this.GlobalBitmapBlkno), 0);
        var bit = (int)(c % this._clustersPerGroup);
        if (Test(entry.Group, bit) == used)
          throw new InvalidDataException($"ocfs2: cluster {c} is already marked {(used ? "allocated" : "free")}.");
        Flip(entry.Group, bit, used);
        touched[blkno] = (entry.Group, entry.UsedDelta + (used ? 1 : -1));
      }
    this.Commit(this.GlobalBitmapBlkno, touched);
  }

  /// <summary>
  /// Finds <paramref name="count"/> free clusters — one run if any group has
  /// one that long, otherwise the fewest first-fit runs, at most
  /// <paramref name="maxRuns"/> — and marks them allocated. Null when the
  /// volume cannot hold them; nothing is changed then.
  /// </summary>
  public List<(long Start, long Count)>? AllocateClusters(long count, int maxRuns) {
    if (count <= 0) return [];
    var groups = new List<(long First, byte[] Group)>();
    for (var g = 0; g < this.ClusterGroupCount; ++g)
      groups.Add((g * (long)this._clustersPerGroup, this.ReadGroup(this.ClusterGroupBlkno(g), this.GlobalBitmapBlkno)));

    var free = new List<(long Start, long Count)>();
    foreach (var (first, group) in groups) {
      var bits = Bits(group);
      for (var i = 0; i < bits;) {
        if (Test(group, i)) { ++i; continue; }
        var s = i;
        while (i < bits && !Test(group, i) && i - s < ushort.MaxValue) ++i;
        free.Add((first + s, i - s));
      }
    }

    var single = free.FirstOrDefault(r => r.Count >= count);
    List<(long Start, long Count)> chosen;
    if (single.Count > 0) chosen = [(single.Start, count)];
    else {
      chosen = [];
      var left = count;
      foreach (var r in free) {
        if (left == 0) break;
        var take = Math.Min(left, r.Count);
        chosen.Add((r.Start, take));
        left -= take;
      }
      if (left > 0 || chosen.Count > maxRuns) return null;
    }
    this.SetClusters(chosen, used: true);
    return chosen;
  }

  // ── shrink ─────────────────────────────────────────────────────────────

  /// <summary>
  /// Trims the free clusters at the end of the volume in place and returns the
  /// new cluster count (the old one when nothing can go). Trailing groups whose
  /// only allocated bit is their own descriptor are unlinked from their chains,
  /// the last kept group's <c>bg_bits</c> ends at the last allocated cluster, and
  /// the chain records, the global bitmap dinode (<c>i_total</c>, <c>i_used</c>,
  /// <c>i_size</c>, <c>i_clusters</c>) and the superblock's <c>i_clusters</c>
  /// follow; the stream is cut to the new size. Every surviving block other than
  /// those counters is untouched.
  /// </summary>
  /// <exception cref="NotSupportedException">
  /// The volume has truncate-log entries still to free, backup superblocks the
  /// new size would keep, or chains laid out so that dropping groups would leave
  /// a hole in the chain list. Nothing has been written then.
  /// </exception>
  public long ShrinkToFit() {
    var tl = this.ReadDinode(this._truncateLogBlkno);
    if (BinaryPrimitives.ReadUInt16LittleEndian(tl.AsSpan(Id2Offset + 2, 2)) != 0)
      throw new NotSupportedException("OCFS2: the truncate log still holds clusters to free; mount the volume once first.");

    var groupCount = this.ClusterGroupCount;
    var groups = new byte[groupCount][];
    for (var g = 0; g < groupCount; ++g)
      groups[g] = this.ReadGroup(this.ClusterGroupBlkno(g), this.GlobalBitmapBlkno);

    // The last cluster anything holds, ignoring descriptors of groups that hold nothing else.
    long last = -1;
    for (var g = groupCount - 1; g >= 0 && last < 0; --g)
      for (var bit = Bits(groups[g]) - 1; bit >= (g == 0 ? 0 : 1); --bit)
        if (Test(groups[g], bit)) { last = (long)g * this._clustersPerGroup + bit; break; }
    var newTotal = last + 1;
    if (newTotal >= this.TotalClusters) return this.TotalClusters;
    if ((this._compat & CompatBackupSuperblock) != 0 && newTotal * BlockSize > FirstBackupSuperblockOffset)
      throw new NotSupportedException("OCFS2: the volume keeps backup superblocks, which a shrink would have to rewrite.");

    var keptGroups = (int)((newTotal + this._clustersPerGroup - 1) / this._clustersPerGroup);
    var dinode = this.ReadDinode(this.GlobalBitmapBlkno);
    var chains = BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(Id2Offset + 6, 2));

    // Unlink every dropped group from its chain: find what points at it.
    var dropped = new HashSet<long>();
    for (var g = keptGroups; g < groupCount; ++g) dropped.Add(this.ClusterGroupBlkno(g));
    var rewrites = new Dictionary<long, byte[]>();
    var emptied = new List<int>();
    for (var c = 0; c < chains; ++c) {
      var rec = Id2Offset + 0x10 + c * 16;
      long prev = 0, removedBits = 0, removedFree = 0;
      for (var blkno = (long)BinaryPrimitives.ReadUInt64LittleEndian(dinode.AsSpan(rec + 8, 8)); blkno != 0;) {
        var group = rewrites.TryGetValue(blkno, out var r) ? r : this.ReadGroup(blkno, this.GlobalBitmapBlkno);
        var next = (long)BinaryPrimitives.ReadUInt64LittleEndian(group.AsSpan(GroupNextOffset, 8));
        if (dropped.Contains(blkno)) {
          removedBits += Bits(group);
          removedFree += BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(GroupFreeOffset, 2));
          if (prev == 0) BinaryPrimitives.WriteUInt64LittleEndian(dinode.AsSpan(rec + 8, 8), (ulong)next);
          else {
            var p = rewrites.TryGetValue(prev, out var pr) ? pr : this.ReadGroup(prev, this.GlobalBitmapBlkno);
            BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(GroupNextOffset, 8), (ulong)next);
            rewrites[prev] = p;
          }
        } else prev = blkno;
        blkno = next;
      }
      if (removedBits == 0) continue;
      var total = BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(rec + 4, 4));
      var free = BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(rec, 4));
      BinaryPrimitives.WriteUInt32LittleEndian(dinode.AsSpan(rec + 4, 4), (uint)(total - removedBits));
      BinaryPrimitives.WriteUInt32LittleEndian(dinode.AsSpan(rec, 4), (uint)(free - removedFree));
      if (BinaryPrimitives.ReadUInt64LittleEndian(dinode.AsSpan(rec + 8, 8)) == 0) emptied.Add(c);
    }
    // A chain list has no holes: only the highest chains may empty.
    var keptChains = chains - emptied.Count;
    if (emptied.Any(c => c < keptChains))
      throw new NotSupportedException("OCFS2: dropping the trailing groups would empty a chain in the middle of the chain list.");
    for (var c = keptChains; c < chains; ++c)
      dinode.AsSpan(Id2Offset + 0x10 + c * 16, 16).Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(dinode.AsSpan(Id2Offset + 6, 2), (ushort)keptChains);

    // The last kept group now ends at the new last cluster.
    var lastBlkno = this.ClusterGroupBlkno(keptGroups - 1);
    var lastGroup = rewrites.TryGetValue(lastBlkno, out var lg) ? lg : groups[keptGroups - 1];
    var oldBits = Bits(lastGroup);
    var oldFree = BinaryPrimitives.ReadUInt16LittleEndian(lastGroup.AsSpan(GroupFreeOffset, 2));
    var newBits = (int)(newTotal - (long)(keptGroups - 1) * this._clustersPerGroup);
    BinaryPrimitives.WriteUInt16LittleEndian(lastGroup.AsSpan(GroupBitsOffset, 2), (ushort)newBits);
    Recount(lastGroup);
    var newFree = BinaryPrimitives.ReadUInt16LittleEndian(lastGroup.AsSpan(GroupFreeOffset, 2));
    rewrites[lastBlkno] = lastGroup;
    var lastChain = Id2Offset + 0x10 + BinaryPrimitives.ReadUInt16LittleEndian(lastGroup.AsSpan(GroupChainOffset, 2)) * 16;
    BinaryPrimitives.WriteUInt32LittleEndian(dinode.AsSpan(lastChain + 4, 4),
      (uint)(BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(lastChain + 4, 4)) - (oldBits - newBits)));
    BinaryPrimitives.WriteUInt32LittleEndian(dinode.AsSpan(lastChain, 4),
      (uint)(BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(lastChain, 4)) - (oldFree - newFree)));

    // Totals: every dropped group took exactly its descriptor's bit with it.
    var used = BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(Id1Offset, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(dinode.AsSpan(Id1Offset, 4), (uint)(used - (groupCount - keptGroups)));
    BinaryPrimitives.WriteUInt32LittleEndian(dinode.AsSpan(Id1Offset + 4, 4), (uint)newTotal);
    BinaryPrimitives.WriteUInt64LittleEndian(dinode.AsSpan(0x20, 8), (ulong)(newTotal * BlockSize));
    BinaryPrimitives.WriteUInt32LittleEndian(dinode.AsSpan(0x14, 4), (uint)newTotal);

    var superblock = ReadBlock(this._image, Ocfs2Writer.SuperBlockBlkno);
    BinaryPrimitives.WriteUInt32LittleEndian(superblock.AsSpan(0x14, 4), (uint)newTotal);

    foreach (var (blkno, group) in rewrites)
      if (!dropped.Contains(blkno)) this.WriteBlock(blkno, group);
    this.WriteBlock(this.GlobalBitmapBlkno, dinode);
    this.WriteBlock(Ocfs2Writer.SuperBlockBlkno, superblock);
    this._image.SetLength(newTotal * BlockSize);
    this._image.Flush();
    return newTotal;
  }

  // ── slot 0's inode allocator ───────────────────────────────────────────

  /// <summary>Where an inode sits in its allocator: its group, its bit, and its block.</summary>
  public readonly record struct InodeSlot(long GroupBlkno, int Bit, long Blkno);

  /// <summary>Claims the first free inode bit of slot 0's allocator; null when every group is full.</summary>
  public InodeSlot? AllocateInode() {
    var dinode = this.ReadDinode(this.InodeAllocBlkno);
    if (BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(Id2Offset + 2, 2)) != 1)
      throw new NotSupportedException("OCFS2: the inode allocator's chain geometry is not one this editor understands.");
    var chains = BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(Id2Offset + 6, 2));
    for (var c = 0; c < chains; ++c) {
      var rec = Id2Offset + 0x10 + c * 16;
      if (BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(rec, 4)) == 0) continue;
      for (var blkno = (long)BinaryPrimitives.ReadUInt64LittleEndian(dinode.AsSpan(rec + 8, 8)); blkno != 0;) {
        var group = this.ReadGroup(blkno, this.InodeAllocBlkno);
        var next = (long)BinaryPrimitives.ReadUInt64LittleEndian(group.AsSpan(GroupNextOffset, 8));
        if (BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(GroupFreeOffset, 2)) > 0 && !this.IsDiscontiguous(group)) {
          for (var bit = 0; bit < Bits(group); ++bit) {
            if (Test(group, bit)) continue;
            Flip(group, bit, used: true);
            this.Commit(this.InodeAllocBlkno, new() { [blkno] = (group, 1) });
            return new InodeSlot(blkno, bit, blkno + bit);
          }
        }
        blkno = next;
      }
    }
    return null;
  }

  /// <summary>
  /// The slot an inode was handed out from, read from its dinode — refused unless
  /// it is a bit of slot 0's allocator whose group lies where the bit says.
  /// </summary>
  public InodeSlot SlotOf(long blkno, byte[] dinode) {
    var slot = BinaryPrimitives.ReadInt16LittleEndian(dinode.AsSpan(0x0C, 2));
    var bit = BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(0x0E, 2));
    if (slot != 0)
      throw new NotSupportedException($"OCFS2: inode {blkno} comes from allocator slot {slot}, not slot 0's.");
    var groupBlkno = blkno - bit;
    var group = this.ReadGroup(groupBlkno, this.InodeAllocBlkno);
    if (this.IsDiscontiguous(group) || bit >= Bits(group) || !Test(group, bit))
      throw new NotSupportedException($"OCFS2: inode {blkno} is not where its allocator bit says.");
    return new InodeSlot(groupBlkno, bit, blkno);
  }

  /// <summary>Gives an inode's bit back to slot 0's allocator.</summary>
  public void FreeInode(InodeSlot slot) {
    var group = this.ReadGroup(slot.GroupBlkno, this.InodeAllocBlkno);
    if (!Test(group, slot.Bit))
      throw new InvalidDataException($"ocfs2: inode bit {slot.Bit} of group {slot.GroupBlkno} is already free.");
    Flip(group, slot.Bit, used: false);
    this.Commit(this.InodeAllocBlkno, new() { [slot.GroupBlkno] = (group, -1) });
  }
}
