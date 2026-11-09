using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Lfs;

/// <summary>
/// A writable model of a NetBSD LFS (version 2, 32-bit) volume: superblock, IFILE (cleaner
/// information, segment usage table, inode map) and the live inodes with their blocks. Changes
/// are committed the way NetBSD's segment writer commits them: new partial segments are
/// appended at the head of the log — summary, file blocks listed in FINFO records, inode
/// blocks — the IFILE is rewritten last, and a checkpoint is written to the first two
/// superblocks. Nothing already on disk is overwritten except the superblocks.
/// </summary>
/// <remarks>
/// Converted from NetBSD's BSD-licensed <c>sbin/newfs_lfs/make_lfs.c</c> (volume geometry) and
/// <c>sbin/fsck_lfs/segwrite.c</c> (partial-segment layout, segment accounting, checksums); see
/// <c>THIRD-PARTY-NOTICE.Lfs.txt</c> and <c>docs/LFS-ON-DISK.md</c>.
/// </remarks>
internal sealed class LfsVolume {
  internal const uint Magic = 0x070162, SsMagic = 0x061561;
  internal const int DinodeSize = 128, IfileEntrySize = 20, SegUseSize = 24, CleanerInfoSize = 28, NDirect = 12;
  internal const int LabelPad = 8192, SbPad = 8192, DirBlockSize = 512;
  internal const uint IfileIno = 1, RootIno = 2;
  internal const ushort SegActive = 0x01, SegDirty = 0x02, SegSuperblock = 0x04, SegEmpty = 0x10;
  private const ushort SsRfw = 0x08;
  private const uint SfImmutable = 0x20000;
  private const int Unwritten = -2;

  internal byte[] Image { get; }
  private readonly byte[] _sb;

  // Geometry (immutable after format).
  internal int Bsize, Fsize, Frag, Bshift, Ffshift, FsbToDb, Ibsize, Inopb, Ifpb, Sepb, Nindir, Nseg, Fsbpseg, Sumsize, Cleansz, Segtabsz;
  internal int S0addr, MinFreeSeg, Dsize;
  internal readonly int[] SbOffs = new int[10];

  // Checkpoint state.
  internal int Offset, CurSeg, NextSeg, LastSeg, LastPseg, Idaddr;
  internal ulong Serial;
  internal readonly SegUse[] Segs;
  internal readonly List<IfileEntry> Entries;
  internal uint FreeHead, FreeTail;
  internal readonly Dictionary<uint, Inode> Inodes = [];
  private uint _now;
  private LfsReader? _reader;

  /// <summary>Byte ranges of <see cref="Image"/> the last commit wrote.</summary>
  internal readonly List<(long Offset, int Length)> Written = [];

  internal sealed class SegUse { public uint NBytes, OLastMod; public ushort NSums, NInos; public uint Flags; public ulong LastMod; }
  internal sealed class IfileEntry { public uint Version; public int Daddr; public uint NextFree; public uint ATime, ATimeNsec; }

  /// <summary>An inode as stored, plus the blocks it owns (for the segment usage table).</summary>
  internal sealed class Inode {
    public uint Ino;
    public byte[] Raw = new byte[DinodeSize];
    public int InodeBlock;
    public List<(int Daddr, int Bytes)> Blocks = [];
    public ushort Mode { get => BinaryPrimitives.ReadUInt16LittleEndian(this.Raw); set => BinaryPrimitives.WriteUInt16LittleEndian(this.Raw, value); }
    public short Nlink { get => BinaryPrimitives.ReadInt16LittleEndian(this.Raw.AsSpan(2)); set => BinaryPrimitives.WriteInt16LittleEndian(this.Raw.AsSpan(2), value); }
    public long Size { get => (long)BinaryPrimitives.ReadUInt64LittleEndian(this.Raw.AsSpan(8)); set => BinaryPrimitives.WriteUInt64LittleEndian(this.Raw.AsSpan(8), (ulong)value); }
    public bool IsDir => (this.Mode & 0xF000) == 0x4000;
  }

  /// <summary>Content to write for an inode in the next commit.</summary>
  internal sealed record Pending(Inode Inode, byte[] Data, bool Inline);

  private LfsVolume(byte[] image, byte[] sb, int nseg) {
    this.Image = image;
    this._sb = sb;
    this.Segs = new SegUse[nseg];
    for (var i = 0; i < nseg; ++i) this.Segs[i] = new SegUse();
    this.Entries = [];
  }

  // ── addressing ──────────────────────────────────────────────────────

  internal long ByteOf(int daddr) => ((long)daddr << this.FsbToDb) * 512;
  internal int Dtosn(int daddr) => (daddr - this.S0addr) / this.Fsbpseg;
  internal int Sntod(int sn) => sn * this.Fsbpseg + this.S0addr;
  private int BtoFsb(long bytes) => (int)(bytes >> this.Ffshift);
  private int FragRound(long bytes) => (int)((bytes + this.Fsize - 1) / this.Fsize * this.Fsize);

  /// <summary>IFILE byte offset of segment <paramref name="sn"/>'s usage entry: <c>sepb</c> per block, the block tail unused.</summary>
  private int SegUseOffset(int sn) => (this.Cleansz + sn / this.Sepb) * this.Bsize + sn % this.Sepb * SegUseSize;

  /// <summary>IFILE byte offset of inode <paramref name="ino"/>'s entry: <c>ifpb</c> per block, the block tail unused.</summary>
  private int EntryOffset(int ino) => (this.Cleansz + this.Segtabsz + ino / this.Ifpb) * this.Bsize + ino % this.Ifpb * IfileEntrySize;

  // ── format (sbin/newfs_lfs/make_lfs.c, version 2, 32-bit) ──────────

  /// <summary>Lays out an empty volume of <paramref name="bytes"/> bytes as newfs_lfs does.</summary>
  internal static LfsVolume Format(long bytes, int blockSize = 0, int fragSize = 0, int segSize = 0, uint ident = 0, DateTimeOffset? now = null) {
    if (bytes < 4L << 20 || bytes > int.MaxValue) throw new NotSupportedException("LFS volumes are written between 4 MiB and 2 GiB.");
    var sectors = bytes / 512;
    var small = sectors <= 65536;
    var bsize = blockSize != 0 ? blockSize : small ? 1024 : 8192;
    var fsize = fragSize != 0 ? fragSize : small ? 512 : 1024;
    var ssize = segSize != 0 ? segSize : small ? 32768 : 1024 * 1024;
    if (fsize > bsize) fsize = bsize;
    if (!IsPow2(bsize) || !IsPow2(fsize) || fsize < 512 || bsize > 65536) throw new NotSupportedException($"LFS block {bsize} / fragment {fsize} must be powers of two, fragment at least 512.");

    while (true) {
      if (bsize >= ssize) throw new NotSupportedException("LFS block size must be smaller than the segment size.");
      var fsbtodb = Log2(fsize / 512);
      var labelFsb = (int)(Roundup(LabelPad, fsize) / fsize);
      var sbFsb = (int)(Roundup(SbPad, fsize) / fsize);
      var fsbpseg = ssize / fsize;
      var sizeFsb = (int)(sectors >> fsbtodb);
      var dsize = sizeFsb - labelFsb;
      var nseg = dsize / fsbpseg;
      var minfreeseg = Math.Max(nseg / 10, 20);
      if (nseg < 4 * minfreeseg || nseg < 6) {
        if (segSize == 0 && ssize > bsize << 1) { ssize >>= 1; continue; }
        throw new NotSupportedException($"LFS volume of {bytes} bytes cannot hold enough {ssize}-byte segments.");
      }

      var sb = new byte[512];
      var v = new LfsVolume(new byte[sectors * 512], sb, nseg) {
        Bsize = bsize, Fsize = fsize, Frag = bsize / fsize, Bshift = Log2(bsize), Ffshift = Log2(fsize), FsbToDb = fsbtodb,
        Ibsize = fsize, Inopb = fsize / DinodeSize, Ifpb = bsize / IfileEntrySize, Sepb = bsize / SegUseSize, Nindir = bsize / 4,
        Nseg = nseg, Fsbpseg = fsbpseg, Sumsize = fsize, S0addr = 0, MinFreeSeg = minfreeseg,
      };
      v.Segtabsz = (nseg + v.Sepb - 1) / v.Sepb;
      v.Cleansz = (CleanerInfoSize + bsize - 1) / bsize;
      v.Dsize = fsbpseg * (nseg - minfreeseg);
      var interval = Math.Max(nseg / 10, 5);
      v.SbOffs[0] = labelFsb;
      v.Dsize -= sbFsb;
      for (var i = 1; i < 10; ++i) {
        var addr = i * interval * fsbpseg;   // sboffs[0] + i·interval segments, less the label segment 0 eats
        if (addr + 512 >= sizeFsb) break;
        v.SbOffs[i] = addr;
        v.Dsize -= sbFsb;
      }
      if (v.SbOffs[1] == 0) throw new NotSupportedException("LFS volume too small for a second superblock.");

      var stamp = (uint)Math.Clamp((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(), 0, uint.MaxValue);
      v._now = stamp;
      v.LastSeg = v.Sntod(nseg - 2);
      v.CurSeg = v.Sntod(nseg - 1);
      v.Offset = v.Sntod(nseg);
      v.NextSeg = v.Sntod(0);
      v.Serial = 0;

      for (int i = 0, j = 0; i < nseg; ++i)
        if (i == 0 || (i % interval == 0 && j < 10)) { v.Segs[i].Flags = SegSuperblock; ++j; }

      // Static superblock fields.
      Set32(sb, 0, Magic); Set32(sb, 4, 2); Set32(sb, 8, (uint)sizeFsb); Set32(sb, 12, (uint)ssize);
      Set32(sb, 20, (uint)bsize); Set32(sb, 24, (uint)fsize); Set32(sb, 28, (uint)v.Frag);
      Set32(sb, 56, IfileIno); Set32(sb, 80, 512 / DinodeSize); Set32(sb, 84, 10);
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(88), (ulong)(NDirect + (long)v.Nindir + (long)v.Nindir * v.Nindir + (long)v.Nindir * v.Nindir * v.Nindir) << v.Bshift);
      Set32(sb, 96, (uint)fsbpseg); Set32(sb, 100, (uint)v.Inopb); Set32(sb, 104, (uint)v.Ifpb); Set32(sb, 108, (uint)v.Sepb);
      Set32(sb, 112, (uint)v.Nindir); Set32(sb, 116, (uint)nseg); Set32(sb, 124, (uint)v.Cleansz); Set32(sb, 128, (uint)v.Segtabsz);
      Set32(sb, 140, (uint)v.Bshift); Set32(sb, 144, (uint)v.Ffshift); Set32(sb, 148, (uint)Log2(v.Frag));
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(152), (ulong)(bsize - 1));
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(160), (ulong)(fsize - 1));
      BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(168), (ulong)(v.Frag - 1));
      Set32(sb, 176, (uint)Log2(bsize / 512)); Set32(sb, 184, 60);
      for (var i = 0; i < 10; ++i) Set32(sb, 188 + 4 * i, (uint)v.SbOffs[i]);
      BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(322), 1);   // LFS_PF_CLEAN
      Set32(sb, 328, (uint)minfreeseg); Set32(sb, 332, (uint)v.Sumsize); Set32(sb, 344, (uint)v.Ibsize); Set32(sb, 348, 0);
      Set32(sb, 360, 0); Set32(sb, 364, 0);
      Set32(sb, 368, ident != 0 ? ident : (uint)Random.Shared.Next(1, int.MaxValue));
      Set32(sb, 372, (uint)fsbtodb);
      Set32(sb, 376, (uint)Math.Max(minfreeseg / 2 + 1, 15));
      Set32(sb, 16, (uint)v.Dsize);

      // IFILE entries 1 and 2 (IFILE, root) in use, the rest of the first block free.
      v.Entries.Add(new IfileEntry());
      for (var i = 1; i < v.Ifpb; ++i) v.Entries.Add(new IfileEntry { Version = 1, NextFree = i <= RootIno || i + 1 == v.Ifpb ? 0 : (uint)(i + 1) });
      v.FreeHead = RootIno + 1;
      v.FreeTail = (uint)(v.Ifpb - 1);

      // IFILE and root inodes; their blocks are written by the first commit.
      var ifile = new Inode { Ino = IfileIno };
      ifile.Mode = 0x8180;   // IFREG | 0600
      ifile.Nlink = 1;
      BinaryPrimitives.WriteUInt32LittleEndian(ifile.Raw.AsSpan(100), SfImmutable);
      v.InitTimes(ifile);
      v.Inodes[IfileIno] = ifile;

      // Root directory (make_lfs: IFDIR | 0755, two links); its block comes with the first commit.
      var root = new Inode { Ino = RootIno };
      root.Mode = 0x41ED;
      root.Nlink = 2;
      v.InitTimes(root);
      v.Inodes[RootIno] = root;
      return v;
    }
  }

  private static bool IsPow2(int x) => x > 0 && (x & (x - 1)) == 0;
  private static int Log2(int x) => System.Numerics.BitOperations.Log2((uint)x);
  private static long Roundup(long x, long y) => (x + y - 1) / y * y;
  private static void Set32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
  private static uint Get32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

  internal void InitTimes(Inode ino, uint? mtime = null) {
    var t = mtime ?? this._now;
    BinaryPrimitives.WriteUInt32LittleEndian(ino.Raw.AsSpan(4), ino.Ino);
    BinaryPrimitives.WriteInt32LittleEndian(ino.Raw.AsSpan(16), (int)this._now);
    BinaryPrimitives.WriteInt32LittleEndian(ino.Raw.AsSpan(24), (int)t);
    BinaryPrimitives.WriteInt32LittleEndian(ino.Raw.AsSpan(32), (int)this._now);
    if (BinaryPrimitives.ReadInt32LittleEndian(ino.Raw.AsSpan(108)) == 0) BinaryPrimitives.WriteInt32LittleEndian(ino.Raw.AsSpan(108), 1);
  }

  // ── load (an existing volume, at its newest checkpoint) ─────────────

  internal static LfsVolume Load(byte[] image, DateTimeOffset? now = null) {
    var reader = new LfsReader(new MemoryStream(image, writable: false));
    return Load(image, reader, now);
  }

  internal static LfsVolume Load(byte[] image, LfsReader reader, DateTimeOffset? now = null) {
    var sb = reader.SuperblockBytes;
    var nseg = (int)Get32(sb, 116);
    var v = new LfsVolume(image, (byte[])sb.Clone(), nseg) {
      Bsize = (int)Get32(sb, 20), Fsize = (int)Get32(sb, 24), Frag = (int)Get32(sb, 28), Bshift = (int)Get32(sb, 140), Ffshift = (int)Get32(sb, 144),
      FsbToDb = (int)Get32(sb, 372), Ibsize = (int)Get32(sb, 344), Inopb = (int)Get32(sb, 100), Ifpb = (int)Get32(sb, 104), Sepb = (int)Get32(sb, 108),
      Nindir = (int)Get32(sb, 112), Nseg = nseg, Fsbpseg = (int)Get32(sb, 96), Sumsize = (int)Get32(sb, 332), Cleansz = (int)Get32(sb, 124),
      Segtabsz = (int)Get32(sb, 128), S0addr = (int)Get32(sb, 348), MinFreeSeg = (int)Get32(sb, 328), Dsize = (int)Get32(sb, 16),
      Offset = (int)Get32(sb, 72), CurSeg = (int)Get32(sb, 68), NextSeg = (int)Get32(sb, 64), LastSeg = (int)Get32(sb, 60),
      LastPseg = (int)Get32(sb, 76), Idaddr = (int)Get32(sb, 52), Serial = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(336)),
    };
    if (v.Fsize * v.Frag != v.Bsize || v.Ibsize != v.Fsize || v.Sumsize != v.Fsize || v.S0addr != 0)
      throw new NotSupportedException("LFS volume geometry (inode block, summary or segment-0 offset) differs from what this editor writes; it is not edited.");
    for (var i = 0; i < 10; ++i) v.SbOffs[i] = (int)Get32(sb, 188 + 4 * i);
    v._reader = reader;
    v._now = (uint)Math.Clamp((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(), 0, uint.MaxValue);

    var ifile = v.LoadInode(IfileIno, v.Idaddr) ?? throw new InvalidDataException("LFS IFILE inode missing.");
    var data = v.ReadContent(ifile);
    var ci = data.AsSpan(0, CleanerInfoSize);
    v.FreeHead = BinaryPrimitives.ReadUInt32LittleEndian(ci[16..]);
    v.FreeTail = BinaryPrimitives.ReadUInt32LittleEndian(ci[20..]);
    for (var s = 0; s < nseg; ++s) {
      var o = v.SegUseOffset(s);
      var su = v.Segs[s];
      su.NBytes = Get32(data, o); su.OLastMod = Get32(data, o + 4);
      su.NSums = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o + 8)); su.NInos = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o + 10));
      su.Flags = Get32(data, o + 12); su.LastMod = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(o + 16));
    }
    var entries = (data.Length / v.Bsize - v.Cleansz - v.Segtabsz) * v.Ifpb;
    for (var n = 0; n < entries; ++n) {
      var o = v.EntryOffset(n);
      v.Entries.Add(new IfileEntry {
        Version = Get32(data, o), Daddr = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o + 4)), NextFree = Get32(data, o + 8),
        ATime = Get32(data, o + 12), ATimeNsec = Get32(data, o + 16),
      });
    }
    v.Inodes[IfileIno] = ifile;
    for (var ino = 2; ino < v.Entries.Count; ++ino) {
      var e = v.Entries[ino];
      if (e.Daddr <= 0) continue;
      v.Inodes[(uint)ino] = v.LoadInode((uint)ino, e.Daddr) ?? throw new InvalidDataException($"LFS inode {ino} missing from its block.");
    }
    return v;
  }

  private Inode? LoadInode(uint ino, int daddr) {
    var at = this.ByteOf(daddr);
    for (var i = 0; i < this.Inopb; ++i) {
      var o = (int)at + i * DinodeSize;
      if (Get32(this.Image, o + 4) != ino) continue;
      var n = new Inode { Ino = ino, InodeBlock = daddr };
      this.Image.AsSpan(o, DinodeSize).CopyTo(n.Raw);
      n.Blocks = this.BlockList(n);
      return n;
    }
    return null;
  }

  /// <summary>Every block an inode owns, with its allocated size: data, then indirect.</summary>
  private List<(int, int)> BlockList(Inode n) {
    var list = new List<(int, int)>();
    var size = n.Size;
    if ((n.Mode & 0xF000) == 0xA000 && size < 60 && BinaryPrimitives.ReadUInt32LittleEndian(n.Raw.AsSpan(104)) == 0) return list;
    var nblocks = (size + this.Bsize - 1) / this.Bsize;
    for (var lbn = 0L; lbn < Math.Min(nblocks, NDirect); ++lbn) {
      var d = BinaryPrimitives.ReadInt32LittleEndian(n.Raw.AsSpan(40 + 4 * (int)lbn));
      if (d <= 0) continue;
      var bytes = lbn == nblocks - 1 && nblocks <= NDirect ? this.FragRound(size - lbn * this.Bsize) : this.Bsize;
      list.Add((d, bytes));
    }
    void Walk(int daddr, int level) {
      if (daddr <= 0) return;
      list.Add((daddr, this.Bsize));
      if (level == 0) {
        var blk = this.Image.AsSpan((int)this.ByteOf(daddr), this.Bsize);
        for (var i = 0; i < this.Nindir; ++i) { var c = BinaryPrimitives.ReadInt32LittleEndian(blk[(4 * i)..]); if (c > 0) list.Add((c, this.Bsize)); }
        return;
      }
      var b = this.Image.AsSpan((int)this.ByteOf(daddr), this.Bsize).ToArray();
      for (var i = 0; i < this.Nindir; ++i) Walk(BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4 * i)), level - 1);
    }
    for (var l = 0; l < 3; ++l) Walk(BinaryPrimitives.ReadInt32LittleEndian(n.Raw.AsSpan(88 + 4 * l)), l);
    return list;
  }

  /// <summary>A file's bytes, through its pointers.</summary>
  internal byte[] ReadContent(Inode n) => (this._reader ?? throw new InvalidOperationException("A freshly formatted LFS volume has no content to read.")).ReadInodeData(n.Raw);

  // ── inode allocation ────────────────────────────────────────────────

  /// <summary>Extends the IFILE by one block of free entries (extend_ifile).</summary>
  private void ExtendIfile() {
    var first = (uint)this.Entries.Count;
    for (var i = 0; i < this.Ifpb; ++i)
      this.Entries.Add(new IfileEntry { Version = 1, NextFree = i + 1 == this.Ifpb ? this.FreeHead : first + (uint)i + 1 });
    if (this.FreeHead == 0) this.FreeTail = first + (uint)this.Ifpb - 1;
    this.FreeHead = first;
  }

  internal Inode Allocate() {
    if (this.FreeHead == 0) this.ExtendIfile();
    var ino = this.FreeHead;
    var e = this.Entries[(int)ino];
    this.FreeHead = e.NextFree;
    if (this.FreeHead == 0) this.FreeTail = 0;
    e.NextFree = 0;
    var n = new Inode { Ino = ino };
    BinaryPrimitives.WriteInt32LittleEndian(n.Raw.AsSpan(108), 1);
    this.Inodes[ino] = n;
    return n;
  }

  internal void Free(uint ino) {
    this.Inodes.Remove(ino);
    var e = this.Entries[(int)ino];
    e.Daddr = 0;
    ++e.Version;
    e.NextFree = this.FreeHead;
    this.FreeHead = ino;
    if (this.FreeTail == 0) this.FreeTail = ino;
  }

  // ── commit (sbin/fsck_lfs/segwrite.c: lfs_segwrite and friends) ─────

  private sealed class Pseg {
    public int Start, Cursor;
    public List<(uint Ino, uint Version, List<(int Lbn, int Bytes, int Item)> Blocks)> Finfos = [];
    public List<Inode> InodesHere = [];
    public List<int> InodeBlocks = [];
    public int SumBytes = 48;
    public int NextSegAtClose;
  }

  private sealed class Item {
    public required Inode Inode;
    public required byte[] Data;
    public bool Inline;
    public List<(int Lbn, int Bytes, byte[]? Content)> Blocks = [];
    public Dictionary<int, int> Addr = [];   // lbn → daddr
  }

  /// <summary>Writes <paramref name="pending"/> (and the IFILE) as new partial segments and checkpoints.</summary>
  internal void Commit(IReadOnlyList<Pending> pending) {
    this.Written.Clear();
    // fsck_lfs pass0 wants a non-empty free list; the kernel extends the IFILE the same way.
    if (this.FreeHead == 0) this.ExtendIfile();
    this.CleanEmptySegments(pending);
    var items = pending.Select(p => new Item { Inode = p.Inode, Data = p.Data, Inline = p.Inline }).ToList();
    var ifileItem = new Item { Inode = this.Inodes[IfileIno], Data = [] };
    // IFILE size: cleaner info + segment table + every entry block.
    var entryBlocks = (this.Entries.Count + this.Ifpb - 1) / this.Ifpb;
    var ifileSize = (long)(this.Cleansz + this.Segtabsz + entryBlocks) * this.Bsize;
    ifileItem.Data = new byte[ifileSize];
    items.Add(ifileItem);
    foreach (var it in items) this.PlanBlocks(it);

    // ── layout ──
    var psegs = new List<Pseg>();
    var written = new HashSet<int>();
    Pseg? cur = null;
    void OpenSegmentIfNeeded() {
      // Room left in the current segment (LFS_PARTIAL_FITS). The log head may sit exactly at
      // its end, which is the start of a segment that is not ours to write.
      var segEnd = this.Sntod(this.Dtosn(this.CurSeg) + 1);
      var left = (long)(segEnd - this.Offset) * this.Fsize;
      if (this.Offset < this.CurSeg || left < this.Sumsize + this.Bsize + this.Ibsize) this.NewSegment();
    }
    Pseg Open() {
      OpenSegmentIfNeeded();
      var p = new Pseg { Start = this.Offset, Cursor = this.Offset + this.BtoFsb(this.Sumsize) };
      written.Add(this.Dtosn(this.Offset));
      psegs.Add(p);
      return p;
    }
    int SegEnd(Pseg p) => this.Sntod(this.Dtosn(p.Start) + 1);
    int InodeBlocksFor(int inodes) => (inodes + this.Inopb - 1) / this.Inopb;
    void Close(Pseg p) {
      var nib = InodeBlocksFor(p.InodesHere.Count);
      for (var i = 0; i < nib; ++i) { p.InodeBlocks.Add(p.Cursor); p.Cursor += this.BtoFsb(this.Ibsize); }
      for (var i = 0; i < p.InodesHere.Count; ++i) p.InodesHere[i].InodeBlock = p.InodeBlocks[i / this.Inopb];
      this.Offset = p.Cursor;
      this.LastPseg = p.Start;
      p.NextSegAtClose = this.NextSeg;
    }

    foreach (var it in items) {
      var queue = new Queue<(int Lbn, int Bytes, byte[]? Content)>(it.Blocks);
      while (true) {
        cur ??= Open();
        var hasFinfo = cur.Finfos.Count > 0 && cur.Finfos[^1].Ino == it.Inode.Ino;
        var inodesAfter = cur.InodesHere.Count + 1;
        var segBytes = (long)(SegEnd(cur) - cur.Cursor) * this.Fsize;
        var placed = 0;
        while (queue.Count > 0) {
          var (lbn, bytes, _) = queue.Peek();
          var sumNeed = cur.SumBytes + (hasFinfo ? 0 : 16) + 4 + 4 * InodeBlocksFor(queue.Count == 1 ? inodesAfter : cur.InodesHere.Count);
          var segNeed = bytes + (long)InodeBlocksFor(queue.Count == 1 ? inodesAfter : cur.InodesHere.Count) * this.Ibsize;
          if (sumNeed > this.Sumsize || segNeed > segBytes) break;
          if (!hasFinfo) { cur.Finfos.Add((it.Inode.Ino, this.Entries[(int)it.Inode.Ino].Version, [])); cur.SumBytes += 16; hasFinfo = true; }
          queue.Dequeue();
          cur.Finfos[^1].Blocks.Add((lbn, bytes, 0));
          it.Addr[lbn] = cur.Cursor;
          cur.Cursor += this.BtoFsb(bytes);
          cur.SumBytes += 4;
          segBytes -= bytes;
          ++placed;
        }
        if (queue.Count == 0) {
          // The inode joins this pseg if its inode block still fits.
          var sumNeed = cur.SumBytes + 4 * InodeBlocksFor(inodesAfter);
          var segNeed = (long)InodeBlocksFor(inodesAfter) * this.Ibsize;
          if (sumNeed <= this.Sumsize && segNeed <= segBytes) { cur.InodesHere.Add(it.Inode); break; }
        }
        Close(cur);
        cur = null;
        if (placed == 0 && queue.Count > 0 && psegs.Count > 1 && psegs[^1].Finfos.Count == 0 && psegs[^1].InodesHere.Count == 0)
          throw new NotSupportedException("LFS: a block does not fit an empty partial segment.");
      }
    }
    Close(cur!);
    psegs.RemoveAll(p => p.Finfos.Count == 0 && p.InodesHere.Count == 0);

    // ── addresses into inodes ──
    foreach (var it in items) this.FillPointers(it);
    var ifileIno = this.Inodes[IfileIno];
    this.Idaddr = ifileIno.InodeBlock;
    foreach (var it in items)
      if (it.Inode.Ino != IfileIno) this.Entries[(int)it.Inode.Ino].Daddr = it.Inode.InodeBlock;

    // ── segment usage, recomputed from every live inode ──
    foreach (var s in this.Segs) s.NBytes = 0;
    foreach (var n in this.Inodes.Values) {
      this.Segs[this.Dtosn(n.InodeBlock)].NBytes += DinodeSize;
      foreach (var (d, b) in n.Blocks) this.Segs[this.Dtosn(d)].NBytes += (uint)b;
    }
    foreach (var p in psegs) {
      var s = this.Segs[this.Dtosn(p.Start)];
      ++s.NSums;
      s.NInos += (ushort)p.InodeBlocks.Count;
      s.Flags = (s.Flags | SegDirty) & ~(uint)SegEmpty;
      s.LastMod = this._now;
    }
    foreach (var s in this.Segs) s.Flags &= ~(uint)SegActive;
    this.Segs[this.Dtosn(this.CurSeg)].Flags |= SegActive | SegDirty;

    // ── space accounting (fsck_lfs pass5) ──
    long bb = 0, ubb = 0, dmeta = 0;
    int nclean = 0, nsbClean = 0;
    foreach (var (s, i) in this.Segs.Select((s, i) => (s, i))) {
      if ((s.Flags & SegDirty) != 0) {
        bb += this.BtoFsb(s.NBytes + (long)s.NSums * this.Sumsize);
        ubb += this.BtoFsb(s.NBytes + (long)s.NSums * this.Sumsize + (long)s.NInos * this.Ibsize);
        dmeta += this.BtoFsb((long)this.Sumsize * s.NSums) + this.BtoFsb((long)this.Ibsize * s.NInos);
      } else {
        ++nclean;
        if ((s.Flags & SegSuperblock) != 0) ++nsbClean;
      }
    }
    var labelCorrect = (this.Segs[0].Flags & SegDirty) == 0 ? this.BtoFsb(LabelPad) - this.S0addr : 0;
    var curr = this.Sntod(this.Dtosn(this.Offset) + 1) - this.Offset;
    var mfs = (long)this.Fsbpseg * (this.MinFreeSeg - this.MinFreeSeg / 2);
    var avail = (long)nclean * this.Fsbpseg - (long)nsbClean * this.BtoFsb(SbPad) - labelCorrect + curr - mfs;
    // The kernel keeps minfreeseg − minfreeseg/2 segments back for its cleaner; so do we.
    if (avail < 0) throw new NotSupportedException("LFS volume is full: the change would eat the segments reserved for the cleaner.");
    var labelSkew = this.BtoFsb(LabelPad);
    var bfree = this.Dsize - labelSkew - (bb + ubb) / 2;
    var nfiles = this.Inodes.Count - 1;

    // ── IFILE content ──
    var ifd = ifileItem.Data;
    Set32(ifd, 0, (uint)nclean); Set32(ifd, 4, (uint)(this.Nseg - nclean));
    BinaryPrimitives.WriteInt32LittleEndian(ifd.AsSpan(8), (int)bfree);
    BinaryPrimitives.WriteInt32LittleEndian(ifd.AsSpan(12), (int)avail);
    Set32(ifd, 16, this.FreeHead); Set32(ifd, 20, this.FreeTail); Set32(ifd, 24, 0);
    for (var s = 0; s < this.Nseg; ++s) {
      var o = this.SegUseOffset(s);
      var su = this.Segs[s];
      Set32(ifd, o, su.NBytes); Set32(ifd, o + 4, su.OLastMod);
      BinaryPrimitives.WriteUInt16LittleEndian(ifd.AsSpan(o + 8), su.NSums); BinaryPrimitives.WriteUInt16LittleEndian(ifd.AsSpan(o + 10), su.NInos);
      Set32(ifd, o + 12, su.Flags); BinaryPrimitives.WriteUInt64LittleEndian(ifd.AsSpan(o + 16), su.LastMod);
    }
    for (var i = 0; i < this.Entries.Count; ++i) {
      var e = this.Entries[i];
      var o = this.EntryOffset(i);
      Set32(ifd, o, e.Version); BinaryPrimitives.WriteInt32LittleEndian(ifd.AsSpan(o + 4), i == IfileIno ? 0 : e.Daddr);
      Set32(ifd, o + 8, e.NextFree); Set32(ifd, o + 12, e.ATime); Set32(ifd, o + 16, e.ATimeNsec);
    }
    this.FillBlockContent(ifileItem);

    // ── write blocks, inode blocks, summaries ──
    foreach (var it in items)
      foreach (var (lbn, bytes, content) in it.Blocks)
        this.Put(this.ByteOf(it.Addr[lbn]), content!.AsSpan(0, bytes));
    foreach (var p in psegs) {
      for (var b = 0; b < p.InodeBlocks.Count; ++b) {
        var blk = new byte[this.Ibsize];
        for (var k = 0; k < this.Inopb; ++k) {
          var idx = b * this.Inopb + k;
          if (idx < p.InodesHere.Count) p.InodesHere[idx].Raw.CopyTo(blk, k * DinodeSize);
        }
        this.Put(this.ByteOf(p.InodeBlocks[b]), blk);
      }
      this.WriteSummary(p);
      this.Written.Add((this.ByteOf(p.Start), this.Sumsize));
    }

    // Roll-forward starts at the checkpoint's offset: leave no stale summary there.
    if (this.Offset + this.BtoFsb(this.Sumsize) <= this.Sntod(this.Dtosn(this.CurSeg) + 1))
      this.Put(this.ByteOf(this.Offset), new byte[this.Sumsize]);

    // ── checkpoint ──
    var sb = this._sb;
    Set32(sb, 32, this.FreeHead);
    BinaryPrimitives.WriteInt32LittleEndian(sb.AsSpan(36), (int)bfree);
    Set32(sb, 40, (uint)nfiles);
    BinaryPrimitives.WriteInt32LittleEndian(sb.AsSpan(44), (int)avail);
    Set32(sb, 48, 0);
    BinaryPrimitives.WriteInt32LittleEndian(sb.AsSpan(52), this.Idaddr);
    BinaryPrimitives.WriteInt32LittleEndian(sb.AsSpan(60), this.LastSeg);
    BinaryPrimitives.WriteInt32LittleEndian(sb.AsSpan(64), this.NextSeg);
    BinaryPrimitives.WriteInt32LittleEndian(sb.AsSpan(68), this.CurSeg);
    BinaryPrimitives.WriteInt32LittleEndian(sb.AsSpan(72), this.Offset);
    BinaryPrimitives.WriteInt32LittleEndian(sb.AsSpan(76), this.LastPseg);
    Set32(sb, 228, (uint)nclean);
    BinaryPrimitives.WriteInt32LittleEndian(sb.AsSpan(324), (int)dmeta);
    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(336), this.Serial);
    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(352), this._now);
    uint sum = 0;
    for (var i = 0; i < 508; i += 2) sum ^= BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(i));
    Set32(sb, 508, sum);
    foreach (var off in this.SbOffs.Take(2)) this.PutSuperblock(off);
  }

  private void PutSuperblock(int off) {
    var pad = new byte[SbPad];
    this._sb.CopyTo(pad, 0);
    this.Put(this.ByteOf(off), pad);
  }

  private void Put(long at, ReadOnlySpan<byte> data) {
    data.CopyTo(this.Image.AsSpan((int)at, data.Length));
    this.Written.Add((at, data.Length));
  }

  /// <summary>
  /// Returns dirty segments that hold nothing live any more to the clean pool, as the
  /// cleaner's lfs_segclean does; never the current or an active segment.
  /// </summary>
  private void CleanEmptySegments(IReadOnlyList<Pending> pending) {
    var live = new long[this.Nseg];
    var fresh = pending.Select(p => p.Inode.Ino).ToHashSet();
    foreach (var n in this.Inodes.Values) {
      if (n.InodeBlock > 0) live[this.Dtosn(n.InodeBlock)] += DinodeSize;
      else if (!fresh.Contains(n.Ino) && n.Ino != IfileIno) return;   // an inode with no home: leave everything alone
      foreach (var (d, _) in n.Blocks) ++live[this.Dtosn(d)];
    }
    var cur = this.Dtosn(this.CurSeg);
    for (var i = 0; i < this.Nseg; ++i) {
      var s = this.Segs[i];
      if ((s.Flags & SegDirty) == 0 || (s.Flags & SegActive) != 0 || i == cur || live[i] != 0) continue;
      s.Flags = (s.Flags & SegSuperblock) | SegEmpty;
      s.NBytes = 0; s.NSums = 0; s.NInos = 0;
    }
  }

  /// <summary>Writes the superblock to every superblock address (a fresh volume).</summary>
  internal void WriteAllSuperblocks() {
    foreach (var off in this.SbOffs.Where(o => o != 0)) this.PutSuperblock(off);
  }

  /// <summary>Takes the next clean segment as the current one (lfs_newseg).</summary>
  private void NewSegment() {
    var sn = this.Dtosn(this.NextSeg);
    var su = this.Segs[sn];
    su.Flags |= SegDirty | SegActive;
    su.NBytes = 0; su.NSums = 0; su.NInos = 0;
    this.LastSeg = this.CurSeg;
    this.CurSeg = this.NextSeg;
    var next = -1;
    for (var k = 1; k < this.Nseg; ++k) {
      var cand = (sn + k) % this.Nseg;
      if ((this.Segs[cand].Flags & SegDirty) == 0) { next = cand; break; }
    }
    if (next < 0) throw new NotSupportedException("LFS volume has no clean segment left; it is full.");
    this.NextSeg = this.Sntod(next);
    this.Offset = this.CurSeg;
    if ((su.Flags & SegSuperblock) != 0) this.Offset += this.BtoFsb(SbPad);
    if (sn == 0 && this.S0addr < this.BtoFsb(LabelPad)) this.Offset += this.BtoFsb(LabelPad) - this.S0addr;
  }

  /// <summary>Data blocks (fragment-sized last block when it is direct), then indirect blocks.</summary>
  private void PlanBlocks(Item it) {
    if (it.Inline) return;
    var size = it.Data.LongLength;
    var nblocks = (int)((size + this.Bsize - 1) / this.Bsize);
    var maxBlocks = NDirect + (long)this.Nindir + (long)this.Nindir * this.Nindir;
    if (nblocks > maxBlocks) throw new NotSupportedException("LFS files needing triple-indirect blocks are not written.");
    for (var lbn = 0; lbn < nblocks; ++lbn) {
      var bytes = lbn == nblocks - 1 && nblocks <= NDirect ? this.FragRound(size - (long)lbn * this.Bsize) : this.Bsize;
      var content = new byte[bytes];
      it.Data.AsSpan(lbn * this.Bsize, (int)Math.Min(this.Bsize, size - (long)lbn * this.Bsize)).CopyTo(content);
      it.Blocks.Add((lbn, bytes, content));
    }
    if (nblocks > NDirect) {
      var indirect = new List<int> { -NDirect };
      if (nblocks > NDirect + this.Nindir) {
        var children = (nblocks - NDirect - this.Nindir + this.Nindir - 1) / this.Nindir;
        indirect.Add(-(NDirect + this.Nindir + 1));
        for (var c = 0; c < children; ++c) indirect.Add(this.DoubleChildLbn(c));
      }
      // Indirect blocks sort after data, by unsigned logical block number.
      foreach (var lbn in indirect.OrderBy(l => unchecked((uint)l))) it.Blocks.Add((lbn, this.Bsize, null));
    }
  }

  /// <summary>
  /// The logical block number of the c-th single-indirect block under the double-indirect block
  /// (ulfs_getlbns: the double block is −(12 + nindir + 1), its children −(12 + nindir) − c·nindir).
  /// </summary>
  private int DoubleChildLbn(int c) => -(NDirect + this.Nindir) - c * this.Nindir;

  /// <summary>Fills the inode's pointers and block count from the layout, and builds indirect block content.</summary>
  private void FillPointers(Item it) {
    var n = it.Inode;
    var raw = n.Raw;
    raw.AsSpan(40, 60).Clear();
    BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(8), (ulong)it.Data.LongLength);
    if (it.Inline) {
      it.Data.CopyTo(raw.AsSpan(40));
      BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(104), 0);
      n.Blocks = [];
      return;
    }
    var nblocks = (int)((it.Data.LongLength + this.Bsize - 1) / this.Bsize);
    for (var lbn = 0; lbn < Math.Min(nblocks, NDirect); ++lbn) BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(40 + 4 * lbn), it.Addr[lbn]);
    if (nblocks > NDirect) {
      BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(88), it.Addr[-NDirect]);
      var single = new byte[this.Bsize];
      for (var i = 0; i < Math.Min(this.Nindir, nblocks - NDirect); ++i) BinaryPrimitives.WriteInt32LittleEndian(single.AsSpan(4 * i), it.Addr[NDirect + i]);
      this.SetContent(it, -NDirect, single);
      if (nblocks > NDirect + this.Nindir) {
        var dlbn = -(NDirect + this.Nindir + 1);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(92), it.Addr[dlbn]);
        var dbl = new byte[this.Bsize];
        var rest = nblocks - NDirect - this.Nindir;
        var children = (rest + this.Nindir - 1) / this.Nindir;
        for (var c = 0; c < children; ++c) {
          var clbn = this.DoubleChildLbn(c);
          BinaryPrimitives.WriteInt32LittleEndian(dbl.AsSpan(4 * c), it.Addr[clbn]);
          var child = new byte[this.Bsize];
          for (var i = 0; i < Math.Min(this.Nindir, rest - c * this.Nindir); ++i)
            BinaryPrimitives.WriteInt32LittleEndian(child.AsSpan(4 * i), it.Addr[NDirect + this.Nindir + c * this.Nindir + i]);
          this.SetContent(it, clbn, child);
        }
        this.SetContent(it, dlbn, dbl);
      }
    }
    var frags = it.Blocks.Sum(b => b.Bytes / this.Fsize);
    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(104), (uint)frags);
    n.Blocks = it.Blocks.Select(b => (it.Addr[b.Lbn], b.Bytes)).ToList();
  }

  private void SetContent(Item it, int lbn, byte[] content) {
    var i = it.Blocks.FindIndex(b => b.Lbn == lbn);
    it.Blocks[i] = (lbn, this.Bsize, content);
  }

  private void FillBlockContent(Item it) {
    for (var i = 0; i < it.Blocks.Count; ++i) {
      var (lbn, bytes, content) = it.Blocks[i];
      if (lbn < 0) continue;
      var c = new byte[bytes];
      it.Data.AsSpan(lbn * this.Bsize, Math.Min(this.Bsize, it.Data.Length - lbn * this.Bsize)).CopyTo(c);
      it.Blocks[i] = (lbn, bytes, c);
    }
  }

  private void WriteSummary(Pseg p) {
    var at = (int)this.ByteOf(p.Start);
    var s = this.Image.AsSpan(at, this.Sumsize);
    s.Clear();
    ++this.Serial;
    Set32(this.Image, at + 8, SsMagic);
    BinaryPrimitives.WriteInt32LittleEndian(s[12..], p.NextSegAtClose);
    Set32(this.Image, at + 16, Get32(this._sb, 368));
    BinaryPrimitives.WriteUInt16LittleEndian(s[20..], (ushort)p.Finfos.Count);
    BinaryPrimitives.WriteUInt16LittleEndian(s[22..], (ushort)p.InodesHere.Count);
    BinaryPrimitives.WriteUInt16LittleEndian(s[24..], SsRfw);
    BinaryPrimitives.WriteUInt64LittleEndian(s[32..], this.Serial);
    BinaryPrimitives.WriteUInt64LittleEndian(s[40..], this._now);
    var o = 48;
    var checkwords = new List<byte>();
    foreach (var (ino, version, blocks) in p.Finfos) {
      Set32(this.Image, at + o, (uint)blocks.Count);
      Set32(this.Image, at + o + 4, version);
      Set32(this.Image, at + o + 8, ino);
      var last = blocks[^1].Bytes;
      Set32(this.Image, at + o + 12, (uint)(((last - 1) & (this.Bsize - 1)) + 1));
      o += 16;
      foreach (var (lbn, _, _) in blocks) { BinaryPrimitives.WriteInt32LittleEndian(s[o..], lbn); o += 4; }
    }
    for (var i = 0; i < p.InodeBlocks.Count; ++i)
      BinaryPrimitives.WriteInt32LittleEndian(s[(this.Sumsize - 4 * (i + 1))..], p.InodeBlocks[i]);
    // Data checksum: the first word of every block in the order the blocks lie on disk.
    var addr = p.Start + this.BtoFsb(this.Sumsize);
    var words = new List<uint>();
    foreach (var (_, _, blocks) in p.Finfos)
      foreach (var (_, bytes, _) in blocks) { words.Add(Get32(this.Image, (int)this.ByteOf(addr))); addr += this.BtoFsb(bytes); }
    foreach (var ib in p.InodeBlocks) words.Add(Get32(this.Image, (int)this.ByteOf(ib)));
    uint dsum = 0;
    foreach (var w in words) dsum ^= (w & 0xFFFF) ^ (w >> 16);
    Set32(this.Image, at + 4, dsum);
    uint ssum = 0;
    for (var i = 4; i < this.Sumsize; i += 2) ssum ^= BinaryPrimitives.ReadUInt16LittleEndian(s[i..]);
    Set32(this.Image, at, ssum);
  }

  // ── directories ─────────────────────────────────────────────────────

  /// <summary>Directory content in 512-byte chunks: "." and "..", then the entries.</summary>
  internal static byte[] BuildDirectory(uint self, uint parent, IEnumerable<(string Name, uint Ino, byte Type)> entries) {
    var all = new List<(byte[] Name, uint Ino, byte Type)> { ("."u8.ToArray(), self, 4), (".."u8.ToArray(), parent, 4) };
    all.AddRange(entries.Select(e => (Encoding.UTF8.GetBytes(e.Name), e.Ino, e.Type)));
    var chunks = new List<byte[]>();
    var chunk = new byte[DirBlockSize];
    var pos = 0;
    var lastAt = -1;
    foreach (var (name, ino, type) in all) {
      var len = 8 + ((name.Length + 1 + 3) & ~3);
      if (pos + len > DirBlockSize) {
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(lastAt + 4), (ushort)(DirBlockSize - lastAt));
        chunks.Add(chunk);
        chunk = new byte[DirBlockSize];
        pos = 0;
      }
      BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(pos), ino);
      BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(pos + 4), (ushort)len);
      chunk[pos + 6] = type;
      chunk[pos + 7] = (byte)name.Length;
      name.CopyTo(chunk, pos + 8);
      lastAt = pos;
      pos += len;
    }
    BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(lastAt + 4), (ushort)(DirBlockSize - lastAt));
    chunks.Add(chunk);
    return chunks.SelectMany(c => c).ToArray();
  }
}
