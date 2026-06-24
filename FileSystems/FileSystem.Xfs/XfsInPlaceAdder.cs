#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Xfs;

/// <summary>
/// Genuine in-place add for XFS v5 images — the counterpart to the rebuild path
/// in <see cref="XfsModifier"/>. Inserts a small file into the directory tree by
/// editing only the structures the change touches, never re-packing the image.
/// <para>
/// The implementation is geometry-locked to <see cref="XfsWriter"/>'s output
/// (4 KiB blocks, 256-byte v3 inodes, 2 AGs, all content in AG 0). It handles,
/// in place and <c>xfs_repair</c>-clean:
/// </para>
/// <list type="bullet">
///   <item>claiming a free inode slot from any inobt chunk record, and growing a
///         new 64-inode chunk (4 fresh blocks + inobt record + AGI/sb counts)
///         when every existing chunk is full;</item>
///   <item>allocating a data extent from AG 0's free space — including carving
///         from the head, tail or middle of any record of a multi-record
///         bnobt/cntbt, keeping both btrees consistent and re-deriving
///         agf_freeblks/agf_longest;</item>
///   <item>inserting into a short-form root/sub directory; promoting a full
///         short-form directory to single-block ("XDB3") then to leaf form
///         ("XDD3" data blocks + an "XFS_DIR3_LEAF1" hash index); and inserting
///         into an already block- or leaf-form directory;</item>
///   <item>resolving nested sub-directory targets (creating intermediate
///         directories in place when absent);</item>
///   <item>replace-by-name (freeing the old inode + extent, then adding anew).</item>
/// </list>
/// Existing files' inodes, BMBT extents and data blocks stay byte-identical at
/// their original offsets. Cases it still cannot satisfy throw
/// <see cref="NotSupportedException"/> / <see cref="IOException"/> so the caller
/// falls back to the verified rebuild.
/// </summary>
public static class XfsInPlaceAdder {
  private const uint XfsMagic = 0x58465342;   // "XFSB"
  private const ushort InodeMagic = 0x494E;   // "IN"
  private const uint BnobtV5Magic = 0x41423342;  // "AB3B"
  private const uint CntbtV5Magic = 0x41423343;  // "AB3C"
  private const uint InobtV5Magic = 0x49414233;  // "IAB3"
  private const uint Dir3BlockMagic = 0x58444233; // "XDB3"
  private const uint Dir3DataMagic = 0x58444433;  // "XDD3"
  private const ushort Dir3Leaf1Magic = 0x3DF1;   // XFS_DIR3_LEAF1_MAGIC
  private const ushort Dir2DataFreeTag = 0xFFFF;

  private const int SectorSize = 512;
  private const int InodesPerBlock = 16;       // 4096 / 256
  private const int InodesPerChunk = 64;
  private const int InodeChunkBlocks = InodesPerChunk / InodesPerBlock; // 4
  private const int ForkOffset = 176;          // v3 dinode literal-area offset
  private const int DiCrcOffset = 100;
  private const int Dir3DataHdrSize = 64;
  private const int Dir3LeafHdrSize = 64;
  private const int Dir3DataCrcOffset = 4;
  private const int Dir3LeafCrcOffset = 12;

  // AG-internal sector/block positions (must match XfsWriter).
  private const int AgfSector = 1;
  private const int AgiSector = 2;
  private const int BnobtBlock = 1;
  private const int CntbtBlock = 2;
  private const int InobtBlock = 3;

  private const int SbCrcOffset = 224;
  private const int AgfCrcOffset = 216;
  private const int AgiCrcOffset = 312;
  private const int BtreeCrcOffset = 52;
  private const int BtreeRecOffset = 56;

  /// <summary>
  /// Adds (or replaces by name) <paramref name="name"/> into the directory tree
  /// of the in-memory XFS image. Throws so the caller can rebuild for any case
  /// this path does not handle (see the class summary).
  /// </summary>
  public static void AddFile(byte[] image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var norm = name.Replace('\\', '/').Trim('/');
    if (norm.Length == 0)
      throw new NotSupportedException("XFS in-place add: empty name.");

    var geo = ParseSuperblock(image);
    if (geo.BlockSize != 4096 || geo.InodeSize != 256)
      throw new NotSupportedException("XFS in-place add: only the writer's 4 KiB/256 B geometry is supported.");

    var crc = new CrcSet();

    var parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var leafName = parts[^1];
    var nameBytes = Encoding.UTF8.GetBytes(leafName);
    if (nameBytes.Length == 0 || nameBytes.Length > 250)
      throw new NotSupportedException("XFS in-place add: name length out of range.");

    // ── Resolve (or create in place) the parent directory inode ──
    var parentIno = geo.RootIno;
    for (var p = 0; p < parts.Length - 1; p++) {
      var child = FindChildInDir(image, geo, parentIno, parts[p]);
      if (child is { } existing) {
        if (!IsDirectoryInode(image, geo, existing))
          throw new NotSupportedException("XFS in-place add: path component is a file, not a directory.");
        parentIno = existing;
      } else {
        // Create the intermediate directory in place.
        parentIno = CreateChildDirectory(image, geo, parentIno, parts[p], crc);
      }
    }

    // ── Replace-by-name: remove the existing entry (free its inode + extent) ──
    var dup = FindChildInDir(image, geo, parentIno, leafName);
    if (dup is { } dupIno) {
      if (IsDirectoryInode(image, geo, dupIno))
        throw new NotSupportedException("XFS in-place add: replacing a directory with a file uses rebuild.");
      RemoveEntryFromDir(image, geo, parentIno, leafName, crc);
      FreeRegularFileInode(image, geo, dupIno, crc);
    }

    // ── Allocate inode + data extent for the new file ──
    var newIno = AllocateInode(image, geo, crc);
    var blocksNeeded = data.Length == 0 ? 1 : (int)((data.Length + geo.BlockSize - 1) / geo.BlockSize);
    var dataStartBlock = AllocateExtent(image, geo, blocksNeeded, crc);

    var dataByteOffset = (long)dataStartBlock * geo.BlockSize;
    if (dataByteOffset + (long)blocksNeeded * geo.BlockSize > image.Length)
      throw new IOException("XFS in-place add: allocated extent out of image bounds.");
    image.AsSpan((int)dataByteOffset, blocksNeeded * geo.BlockSize).Clear();
    data.CopyTo(image, (int)dataByteOffset);

    var inodeOff = (int)InodeByteOffset(geo, newIno);
    WriteRegularFileInode(image, inodeOff, newIno, data.Length, dataStartBlock, blocksNeeded, geo);
    AddInodeCrc(crc, geo, newIno);

    // ── Insert the directory entry into the parent ──
    InsertDirEntry(image, geo, parentIno, leafName, newIno, isDir: false, crc);

    // ── Recompute every touched CRC ──
    crc.Add(0, SbCrcOffset, SectorSize);
    crc.Add(AgfSector * SectorSize, AgfCrcOffset, SectorSize);
    crc.Add(AgiSector * SectorSize, AgiCrcOffset, SectorSize);
    crc.Add(BnobtBlock * geo.BlockSize, BtreeCrcOffset, geo.BlockSize);
    crc.Add(CntbtBlock * geo.BlockSize, BtreeCrcOffset, geo.BlockSize);
    crc.Add(InobtBlock * geo.BlockSize, BtreeCrcOffset, geo.BlockSize);
    crc.Flush(image);
  }

  // ── Geometry ──────────────────────────────────────────────────────────────

  private readonly record struct Geo(
      int BlockSize, int InodeSize, ulong RootIno, uint AgBlocks, uint AgCount,
      byte AgBlkLog, byte DirBlkLog, ushort Version, uint FeaturesIncompat) {
    public bool HasFtype => (this.FeaturesIncompat & 0x1) != 0;
    public int InoPbLog {
      get { var l = 0; for (var v = this.BlockSize / this.InodeSize; v > 1; v >>= 1) l++; return l; }
    }
    public int AgInoLog => this.AgBlkLog + this.InoPbLog;
    public int DirBlockSize => this.BlockSize << this.DirBlkLog;
    public int DirFsBlocks => 1 << this.DirBlkLog;
    // Logical fs-block offset where the directory leaf/free space begins (32 GiB).
    public long Dir2LeafFsBlockOffset {
      get { var blockLog = 0; for (var v = this.BlockSize; v > 1; v >>= 1) blockLog++; return 1L << (35 - blockLog); }
    }
  }

  private static Geo ParseSuperblock(byte[] image) {
    if (image.Length < 512 || BinaryPrimitives.ReadUInt32BigEndian(image) != XfsMagic)
      throw new InvalidDataException("XFS: invalid superblock magic.");
    var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(4));
    var rootIno = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(56));
    var agBlocks = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(84));
    var agCount = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(88));
    var version = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(100));
    var inodeSize = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(104));
    var agBlkLog = image[124];
    var dirBlkLog = image[192];
    uint featuresIncompat = 0;
    if ((version & 0xF) >= 5)
      featuresIncompat = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(216));
    else
      throw new NotSupportedException("XFS in-place add: only v5 (CRC) images are supported.");
    return new Geo(blockSize, inodeSize, rootIno, agBlocks, agCount, agBlkLog, dirBlkLog, version, featuresIncompat);
  }

  // Absolute byte offset of an inode (AG 0 assumed for the writer's layout, but
  // the agino math is general so AG number is honoured).
  private static long InodeByteOffset(Geo geo, ulong ino) {
    var agNo = ino >> geo.AgInoLog;
    var agIno = ino & ((1UL << geo.AgInoLog) - 1);
    var block = agIno / (ulong)InodesPerBlock;
    var offset = agIno % (ulong)InodesPerBlock;
    return (long)((agNo * geo.AgBlocks + block) * (ulong)geo.BlockSize + offset * (ulong)geo.InodeSize);
  }

  // Registers an inode's own 256-byte region for CRC backfill (field at +100).
  private static void AddInodeCrc(CrcSet crc, Geo geo, ulong ino)
    => crc.Add((int)InodeByteOffset(geo, ino), DiCrcOffset, geo.InodeSize);

  // ── Inode core / metadata helpers ───────────────────────────────────────────

  private static bool IsDirectoryInode(byte[] image, Geo geo, ulong ino) {
    var off = (int)InodeByteOffset(geo, ino);
    if (BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(off)) != InodeMagic) return false;
    return (BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(off + 2)) & 0xF000) == 0x4000;
  }

  private static byte[] SbUuid(byte[] image) => image.AsSpan(32, 16).ToArray();

  // ════════════════════════════════════════════════════════════════════════════
  //  INODE ALLOCATION  (inobt + AGI + sb), with chunk growth
  // ════════════════════════════════════════════════════════════════════════════

  private static ulong AllocateInode(byte[] image, Geo geo, CrcSet crc) {
    var inobtOff = InobtBlock * geo.BlockSize;
    if (BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(inobtOff)) != InobtV5Magic)
      throw new NotSupportedException("XFS in-place add: unexpected inobt magic.");
    var numrecs = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(inobtOff + 6));

    // Try every existing chunk record for a free slot.
    for (var c = 0; c < numrecs; c++) {
      var rec = inobtOff + BtreeRecOffset + c * 16;
      var startIno = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(rec));
      var freeCount = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(rec + 4));
      var freeMask = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(rec + 8));
      if (freeCount == 0 || freeMask == 0) continue;

      var bit = System.Numerics.BitOperations.TrailingZeroCount(freeMask);
      freeMask &= ~(1UL << bit);
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(rec + 4), freeCount - 1);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(rec + 8), freeMask);

      DecrementInodeCounts(image);

      var agNo = geo.RootIno >> geo.AgInoLog;
      return (agNo << geo.AgInoLog) | (ulong)(startIno + (uint)bit);
    }

    // No free slot anywhere: grow a new 64-inode chunk.
    return GrowInodeChunk(image, geo, crc);
  }

  private static void DecrementInodeCounts(byte[] image) {
    var agiOff = AgiSector * SectorSize;
    var agiFree = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(agiOff + 28));
    if (agiFree == 0) throw new IOException("XFS in-place add: AGI freecount underflow.");
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(agiOff + 28), agiFree - 1);

    var ifree = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(136));
    if (ifree == 0) throw new IOException("XFS in-place add: sb_ifree underflow.");
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(136), ifree - 1);
  }

  // Allocates a 4-block inode chunk, fills it with valid empty v3 inodes, claims
  // slot 0 of it, inserts a new inobt record (sorted by ir_startino), and bumps
  // AGI agi_count/agi_newino + sb_icount. AGI freecount + sb_ifree net change for
  // a fresh 64-slot chunk with one slot claimed is +63.
  private static ulong GrowInodeChunk(byte[] image, Geo geo, CrcSet crc) {
    // Inode chunks must be aligned to the inode-cluster (sb_inoalignmt) boundary.
    var inoAlign = (int)BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(180));
    if (inoAlign < 1) inoAlign = 1;
    var chunkStartBlock = AllocateAlignedExtent(image, geo, InodeChunkBlocks, inoAlign, crc);

    var agNo = geo.RootIno >> geo.AgInoLog;
    var startAgino = (uint)(chunkStartBlock * InodesPerBlock);
    var chunkBaseIno = (agNo << geo.AgInoLog) | startAgino;

    // Initialise all 64 slots as valid empty inodes (mode 0, format DEV).
    for (var s = 0; s < InodesPerChunk; s++) {
      var ino = chunkBaseIno + (ulong)s;
      var ioff = (int)InodeByteOffset(geo, ino);
      WriteEmptyInode(image, ioff, ino, geo);
    }
    // Claim slot 0; mark the rest free (mask bits 1..63).
    var freeMask = ulong.MaxValue & ~1UL;

    // Insert the inobt record sorted by ir_startino.
    var inobtOff = InobtBlock * geo.BlockSize;
    var numrecs = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(inobtOff + 6));
    int insertAt = numrecs;
    for (var c = 0; c < numrecs; c++) {
      var rec = inobtOff + BtreeRecOffset + c * 16;
      var s = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(rec));
      if (startAgino < s) { insertAt = c; break; }
    }
    // Capacity check: records must fit in the single leaf block.
    var maxRecs = (geo.BlockSize - BtreeRecOffset) / 16;
    if (numrecs + 1 > maxRecs)
      throw new NotSupportedException("XFS in-place add: inobt leaf full (multi-level inobt) — uses rebuild.");
    // Shift records after insertAt up by one slot.
    var recsBase = inobtOff + BtreeRecOffset;
    for (var c = numrecs; c > insertAt; c--)
      image.AsSpan(recsBase + (c - 1) * 16, 16).CopyTo(image.AsSpan(recsBase + c * 16));
    var nrec = recsBase + insertAt * 16;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(nrec), startAgino);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(nrec + 4), InodesPerChunk - 1u);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(nrec + 8), freeMask);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(inobtOff + 6), (ushort)(numrecs + 1));

    // AGI: agi_count += 64, agi_freecount += 63, agi_newino = startAgino.
    var agiOff = AgiSector * SectorSize;
    var agiCount = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(agiOff + 16));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(agiOff + 16), agiCount + InodesPerChunk);
    var agiFree = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(agiOff + 28));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(agiOff + 28), agiFree + (InodesPerChunk - 1));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(agiOff + 32), startAgino); // agi_newino

    // sb: sb_icount += 64, sb_ifree += 63.
    var icount = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(128));
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(128), icount + InodesPerChunk);
    var ifree = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(136));
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(136), ifree + (InodesPerChunk - 1));

    // Per-inode CRC (each inode is its own CRC region).
    for (var s = 0; s < InodesPerChunk; s++)
      AddInodeCrc(crc, geo, chunkBaseIno + (ulong)s);

    return chunkBaseIno; // slot 0
  }

  private static void WriteEmptyInode(byte[] image, int ioff, ulong ino, Geo geo) {
    var di = image.AsSpan(ioff, geo.InodeSize);
    di.Clear();
    BinaryPrimitives.WriteUInt16BigEndian(di[0..], InodeMagic);
    BinaryPrimitives.WriteUInt16BigEndian(di[2..], 0);                 // mode = 0
    di[4] = 3;                                                         // v3
    di[5] = 0;                                                         // di_format = DEV
    di[83] = 0;                                                        // di_aformat
    BinaryPrimitives.WriteUInt32BigEndian(di[96..], 0xFFFFFFFFu);      // di_next_unlinked
    BinaryPrimitives.WriteUInt64BigEndian(di[152..], ino);            // di_ino
    SbUuid(image).CopyTo(di[160..]);
  }

  // ════════════════════════════════════════════════════════════════════════════
  //  EXTENT ALLOCATION  (bnobt + cntbt + AGF + sb), multi-record + middle carve
  // ════════════════════════════════════════════════════════════════════════════

  private readonly record struct FreeExtent(uint Start, uint Len);

  private static int AllocateExtent(byte[] image, Geo geo, int blocks, CrcSet crc)
    => AllocateAlignedExtent(image, geo, blocks, 1, crc);

  // Allocates `blocks` blocks from AG 0 free space, choosing a record that can
  // satisfy the request at the required alignment, carving from its head (after
  // alignment padding). The carve may split one free record into two (head pad +
  // tail remainder), updating both bnobt (start-keyed) and cntbt (count,start-keyed)
  // and re-deriving agf_freeblks/agf_longest. Returns the allocated start agbno.
  private static int AllocateAlignedExtent(byte[] image, Geo geo, int blocks, int align, CrcSet crc) {
    var extents = ReadFreeExtents(image, geo);
    if (extents.Count == 0)
      throw new IOException("XFS in-place add: no free space in AG 0.");

    // Choose the smallest extent that fits the aligned request (best-fit).
    var bestIdx = -1;
    uint bestStart = 0;
    long bestWaste = long.MaxValue;
    for (var i = 0; i < extents.Count; i++) {
      var e = extents[i];
      var aligned = (uint)(((e.Start + align - 1) / align) * align);
      var pad = aligned - e.Start;
      if (pad + (uint)blocks > e.Len) continue;
      var waste = (long)e.Len - pad - blocks;
      if (waste < bestWaste) { bestWaste = waste; bestIdx = i; bestStart = aligned; }
    }
    if (bestIdx < 0)
      throw new IOException($"XFS in-place add: no free extent fits {blocks} block(s) at align {align}.");

    var chosen = extents[bestIdx];
    extents.RemoveAt(bestIdx);
    // Head padding (alignment) stays free.
    if (bestStart > chosen.Start)
      extents.Add(new FreeExtent(chosen.Start, bestStart - chosen.Start));
    // Tail remainder stays free.
    var tailStart = bestStart + (uint)blocks;
    var tailLen = chosen.Start + chosen.Len - tailStart;
    if (tailLen > 0)
      extents.Add(new FreeExtent(tailStart, tailLen));

    WriteFreeExtents(image, geo, extents, crc);
    UpdateAgfAndSb(image, blocks, extents);
    return (int)bestStart;
  }

  private static List<FreeExtent> ReadFreeExtents(byte[] image, Geo geo) {
    var bnobtOff = BnobtBlock * geo.BlockSize;
    if (BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(bnobtOff)) != BnobtV5Magic)
      throw new NotSupportedException("XFS in-place add: unexpected bnobt magic.");
    var level = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(bnobtOff + 4));
    if (level != 0)
      throw new NotSupportedException("XFS in-place add: multi-level bnobt — uses rebuild.");
    var numrecs = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(bnobtOff + 6));
    var list = new List<FreeExtent>(numrecs);
    for (var i = 0; i < numrecs; i++) {
      var rec = bnobtOff + BtreeRecOffset + i * 8;
      var start = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(rec));
      var len = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(rec + 4));
      list.Add(new FreeExtent(start, len));
    }
    return list;
  }

  private static void WriteFreeExtents(byte[] image, Geo geo, List<FreeExtent> extents, CrcSet crc) {
    var maxRecs = (geo.BlockSize - BtreeRecOffset) / 8;
    if (extents.Count > maxRecs)
      throw new NotSupportedException("XFS in-place add: free-space btree would overflow a single leaf — uses rebuild.");

    // bnobt: sorted by start. cntbt: sorted by (count, start).
    var byStart = extents.OrderBy(e => e.Start).ToList();
    var byCount = extents.OrderBy(e => e.Len).ThenBy(e => e.Start).ToList();

    WriteBtreeLeaf(image, BnobtBlock * geo.BlockSize, byStart);
    WriteBtreeLeaf(image, CntbtBlock * geo.BlockSize, byCount);
    crc.Add(BnobtBlock * geo.BlockSize, BtreeCrcOffset, geo.BlockSize);
    crc.Add(CntbtBlock * geo.BlockSize, BtreeCrcOffset, geo.BlockSize);
  }

  private static void WriteBtreeLeaf(byte[] image, int blockOff, List<FreeExtent> recs) {
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(blockOff + 6), (ushort)recs.Count); // numrecs
    for (var i = 0; i < recs.Count; i++) {
      var rec = blockOff + BtreeRecOffset + i * 8;
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(rec), recs[i].Start);
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(rec + 4), recs[i].Len);
    }
  }

  private static void UpdateAgfAndSb(byte[] image, int blocksTaken, List<FreeExtent> extents) {
    var agfOff = AgfSector * SectorSize;
    var freeblks = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(agfOff + 52));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(agfOff + 52), freeblks - (uint)blocksTaken);
    uint longest = 0;
    foreach (var e in extents) if (e.Len > longest) longest = e.Len;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(agfOff + 56), longest);

    var fdblocks = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(144));
    if (fdblocks < (ulong)blocksTaken) throw new IOException("XFS in-place add: sb_fdblocks underflow.");
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(144), fdblocks - (ulong)blocksTaken);
  }

  // Returns blocks to free space (merging adjacent extents) — used by replace.
  private static void FreeExtentBlocks(byte[] image, Geo geo, uint start, uint len, CrcSet crc) {
    if (len == 0) return;
    var extents = ReadFreeExtents(image, geo);
    extents.Add(new FreeExtent(start, len));
    // Coalesce adjacent extents.
    extents = Coalesce(extents);
    WriteFreeExtents(image, geo, extents, crc);

    var agfOff = AgfSector * SectorSize;
    var freeblks = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(agfOff + 52));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(agfOff + 52), freeblks + len);
    uint longest = 0;
    foreach (var e in extents) if (e.Len > longest) longest = e.Len;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(agfOff + 56), longest);
    var fdblocks = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(144));
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(144), fdblocks + len);
  }

  private static List<FreeExtent> Coalesce(List<FreeExtent> extents) {
    var sorted = extents.OrderBy(e => e.Start).ToList();
    var result = new List<FreeExtent>();
    foreach (var e in sorted) {
      if (result.Count > 0) {
        var last = result[^1];
        if (last.Start + last.Len == e.Start) {
          result[^1] = new FreeExtent(last.Start, last.Len + e.Len);
          continue;
        }
      }
      result.Add(e);
    }
    return result;
  }

  // ════════════════════════════════════════════════════════════════════════════
  //  REGULAR FILE INODE
  // ════════════════════════════════════════════════════════════════════════════

  private static void WriteRegularFileInode(byte[] image, int ioff, ulong ino, long size,
      int startBlock, int blockCount, Geo geo) {
    var di = image.AsSpan(ioff, geo.InodeSize);
    di.Clear();
    BinaryPrimitives.WriteUInt16BigEndian(di[0..], InodeMagic);
    BinaryPrimitives.WriteUInt16BigEndian(di[2..], 0x81A4);            // S_IFREG | 0644
    di[4] = 3;
    di[5] = 2;                                                         // extents
    BinaryPrimitives.WriteUInt32BigEndian(di[16..], 1);               // nlink
    BinaryPrimitives.WriteUInt64BigEndian(di[56..], (ulong)size);     // di_size
    BinaryPrimitives.WriteUInt64BigEndian(di[64..], (ulong)blockCount); // di_nblocks
    BinaryPrimitives.WriteUInt32BigEndian(di[76..], 1);               // di_nextents
    di[83] = 2;                                                       // di_aformat
    BinaryPrimitives.WriteUInt32BigEndian(di[96..], 0xFFFFFFFFu);     // di_next_unlinked
    BinaryPrimitives.WriteUInt64BigEndian(di[152..], ino);           // di_ino
    SbUuid(image).CopyTo(di[160..]);

    var startBlk = (ulong)startBlock;
    var cnt = (ulong)blockCount;
    var hi = (startBlk >> 43) & 0x1FF;
    var lo = (startBlk << 21) | (cnt & 0x1FFFFF);
    BinaryPrimitives.WriteUInt64BigEndian(di[ForkOffset..], hi);
    BinaryPrimitives.WriteUInt64BigEndian(di[(ForkOffset + 8)..], lo);
  }

  // Frees a regular file's data extent(s) and turns its inode back into an empty
  // slot (mode 0) — marking the inobt bit free + bumping counters.
  private static void FreeRegularFileInode(byte[] image, Geo geo, ulong ino, CrcSet crc) {
    var ioff = (int)InodeByteOffset(geo, ino);
    var format = image[ioff + 5];
    if (format == 2) {
      var nextents = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(ioff + 76));
      var extPos = ioff + ForkOffset;
      for (var e = 0; e < nextents; e++) {
        var hi = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(extPos));
        var lo = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(extPos + 8));
        extPos += 16;
        var blockCount = (uint)(lo & 0x1FFFFF);
        var startBlock = (uint)(((hi & 0x1FF) << 43) | (lo >> 21));
        FreeExtentBlocks(image, geo, startBlock, blockCount, crc);
      }
    }
    // Re-init the inode as empty and free its inobt bit.
    WriteEmptyInode(image, ioff, ino, geo);
    AddInodeCrc(crc, geo, ino);
    FreeInodeBit(image, geo, ino);
  }

  private static void FreeInodeBit(byte[] image, Geo geo, ulong ino) {
    var agino = (uint)(ino & ((1UL << geo.AgInoLog) - 1));
    var inobtOff = InobtBlock * geo.BlockSize;
    var numrecs = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(inobtOff + 6));
    for (var c = 0; c < numrecs; c++) {
      var rec = inobtOff + BtreeRecOffset + c * 16;
      var startIno = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(rec));
      if (agino < startIno || agino >= startIno + InodesPerChunk) continue;
      var bit = (int)(agino - startIno);
      var freeCount = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(rec + 4));
      var freeMask = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(rec + 8));
      freeMask |= 1UL << bit;
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(rec + 4), freeCount + 1);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(rec + 8), freeMask);

      var agiOff = AgiSector * SectorSize;
      var agiFree = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(agiOff + 28));
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(agiOff + 28), agiFree + 1);
      var ifree = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(136));
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(136), ifree + 1);
      return;
    }
  }

  // ════════════════════════════════════════════════════════════════════════════
  //  DIRECTORY OPERATIONS  (lookup / insert / remove / promote)
  // ════════════════════════════════════════════════════════════════════════════

  private static int Dir2EntrySize(int nameLen, bool hasFtype)
    => (8 + 1 + nameLen + (hasFtype ? 1 : 0) + 2 + 7) & ~7;

  // XFS directory name hash (xfs_da_hashname).
  private static uint HashName(string name) {
    var bytes = Encoding.UTF8.GetBytes(name);
    var len = Math.Min(bytes.Length, 250);
    uint hash = 0;
    var i = 0;
    for (; len >= 4; len -= 4, i += 4)
      hash = ((uint)bytes[i] << 21) ^ ((uint)bytes[i + 1] << 14)
           ^ ((uint)bytes[i + 2] << 7) ^ bytes[i + 3]
           ^ ((hash << 28) | (hash >> 4));
    return len switch {
      3 => ((uint)bytes[i] << 14) ^ ((uint)bytes[i + 1] << 7) ^ bytes[i + 2] ^ ((hash << 21) | (hash >> 11)),
      2 => ((uint)bytes[i] << 7) ^ bytes[i + 1] ^ ((hash << 14) | (hash >> 18)),
      1 => bytes[i] ^ ((hash << 7) | (hash >> 25)),
      _ => hash,
    };
  }

  // Returns the child inode for a name within a directory, or null.
  private static ulong? FindChildInDir(byte[] image, Geo geo, ulong dirIno, string name) {
    var ioff = (int)InodeByteOffset(geo, dirIno);
    if (BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(ioff)) != InodeMagic) return null;
    var format = image[ioff + 5];
    if (format == 1)
      return FindInShortForm(image, geo, ioff, name);
    if (format == 2)
      return FindInBlockOrLeaf(image, geo, ioff, name);
    return null;
  }

  private static ulong? FindInShortForm(byte[] image, Geo geo, int ioff, string name) {
    var dirOff = ioff + ForkOffset;
    var count = image[dirOff];
    var i8count = image[dirOff + 1];
    var ftypeLen = geo.HasFtype ? 1 : 0;
    var pos = dirOff + (i8count != 0 ? 10 : 6);
    var inoSize = i8count != 0 ? 8 : 4;
    for (var i = 0; i < count; i++) {
      var entNameLen = image[pos];
      var entName = Encoding.UTF8.GetString(image, pos + 3, entNameLen);
      var inoPos = pos + 3 + entNameLen + ftypeLen;
      if (string.Equals(entName, name, StringComparison.Ordinal))
        return inoSize == 4
          ? BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(inoPos))
          : BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(inoPos));
      pos = inoPos + inoSize;
    }
    return null;
  }

  private static ulong? FindInBlockOrLeaf(byte[] image, Geo geo, int ioff, string name) {
    foreach (var (entIno, entName, _, _, _) in EnumerateBlockDirEntries(image, geo, ioff))
      if (string.Equals(entName, name, StringComparison.Ordinal))
        return entIno;
    return null;
  }

  // Yields (ino, name, isDir, dataBlockByteOffset, offsetInBlock) for every real
  // entry (excluding "." / "..") in a block/leaf-form directory.
  private static IEnumerable<(ulong Ino, string Name, bool IsDir, int BlockByteOff, int OffInBlock)>
      EnumerateBlockDirEntries(byte[] image, Geo geo, int ioff) {
    var nextents = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(ioff + 76));
    var ftypeLen = geo.HasFtype ? 1 : 0;
    var extPos = ioff + ForkOffset;
    var leafThreshold = geo.Dir2LeafFsBlockOffset;
    for (var e = 0; e < nextents; e++) {
      var hi = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(extPos));
      var lo = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(extPos + 8));
      extPos += 16;
      var blockCount = (int)(lo & 0x1FFFFF);
      var startBlock = ((hi & 0x1FF) << 43) | (lo >> 21);
      var startOff = (long)((hi >> 9) & 0x3FFFFFFFFFFFFFUL);
      if (startOff >= leafThreshold) continue; // leaf/free index space

      for (var b = 0; b < blockCount; b += geo.DirFsBlocks) {
        var blockByteOff = (int)((startBlock + (ulong)b) * (ulong)geo.BlockSize);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(blockByteOff));
        if (magic != Dir3BlockMagic && magic != Dir3DataMagic) continue;
        var pos = blockByteOff + Dir3DataHdrSize;
        var end = blockByteOff + geo.DirBlockSize;
        // For a single-block dir ("XDB3") the data area stops short of the
        // embedded leaf index + 8-byte tail at the block end.
        if (magic == Dir3BlockMagic) {
          var leafCount = (int)BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(end - 8));
          end -= 8 + leafCount * 8;
        }
        while (pos + 12 <= end) {
          if (BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(pos)) == Dir2DataFreeTag) {
            var freeLen = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(pos + 2));
            if (freeLen < 8) break;
            pos += freeLen;
            continue;
          }
          var entIno = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(pos));
          var nameLen = image[pos + 8];
          if (nameLen == 0) { pos += 8; continue; }
          var entName = Encoding.UTF8.GetString(image, pos + 9, nameLen);
          var entLen = Dir2EntrySize(nameLen, geo.HasFtype);
          if (entName != "." && entName != "..") {
            var isDir = (BinaryPrimitives.ReadUInt16BigEndian(
              image.AsSpan((int)InodeByteOffset(geo, entIno) + 2)) & 0xF000) == 0x4000;
            yield return (entIno, entName, isDir, blockByteOff, pos - blockByteOff);
          }
          pos += entLen;
        }
      }
    }
  }

  // Dispatches insertion based on the directory's current form.
  private static void InsertDirEntry(byte[] image, Geo geo, ulong dirIno, string name,
      ulong childIno, bool isDir, CrcSet crc) {
    var ioff = (int)InodeByteOffset(geo, dirIno);
    var format = image[ioff + 5];
    if (format == 1 && childIno <= 0xFFFFFFFFUL &&
        TryInsertShortForm(image, geo, ioff, name, childIno, isDir)) {
      AddInodeCrc(crc, geo, dirIno);
      return;
    }
    // Short-form is full (or out-of-line already): gather the entry set, add the
    // new one, and re-lay the directory in the smallest form that fits.
    var (parentIno, entries) = ReadDirModel(image, geo, dirIno);
    entries.Add(new DirEnt(childIno, name, isDir));
    RelayDirectory(image, geo, dirIno, parentIno, entries, crc);
  }

  // ── Short-form insert ──

  private static bool TryInsertShortForm(byte[] image, Geo geo, int ioff, string name,
      ulong childIno, bool isDir) {
    var dirOff = ioff + ForkOffset;
    var count = image[dirOff];
    var i8count = image[dirOff + 1];
    if (i8count != 0) return false; // writer keeps i8count == 0; large inos -> promote/rebuild
    if (childIno > 0xFFFFFFFFUL) return false;

    var nameBytes = Encoding.UTF8.GetBytes(name);
    var nameLen = nameBytes.Length;
    var ftypeLen = geo.HasFtype ? 1 : 0;

    var pos = dirOff + 6;
    ushort nextOffset = 0x60;
    for (var i = 0; i < count; i++) {
      var entNameLen = image[pos];
      var entOffset = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(pos + 1));
      var entStep = (entNameLen + 12 + 7) & ~7;
      nextOffset = (ushort)(entOffset + entStep);
      pos += 3 + entNameLen + ftypeLen + 4;
    }

    var newEntryBytes = 3 + nameLen + ftypeLen + 4;
    var newDirSize = (pos - dirOff) + newEntryBytes;
    if (ForkOffset + newDirSize > geo.InodeSize) return false;

    image[pos] = (byte)nameLen;
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(pos + 1), nextOffset);
    nameBytes.CopyTo(image, pos + 3);
    if (geo.HasFtype) image[pos + 3 + nameLen] = isDir ? (byte)2 : (byte)1;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(pos + 3 + nameLen + ftypeLen), (uint)childIno);

    image[dirOff] = (byte)(count + 1);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 56), (ulong)newDirSize);
    return true;
  }

  // ── Short-form remove (returns the removed child ino's ftype isDir) ──

  private static void RemoveEntryFromDir(byte[] image, Geo geo, ulong dirIno, string name, CrcSet crc) {
    var ioff = (int)InodeByteOffset(geo, dirIno);
    var format = image[ioff + 5];
    if (format == 1) {
      RemoveFromShortForm(image, geo, ioff, name);
      AddInodeCrc(crc, geo, dirIno);
      return;
    }
    var (parentIno, entries) = ReadDirModel(image, geo, dirIno);
    entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.Ordinal));
    RelayDirectory(image, geo, dirIno, parentIno, entries, crc);
  }

  private static void RemoveFromShortForm(byte[] image, Geo geo, int ioff, string name) {
    var dirOff = ioff + ForkOffset;
    var count = image[dirOff];
    var ftypeLen = geo.HasFtype ? 1 : 0;
    var pos = dirOff + 6;
    for (var i = 0; i < count; i++) {
      var entNameLen = image[pos];
      var entName = Encoding.UTF8.GetString(image, pos + 3, entNameLen);
      var entTotal = 3 + entNameLen + ftypeLen + 4;
      if (string.Equals(entName, name, StringComparison.Ordinal)) {
        // Shift the rest down, shrink the entry list, recompute di_size.
        var tailStart = pos + entTotal;
        var tailEnd = dirOff + 6;
        // Find current end of entries.
        var scan = dirOff + 6;
        for (var j = 0; j < count; j++) {
          var l = image[scan];
          scan += 3 + l + ftypeLen + 4;
        }
        var bytesAfter = scan - tailStart;
        if (bytesAfter > 0)
          image.AsSpan(tailStart, bytesAfter).CopyTo(image.AsSpan(pos));
        image[dirOff] = (byte)(count - 1);
        var newSize = scan - entTotal - dirOff;
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 56), (ulong)newSize);
        return;
      }
      pos += entTotal;
    }
  }

  // ════════════════════════════════════════════════════════════════════════════
  //  DIRECTORY MODEL  (read all entries, re-lay in place)
  // ════════════════════════════════════════════════════════════════════════════

  private readonly record struct DirEnt(ulong Ino, string Name, bool IsDir);

  // Reads a directory's parent inode + child entry set (excluding "." / "..").
  private static (ulong ParentIno, List<DirEnt> Entries) ReadDirModel(byte[] image, Geo geo, ulong dirIno) {
    var ioff = (int)InodeByteOffset(geo, dirIno);
    var format = image[ioff + 5];
    var entries = new List<DirEnt>();
    if (format == 1) {
      var dirOff = ioff + ForkOffset;
      var count = image[dirOff];
      var i8count = image[dirOff + 1];
      var ftypeLen = geo.HasFtype ? 1 : 0;
      var parent = i8count != 0
        ? BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(dirOff + 2))
        : BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(dirOff + 2));
      var pos = dirOff + (i8count != 0 ? 10 : 6);
      var inoSize = i8count != 0 ? 8 : 4;
      for (var i = 0; i < count; i++) {
        var nameLen = image[pos];
        var name = Encoding.UTF8.GetString(image, pos + 3, nameLen);
        var ftype = ftypeLen != 0 ? image[pos + 3 + nameLen] : (byte)0;
        var inoPos = pos + 3 + nameLen + ftypeLen;
        var ino = inoSize == 4
          ? BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(inoPos))
          : BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(inoPos));
        var isDir = ftypeLen != 0 ? ftype == 2 : IsDirectoryInode(image, geo, ino);
        entries.Add(new DirEnt(ino, name, isDir));
        pos = inoPos + inoSize;
      }
      return (parent, entries);
    }
    // Block/leaf form: parent is the ".." entry in the first data block.
    var parentIno = ReadDotDotIno(image, geo, ioff);
    foreach (var (entIno, entName, isDir, _, _) in EnumerateBlockDirEntries(image, geo, ioff))
      entries.Add(new DirEnt(entIno, entName, isDir));
    return (parentIno, entries);
  }

  private static ulong ReadDotDotIno(byte[] image, Geo geo, int ioff) {
    var nextents = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(ioff + 76));
    var extPos = ioff + ForkOffset;
    for (var e = 0; e < nextents; e++) {
      var hi = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(extPos));
      var lo = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(extPos + 8));
      extPos += 16;
      var startOff = (long)((hi >> 9) & 0x3FFFFFFFFFFFFFUL);
      if (startOff != 0) continue;
      var startBlock = ((hi & 0x1FF) << 43) | (lo >> 21);
      var blockOff = (int)(startBlock * (ulong)geo.BlockSize);
      var pos = blockOff + Dir3DataHdrSize;
      // "." then "..".
      for (var k = 0; k < 2; k++) {
        var nameLen = image[pos + 8];
        var name = Encoding.UTF8.GetString(image, pos + 9, nameLen);
        var ino = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(pos));
        if (name == "..") return ino;
        pos += Dir2EntrySize(nameLen, geo.HasFtype);
      }
    }
    throw new InvalidDataException("XFS in-place add: directory has no '..' entry.");
  }

  // Re-lays a directory in place: frees its current out-of-line blocks, then
  // writes it back in the smallest form (short / single-block / leaf) holding the
  // given entry set. The inode's other metadata (mode, nlink) is preserved.
  private static void RelayDirectory(byte[] image, Geo geo, ulong dirIno, ulong parentIno,
      List<DirEnt> entries, CrcSet crc) {
    var ioff = (int)InodeByteOffset(geo, dirIno);
    var format = image[ioff + 5];

    // Free any currently allocated directory blocks (data + leaf).
    if (format == 2)
      FreeDirInodeBlocks(image, geo, ioff, crc);

    // Sort children deterministically (directories before files, then by name).
    entries.Sort((a, b) => a.Name.CompareTo(b.Name));

    if (FitsShortForm(geo, entries) && AllInos32Bit(parentIno, entries)) {
      WriteShortFormDir(image, geo, ioff, parentIno, entries);
      AddInodeCrc(crc, geo, dirIno);
      return;
    }

    // Out-of-line: pack data blocks, choose single-block vs leaf.
    var dirFsBlocks = geo.DirFsBlocks;
    var packed = PackDataBlocks(geo, dirIno, parentIno, entries);
    var singleBlock = packed.Count == 1 && FitsSingleBlock(geo, entries);

    if (singleBlock) {
      var phys = AllocateAlignedExtent(image, geo, dirFsBlocks, dirFsBlocks, crc);
      WriteSingleBlockDir(image, geo, ioff, dirIno, phys, packed[0], crc);
      WriteDirInodeExtents(image, ioff, [(0, (ulong)phys, dirFsBlocks)],
        byteSize: geo.DirBlockSize, nblocks: dirFsBlocks);
      AddInodeCrc(crc, geo, dirIno);
      return;
    }

    // Leaf form: N contiguous data dir-blocks + 1 leaf index block. Allocate the
    // data blocks and the leaf block separately so each lands at a directory-block
    // aligned physical address.
    var dataDirBlocks = packed.Count;
    var dataPhys = AllocateAlignedExtent(image, geo, dataDirBlocks * dirFsBlocks, dirFsBlocks, crc);
    var leafPhys = AllocateAlignedExtent(image, geo, dirFsBlocks, dirFsBlocks, crc);
    WriteLeafFormDir(image, geo, ioff, dirIno, dataPhys, leafPhys, packed, crc);

    var extents = new List<(long, ulong, int)>();
    for (var db = 0; db < dataDirBlocks; db++)
      extents.Add((db * dirFsBlocks, (ulong)(dataPhys + db * dirFsBlocks), dirFsBlocks));
    extents.Add((geo.Dir2LeafFsBlockOffset, (ulong)leafPhys, dirFsBlocks));
    WriteDirInodeExtents(image, ioff, extents,
      byteSize: (long)dataDirBlocks * geo.DirBlockSize, nblocks: (dataDirBlocks + 1) * dirFsBlocks);
    AddInodeCrc(crc, geo, dirIno);
  }

  // Frees all fs blocks referenced by an extents-format directory inode.
  private static void FreeDirInodeBlocks(byte[] image, Geo geo, int ioff, CrcSet crc) {
    var nextents = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(ioff + 76));
    var extPos = ioff + ForkOffset;
    for (var e = 0; e < nextents; e++) {
      var hi = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(extPos));
      var lo = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(extPos + 8));
      extPos += 16;
      var blockCount = (uint)(lo & 0x1FFFFF);
      var startBlock = (uint)(((hi & 0x1FF) << 43) | (lo >> 21));
      FreeExtentBlocks(image, geo, startBlock, blockCount, crc);
    }
  }

  private static bool AllInos32Bit(ulong parentIno, List<DirEnt> entries) {
    if (parentIno > 0xFFFFFFFFUL) return false;
    foreach (var e in entries) if (e.Ino > 0xFFFFFFFFUL) return false;
    return true;
  }

  // ── Layout calculators (mirror XfsWriter) ──

  private const int ShortFormForkCapacity = 256 - 176; // inode literal area (80 B)

  private static bool FitsShortForm(Geo geo, List<DirEnt> entries) {
    var total = 6; // sf hdr (count, i8count, 4-byte parent)
    var ftypeLen = geo.HasFtype ? 1 : 0;
    foreach (var e in entries) {
      total += 3 + Math.Min(Encoding.UTF8.GetByteCount(e.Name), 250) + ftypeLen + 4;
      if (total > ShortFormForkCapacity) return false;
    }
    return total <= ShortFormForkCapacity;
  }

  private static bool FitsSingleBlock(Geo geo, List<DirEnt> entries) {
    var entryCount = entries.Count + 2; // "." + ".."
    var dataBytes = Dir3DataHdrSize + Dir2EntrySize(1, geo.HasFtype) + Dir2EntrySize(2, geo.HasFtype);
    foreach (var e in entries)
      dataBytes += Dir2EntrySize(Math.Min(Encoding.UTF8.GetByteCount(e.Name), 250), geo.HasFtype);
    var tailBytes = 8 + entryCount * 8;
    return dataBytes + tailBytes <= geo.DirBlockSize;
  }

  // Packs "." , ".." then children into data blocks; an entry never straddles.
  private static List<List<DirEnt>> PackDataBlocks(Geo geo, ulong dirIno, ulong parentIno,
      List<DirEnt> entries) {
    var blocks = new List<List<DirEnt>>();
    var current = new List<DirEnt>();
    var used = Dir3DataHdrSize;
    void Place(DirEnt ent) {
      var entLen = Dir2EntrySize(Math.Min(Encoding.UTF8.GetByteCount(ent.Name), 250), geo.HasFtype);
      if (used + entLen > geo.DirBlockSize) {
        blocks.Add(current);
        current = [];
        used = Dir3DataHdrSize;
      }
      current.Add(ent);
      used += entLen;
    }
    Place(new DirEnt(dirIno, ".", true));
    Place(new DirEnt(parentIno, "..", true));
    foreach (var e in entries) Place(e);
    blocks.Add(current);
    return blocks;
  }

  // ── Writers (mirror XfsWriter byte-for-byte) ──

  private static void WriteShortFormDir(byte[] image, Geo geo, int ioff, ulong parentIno,
      List<DirEnt> entries) {
    image[ioff + 5] = 1; // di_format = local
    var dirOff = ioff + ForkOffset;
    image[dirOff] = (byte)entries.Count;
    image[dirOff + 1] = 0;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(dirOff + 2), (uint)parentIno);
    var entryPos = dirOff + 6;
    ushort nextOffset = 0x60;
    var ftypeLen = geo.HasFtype ? 1 : 0;
    foreach (var e in entries) {
      var nameBytes = Encoding.UTF8.GetBytes(e.Name);
      var nameLen = Math.Min(nameBytes.Length, 250);
      image[entryPos] = (byte)nameLen;
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(entryPos + 1), nextOffset);
      nameBytes.AsSpan(0, nameLen).CopyTo(image.AsSpan(entryPos + 3));
      if (geo.HasFtype) image[entryPos + 3 + nameLen] = e.IsDir ? (byte)2 : (byte)1;
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(entryPos + 3 + nameLen + ftypeLen), (uint)e.Ino);
      entryPos += 3 + nameLen + ftypeLen + 4;
      nextOffset = (ushort)(nextOffset + ((nameLen + 12 + 7) & ~7));
    }
    var dirSize = entryPos - dirOff;
    // Clear the rest of the literal area (stale extent bytes from prior format).
    image.AsSpan(entryPos, geo.InodeSize - (entryPos - ioff)).Clear();
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 56), (ulong)dirSize); // di_size
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 64), 0);               // di_nblocks
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(ioff + 76), 0);               // di_nextents
  }

  private static void WriteDir3DataHeader(byte[] image, Geo geo, int blockOff, int fsBlock,
      ulong ownerIno, uint magic) {
    var h = image.AsSpan(blockOff);
    h[..Dir3DataHdrSize].Clear();
    BinaryPrimitives.WriteUInt32BigEndian(h[0..], magic);
    BinaryPrimitives.WriteUInt64BigEndian(h[8..], (ulong)fsBlock * (ulong)(geo.BlockSize / SectorSize));
    SbUuid(image).CopyTo(h[24..]);
    BinaryPrimitives.WriteUInt64BigEndian(h[40..], ownerIno);
  }

  private static void WriteDir3DataEntry(byte[] image, int entryOff, ulong ino, string name,
      bool isDir, ushort tag, Geo geo) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var nameLen = Math.Min(nameBytes.Length, 250);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(entryOff), ino);
    image[entryOff + 8] = (byte)nameLen;
    nameBytes.AsSpan(0, nameLen).CopyTo(image.AsSpan(entryOff + 9));
    if (geo.HasFtype) image[entryOff + 9 + nameLen] = isDir ? (byte)2 : (byte)1;
    var entLen = Dir2EntrySize(nameLen, geo.HasFtype);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(entryOff + entLen - 2), tag);
  }

  private static void WriteDir2DataUnused(byte[] image, Geo geo, int off, int length) {
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off), Dir2DataFreeTag);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + 2), (ushort)length);
    var blockStart = off - (off % geo.DirBlockSize);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + length - 2), (ushort)(off - blockStart));
  }

  private static void WriteSingleBlockDir(byte[] image, Geo geo, int ioff, ulong dirIno,
      int physBlock, List<DirEnt> entries, CrcSet crc) {
    var blockByteOff = physBlock * geo.BlockSize;
    image.AsSpan(blockByteOff, geo.DirBlockSize).Clear();
    WriteDir3DataHeader(image, geo, blockByteOff, physBlock, dirIno, Dir3BlockMagic);

    // Build the full entry list (".", "..", children) — caller passed packed[0].
    var pos = Dir3DataHdrSize;
    var placed = new List<(uint Hash, uint Address)>();
    foreach (var e in entries) {
      WriteDir3DataEntry(image, blockByteOff + pos, e.Ino, e.Name, e.IsDir, (ushort)pos, geo);
      placed.Add((HashName(e.Name), (uint)(pos >> 3)));
      pos += Dir2EntrySize(Math.Min(Encoding.UTF8.GetByteCount(e.Name), 250), geo.HasFtype);
    }
    var entryCount = entries.Count;
    var usableEnd = geo.DirBlockSize - (8 + entryCount * 8);
    var freeLen = usableEnd - pos;
    if (freeLen >= 8) {
      WriteDir2DataUnused(image, geo, blockByteOff + pos, freeLen);
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(blockByteOff + 48), (ushort)pos);
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(blockByteOff + 50), (ushort)freeLen);
    }

    // Leaf entries + tail at the block end, sorted by hash then address.
    var sorted = placed.OrderBy(p => p.Hash).ThenBy(p => p.Address).ToList();
    var tailOff = blockByteOff + geo.DirBlockSize - 8;
    var leafStart = tailOff - sorted.Count * 8;
    for (var i = 0; i < sorted.Count; i++) {
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(leafStart + i * 8), sorted[i].Hash);
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(leafStart + i * 8 + 4), sorted[i].Address);
    }
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(tailOff), (uint)sorted.Count);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(tailOff + 4), 0);

    crc.Add(blockByteOff, Dir3DataCrcOffset, geo.DirBlockSize);
  }

  private static void WriteLeafFormDir(byte[] image, Geo geo, int ioff, ulong dirIno,
      int dataPhys, int leafPhys, List<List<DirEnt>> packed, CrcSet crc) {
    var dirFsBlocks = geo.DirFsBlocks;
    var dataDirBlocks = packed.Count;
    var placed = new List<(uint Hash, int DirBlock, int OffInBlock)>();
    var blockBestFree = new int[dataDirBlocks];

    for (var db = 0; db < dataDirBlocks; db++) {
      var firstFsBlock = dataPhys + db * dirFsBlocks;
      var blockByteOff = firstFsBlock * geo.BlockSize;
      image.AsSpan(blockByteOff, geo.DirBlockSize).Clear();
      WriteDir3DataHeader(image, geo, blockByteOff, firstFsBlock, dirIno, Dir3DataMagic);
      crc.Add(blockByteOff, Dir3DataCrcOffset, geo.DirBlockSize);

      var pos = Dir3DataHdrSize;
      foreach (var e in packed[db]) {
        WriteDir3DataEntry(image, blockByteOff + pos, e.Ino, e.Name, e.IsDir, (ushort)pos, geo);
        placed.Add((HashName(e.Name), db, pos));
        pos += Dir2EntrySize(Math.Min(Encoding.UTF8.GetByteCount(e.Name), 250), geo.HasFtype);
      }
      var freeLen = geo.DirBlockSize - pos;
      if (freeLen >= 8) {
        WriteDir2DataUnused(image, geo, blockByteOff + pos, freeLen);
        blockBestFree[db] = freeLen;
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(blockByteOff + 48), (ushort)pos);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(blockByteOff + 50), (ushort)freeLen);
      }
    }

    // Leaf index block (magic 0x3df1).
    var leafByteOff = leafPhys * geo.BlockSize;
    image.AsSpan(leafByteOff, geo.DirBlockSize).Clear();
    var leafEntries = placed
      .Select(p => (p.Hash, Address: (uint)(((long)p.DirBlock * geo.DirBlockSize + p.OffInBlock) >> 3)))
      .OrderBy(e => e.Hash).ThenBy(e => e.Address).ToList();

    // Leaf capacity check (header + 8/entry + bests + tail).
    var leafAvail = geo.DirBlockSize - Dir3LeafHdrSize - 4 - dataDirBlocks * 2;
    if (leafEntries.Count > leafAvail / 8)
      throw new NotSupportedException("XFS in-place add: directory too large for single leaf (node form) — uses rebuild.");

    var h = image.AsSpan(leafByteOff);
    BinaryPrimitives.WriteUInt16BigEndian(h[8..], Dir3Leaf1Magic);
    BinaryPrimitives.WriteUInt64BigEndian(h[16..], (ulong)leafPhys * (ulong)(geo.BlockSize / SectorSize));
    SbUuid(image).CopyTo(h[32..]);
    BinaryPrimitives.WriteUInt64BigEndian(h[48..], dirIno);
    BinaryPrimitives.WriteUInt16BigEndian(h[56..], (ushort)leafEntries.Count);
    BinaryPrimitives.WriteUInt16BigEndian(h[58..], 0);

    var pos2 = Dir3LeafHdrSize;
    foreach (var (hash, address) in leafEntries) {
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(leafByteOff + pos2), hash);
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(leafByteOff + pos2 + 4), address);
      pos2 += 8;
    }
    var tailOff = leafByteOff + geo.DirBlockSize - 4;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(tailOff), (uint)dataDirBlocks);
    for (var i = 0; i < dataDirBlocks; i++) {
      var bestOff = tailOff - (dataDirBlocks - i) * 2;
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(bestOff), (ushort)blockBestFree[i]);
    }
    crc.Add(leafByteOff, Dir3LeafCrcOffset, geo.DirBlockSize);
  }

  private static void WriteDirInodeExtents(byte[] image, int ioff,
      IReadOnlyList<(long LogicalFsBlock, ulong PhysFsBlock, int FsBlockCount)> extents,
      long byteSize, int nblocks) {
    image[ioff + 5] = 2; // di_format = extents
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 56), (ulong)byteSize);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 64), (ulong)nblocks);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(ioff + 76), (uint)extents.Count);
    var extPos = ioff + ForkOffset;
    // Clear the literal area before writing extents (may have held short-form).
    image.AsSpan(ioff + ForkOffset, 256 - ForkOffset).Clear();
    foreach (var (logical, phys, count) in extents) {
      var hi = (((ulong)logical & 0x3FFFFFFFFFFFFFUL) << 9) | ((phys >> 43) & 0x1FF);
      var lo = (phys << 21) | ((ulong)count & 0x1FFFFF);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(extPos), hi);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(extPos + 8), lo);
      extPos += 16;
    }
  }

  // ════════════════════════════════════════════════════════════════════════════
  //  CREATE A SUB-DIRECTORY IN PLACE  (short-form, empty)
  // ════════════════════════════════════════════════════════════════════════════

  private static ulong CreateChildDirectory(byte[] image, Geo geo, ulong parentIno, string name, CrcSet crc) {
    var newIno = AllocateInode(image, geo, crc);
    var ioff = (int)InodeByteOffset(geo, newIno);
    var di = image.AsSpan(ioff, geo.InodeSize);
    di.Clear();
    BinaryPrimitives.WriteUInt16BigEndian(di[0..], InodeMagic);
    BinaryPrimitives.WriteUInt16BigEndian(di[2..], 0x41ED);            // S_IFDIR | 0755
    di[4] = 3;
    di[5] = 1;                                                         // local short-form
    BinaryPrimitives.WriteUInt32BigEndian(di[16..], 2);               // nlink: self + parent ref
    di[83] = 2;                                                       // di_aformat
    BinaryPrimitives.WriteUInt32BigEndian(di[96..], 0xFFFFFFFFu);
    BinaryPrimitives.WriteUInt64BigEndian(di[152..], newIno);
    SbUuid(image).CopyTo(di[160..]);

    // Empty short-form dir header: count=0, i8count=0, parent[4].
    var dirOff = ioff + ForkOffset;
    image[dirOff] = 0;
    image[dirOff + 1] = 0;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(dirOff + 2), (uint)parentIno);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 56), 6); // di_size = sf hdr
    AddInodeCrc(crc, geo, newIno);

    // Insert into parent and bump parent nlink (new subdir's ".." references it).
    InsertDirEntry(image, geo, parentIno, name, newIno, isDir: true, crc);
    BumpParentNlink(image, geo, parentIno, +1, crc);
    return newIno;
  }

  private static void BumpParentNlink(byte[] image, Geo geo, ulong dirIno, int delta, CrcSet crc) {
    var ioff = (int)InodeByteOffset(geo, dirIno);
    var nlink = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(ioff + 16));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(ioff + 16), (uint)(nlink + delta));
    AddInodeCrc(crc, geo, dirIno);
  }

}

/// <summary>
/// Collects every v5 metadata region whose CRC-32C must be backfilled at the end
/// of an in-place operation. Keyed by (block byte offset, CRC field offset) so
/// the per-inode CRCs of many inodes inside one fs block stay distinct.
/// </summary>
internal sealed class CrcSet {
  private readonly Dictionary<(int, int), int> _regions = []; // (byteOffset, crcOffset) -> length
  public void Add(int byteOffset, int crcOffset, int length) => this._regions[(byteOffset, crcOffset)] = length;
  public void Flush(byte[] image) {
    foreach (var ((off, crc), len) in this._regions)
      XfsWriter.BackfillCrc(image.AsSpan(off, len), crc);
  }
}
