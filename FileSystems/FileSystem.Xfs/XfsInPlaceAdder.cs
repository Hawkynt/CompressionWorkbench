#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Xfs;

/// <summary>
/// Genuine in-place add for XFS v5 images — the counterpart to the rebuild path
/// in <see cref="XfsModifier"/>. Inserts a small file into the <em>root</em>
/// directory by editing only the structures the change touches:
/// <list type="bullet">
///   <item>claims a free inode slot in the root inode chunk (flips its bit in
///         the inobt record's <c>ir_free</c> mask, decrements
///         <c>ir_freecount</c>, AGI <c>agi_freecount</c> and sb <c>ifree</c>);</item>
///   <item>allocates a data extent from the head of AG 0's single free extent
///         in the bnobt/cntbt leaves (shrinks the record, decrements
///         <c>agf_freeblks</c>/<c>agf_longest</c> and sb <c>fdblocks</c>);</item>
///   <item>writes the file bytes into the allocated blocks;</item>
///   <item>writes the new inode core + one BMBT data-fork extent;</item>
///   <item>appends a short-form directory entry to the root dir and bumps its
///         <c>di_size</c>;</item>
///   <item>recomputes CRC-32C on every touched v5 metadata block (new inode
///         block, root inode block, AGF, AGI, bnobt, cntbt, superblock).</item>
/// </list>
/// Every existing file's inode, BMBT extent and data blocks stay byte-identical
/// at their original offsets — no whole-image re-pack.
/// <para>
/// The implementation is geometry-locked to <see cref="XfsWriter"/>'s output
/// (4 KiB blocks, 256-byte v3 inodes, 2 AGs, single free extent at the tail of
/// AG 0, inobt with one chunk record covering the root chunk). Cases it cannot
/// satisfy throw <see cref="NotSupportedException"/> / <see cref="IOException"/>
/// so the caller falls back to the verified rebuild:
/// nested sub-directory targets; a root directory that is not short-form or
/// whose short-form area cannot hold the new entry; no free inode slot; or
/// insufficient contiguous free space in AG 0.
/// </para>
/// </summary>
public static class XfsInPlaceAdder {
  private const uint XfsMagic = 0x58465342;   // "XFSB"
  private const ushort InodeMagic = 0x494E;   // "IN"
  private const uint BnobtV5Magic = 0x41423342;  // "AB3B"
  private const uint CntbtV5Magic = 0x41423343;  // "AB3C"
  private const uint InobtV5Magic = 0x49414233;  // "IAB3"

  private const int SectorSize = 512;
  private const int InodesPerBlock = 16;       // 4096 / 256
  private const int InodesPerChunk = 64;
  private const int ForkOffset = 176;          // v3 dinode literal-area offset
  private const int DiCrcOffset = 100;

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
  /// Adds (or replaces by name) <paramref name="name"/> into the root directory
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
    if (norm.Contains('/'))
      throw new NotSupportedException("XFS in-place add: nested sub-directory targets use rebuild.");

    var geo = ParseSuperblock(image);
    if (geo.BlockSize != 4096 || geo.InodeSize != 256)
      throw new NotSupportedException("XFS in-place add: only the writer's 4 KiB/256 B geometry is supported.");

    // Replace-by-name is not handled in place (removing an existing entry would
    // need to free its inode/extent and rebalance) — let the caller rebuild.
    if (RootHasEntry(image, geo, norm))
      throw new NotSupportedException("XFS in-place add: replace-by-name uses rebuild.");

    // ── 1. Claim a free inode slot in the root inode chunk ──
    var slot = AllocateInodeSlot(image, geo, out var newIno);

    // ── 2. Allocate a data extent from AG 0's free space (head of the extent) ──
    var blocksNeeded = data.Length == 0 ? 1 : (int)((data.Length + geo.BlockSize - 1) / geo.BlockSize);
    var dataStartBlock = AllocateExtent(image, geo, blocksNeeded);

    // ── 3. Write the file bytes ──
    var dataByteOffset = (long)dataStartBlock * geo.BlockSize;
    if (dataByteOffset + (long)blocksNeeded * geo.BlockSize > image.Length)
      throw new IOException("XFS in-place add: allocated extent out of image bounds.");
    // Zero the whole extent first (covers the cluster tail past the data), then copy.
    image.AsSpan((int)dataByteOffset, blocksNeeded * geo.BlockSize).Clear();
    data.CopyTo(image, (int)dataByteOffset);

    // ── 4. Write the new inode core + BMBT data-fork extent ──
    var inodeOff = (int)InodeByteOffset(geo, slot);
    WriteRegularFileInode(image, inodeOff, newIno, data.Length, dataStartBlock, blocksNeeded, geo);

    // ── 5. Append the short-form directory entry to the root dir ──
    InsertRootShortFormEntry(image, geo, norm, newIno);

    // ── 6. Recompute CRC-32C on every touched v5 metadata block ──
    // New inode's block, root inode's block, AGF, AGI, bnobt, cntbt, superblock.
    BackfillInodeCrc(image, geo, slot);
    BackfillInodeCrc(image, geo, RootSlot(geo));

    XfsWriter.BackfillCrc(image.AsSpan(0, SectorSize), SbCrcOffset);
    XfsWriter.BackfillCrc(image.AsSpan(AgfSector * SectorSize, SectorSize), AgfCrcOffset);
    XfsWriter.BackfillCrc(image.AsSpan(AgiSector * SectorSize, SectorSize), AgiCrcOffset);
    XfsWriter.BackfillCrc(image.AsSpan(BnobtBlock * geo.BlockSize, geo.BlockSize), BtreeCrcOffset);
    XfsWriter.BackfillCrc(image.AsSpan(CntbtBlock * geo.BlockSize, geo.BlockSize), BtreeCrcOffset);
    XfsWriter.BackfillCrc(image.AsSpan(InobtBlock * geo.BlockSize, geo.BlockSize), BtreeCrcOffset);
  }

  // ── Geometry ──────────────────────────────────────────────────────────────

  private readonly record struct Geo(
      int BlockSize, int InodeSize, ulong RootIno, uint AgBlocks, uint AgCount,
      byte AgBlkLog, ushort Version, uint FeaturesIncompat) {
    public bool HasFtype => (this.FeaturesIncompat & 0x1) != 0;
    public int InoPbLog {
      get { var l = 0; for (var v = this.BlockSize / this.InodeSize; v > 1; v >>= 1) l++; return l; }
    }
    public int AgInoLog => this.AgBlkLog + this.InoPbLog;
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
    uint featuresIncompat = 0;
    if ((version & 0xF) >= 5)
      featuresIncompat = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(216));
    if ((version & 0xF) < 5)
      throw new NotSupportedException("XFS in-place add: only v5 (CRC) images are supported.");
    return new Geo(blockSize, inodeSize, rootIno, agBlocks, agCount, agBlkLog, version, featuresIncompat);
  }

  // The writer's root inode chunk starts exactly at rootino, so the root
  // directory occupies slot 0 of the chunk (and slot 0 of the inode-offset map).
  private static int RootSlot(Geo geo) => 0;

  private static long InodeByteOffset(Geo geo, int slot) {
    // Slot index within AG 0's root inode chunk; chunk starts at rootino's agbno.
    var rootAgIno = geo.RootIno & ((1UL << geo.AgInoLog) - 1);
    var agIno = rootAgIno + (ulong)slot;
    var block = agIno / (ulong)InodesPerBlock;
    var offset = agIno % (ulong)InodesPerBlock;
    return (long)(block * (ulong)geo.BlockSize + offset * (ulong)geo.InodeSize);
  }

  // ── Inode-slot allocation (inobt + AGI + sb) ────────────────────────────────

  // Picks the lowest free slot in the root inode chunk, flips its bit, and
  // decrements every free-inode counter. Returns the slot index and absolute ino.
  private static int AllocateInodeSlot(byte[] image, Geo geo, out ulong newIno) {
    var inobtOff = InobtBlock * geo.BlockSize;
    if (BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(inobtOff)) != InobtV5Magic)
      throw new NotSupportedException("XFS in-place add: unexpected inobt magic.");
    var numrecs = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(inobtOff + 6));
    if (numrecs == 0)
      throw new IOException("XFS in-place add: empty inobt.");

    // Find the first chunk record with a free slot.
    for (var c = 0; c < numrecs; c++) {
      var rec = inobtOff + BtreeRecOffset + c * 16;
      var startIno = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(rec));
      var freeCount = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(rec + 4));
      var freeMask = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(rec + 8));
      if (freeCount == 0 || freeMask == 0) continue;

      // Lowest set bit = lowest free slot within this chunk.
      var bit = System.Numerics.BitOperations.TrailingZeroCount(freeMask);
      var chunkStartAgIno = startIno & ((1u << geo.AgInoLog) - 1);
      var rootAgIno = (uint)(geo.RootIno & ((1UL << geo.AgInoLog) - 1));
      var slot = (int)(chunkStartAgIno - rootAgIno) + bit;

      // Clear the bit, decrement freecount in the inobt record.
      freeMask &= ~(1UL << bit);
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(rec + 4), freeCount - 1);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(rec + 8), freeMask);

      // Decrement AGI agi_freecount.
      var agiOff = AgiSector * SectorSize;
      var agiFree = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(agiOff + 28));
      if (agiFree == 0) throw new IOException("XFS in-place add: AGI freecount underflow.");
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(agiOff + 28), agiFree - 1);

      // Decrement sb_ifree.
      var ifree = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(136));
      if (ifree == 0) throw new IOException("XFS in-place add: sb_ifree underflow.");
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(136), ifree - 1);

      // inobt block CRC is recomputed by the caller.
      newIno = geo.RootIno + (ulong)slot;
      return slot;
    }
    throw new IOException("XFS in-place add: no free inode slot in AG 0 (chunk growth uses rebuild).");
  }

  // ── Data-extent allocation (bnobt + cntbt + AGF + sb) ───────────────────────

  // Takes `blocks` blocks from the HEAD of AG 0's single free extent so the tail
  // (where future allocations and the existing free record live) shifts up. Both
  // the bnobt and cntbt single leaf records describe the same extent, so both are
  // updated identically. Returns the starting agbno (== fsbno in AG 0).
  private static int AllocateExtent(byte[] image, Geo geo, int blocks) {
    var bnobtOff = BnobtBlock * geo.BlockSize;
    var cntbtOff = CntbtBlock * geo.BlockSize;
    if (BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(bnobtOff)) != BnobtV5Magic ||
        BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(cntbtOff)) != CntbtV5Magic)
      throw new NotSupportedException("XFS in-place add: unexpected free-space btree magic.");

    var bnoNumrecs = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(bnobtOff + 6));
    var cntNumrecs = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(cntbtOff + 6));
    // The writer always emits exactly one free record per AG. A multi-record free
    // map (after fragmentation) would need full btree rebalancing — rebuild.
    if (bnoNumrecs != 1 || cntNumrecs != 1)
      throw new NotSupportedException("XFS in-place add: AG 0 free space is fragmented (multi-record btree) — uses rebuild.");

    var bnoRec = bnobtOff + BtreeRecOffset;
    var freeStart = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(bnoRec));
    var freeLen = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(bnoRec + 4));
    if (freeLen < blocks)
      throw new IOException($"XFS in-place add: only {freeLen} free blocks in AG 0, need {blocks}.");

    var newStart = freeStart + (uint)blocks;
    var newLen = freeLen - (uint)blocks;

    // Update bnobt record (keyed by startblock).
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(bnoRec), newStart);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(bnoRec + 4), newLen);
    // Update cntbt record (keyed by blockcount, then startblock — single record so order is fine).
    var cntRec = cntbtOff + BtreeRecOffset;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(cntRec), newStart);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(cntRec + 4), newLen);

    // Update AGF: agf_freeblks (52), agf_longest (56).
    var agfOff = AgfSector * SectorSize;
    var agfFree = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(agfOff + 52));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(agfOff + 52), agfFree - (uint)blocks);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(agfOff + 56), newLen); // longest == the only extent

    // Update sb_fdblocks (144).
    var fdblocks = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(144));
    if (fdblocks < (ulong)blocks) throw new IOException("XFS in-place add: sb_fdblocks underflow.");
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(144), fdblocks - (ulong)blocks);

    return (int)freeStart;
  }

  // ── Inode core + BMBT extent ────────────────────────────────────────────────

  private static void WriteRegularFileInode(byte[] image, int ioff, ulong ino, long size,
      int startBlock, int blockCount, Geo geo) {
    var di = image.AsSpan(ioff, geo.InodeSize);
    di.Clear();
    BinaryPrimitives.WriteUInt16BigEndian(di[0..], InodeMagic);
    BinaryPrimitives.WriteUInt16BigEndian(di[2..], 0x81A4);            // S_IFREG | 0644
    di[4] = 3;                                                         // di_version = v3
    di[5] = 2;                                                         // di_format = extents
    BinaryPrimitives.WriteUInt32BigEndian(di[16..], 1);               // di_nlink
    BinaryPrimitives.WriteUInt64BigEndian(di[56..], (ulong)size);     // di_size
    BinaryPrimitives.WriteUInt64BigEndian(di[64..], (ulong)blockCount); // di_nblocks
    BinaryPrimitives.WriteUInt32BigEndian(di[76..], 1);               // di_nextents
    di[83] = 2;                                                       // di_aformat = extents
    BinaryPrimitives.WriteUInt32BigEndian(di[96..], 0xFFFFFFFFu);     // di_next_unlinked = NULLAGINO
    BinaryPrimitives.WriteUInt64BigEndian(di[152..], ino);           // di_ino
    SbUuid(image).CopyTo(di[160..]);                                  // di_uuid (matches sb_uuid)

    // BMBT_REC at the fork offset: startoff=0, startblock, blockcount, flag=0.
    var startBlk = (ulong)startBlock;
    var cnt = (ulong)blockCount;
    var hi = (startBlk >> 43) & 0x1FF;          // startoff(0)<<9 | high 9 bits of startblock
    var lo = (startBlk << 21) | (cnt & 0x1FFFFF);
    BinaryPrimitives.WriteUInt64BigEndian(di[ForkOffset..], hi);
    BinaryPrimitives.WriteUInt64BigEndian(di[(ForkOffset + 8)..], lo);
  }

  // ── Root short-form directory entry insertion ───────────────────────────────

  private static void InsertRootShortFormEntry(byte[] image, Geo geo, string name, ulong childIno) {
    var rootOff = (int)InodeByteOffset(geo, RootSlot(geo));
    if (BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(rootOff)) != InodeMagic)
      throw new InvalidDataException("XFS in-place add: root inode magic mismatch.");
    var mode = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(rootOff + 2));
    if ((mode & 0xF000) != 0x4000)
      throw new InvalidDataException("XFS in-place add: root inode is not a directory.");
    var format = image[rootOff + 5];
    if (format != 1)
      throw new NotSupportedException("XFS in-place add: root directory is not short-form (out-of-line) — uses rebuild.");

    var dirOff = rootOff + ForkOffset;
    var count = image[dirOff];
    var i8count = image[dirOff + 1];
    if (i8count != 0)
      throw new NotSupportedException("XFS in-place add: 8-byte-inode short-form dir — uses rebuild.");

    // childIno must fit in 32 bits to stay i8count==0 (the writer's invariant).
    if (childIno > 0xFFFFFFFFUL)
      throw new NotSupportedException("XFS in-place add: child inode exceeds 32 bits — uses rebuild.");

    var nameBytes = Encoding.UTF8.GetBytes(name);
    var nameLen = nameBytes.Length;
    if (nameLen == 0 || nameLen > 250)
      throw new NotSupportedException("XFS in-place add: name length out of range.");
    var ftypeLen = geo.HasFtype ? 1 : 0;

    // Walk existing entries to find the end of the entry list and the next
    // sf_offset to assign (entries are laid out by ascending offset; the next
    // offset mimics the dir2 data-block entry size, matching the writer).
    var pos = dirOff + 6; // hdr: count(1)+i8count(1)+parent(4)
    ushort nextOffset = 0x60;
    for (var i = 0; i < count; i++) {
      var entNameLen = image[pos];
      var entOffset = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(pos + 1));
      // dir2 data-block entry footprint = ino(8)+namelen(1)+name+ftype+tag(2), 8-aligned.
      var entStep = (entNameLen + 12 + 7) & ~7;
      nextOffset = (ushort)(entOffset + entStep);
      pos += 3 + entNameLen + ftypeLen + 4; // namelen(1)+offset(2)+name+ftype+ino(4)
    }

    // New entry footprint inside the inode literal area.
    var newEntryBytes = 3 + nameLen + ftypeLen + 4;
    var newDirSize = (pos - dirOff) + newEntryBytes;
    if (ForkOffset + newDirSize > geo.InodeSize)
      throw new NotSupportedException("XFS in-place add: short-form directory area is full — uses rebuild.");

    // Append the entry: namelen(1), offset[2], name[namelen], ftype(1), ino[4].
    image[pos] = (byte)nameLen;
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(pos + 1), nextOffset);
    nameBytes.CopyTo(image, pos + 3);
    if (geo.HasFtype) image[pos + 3 + nameLen] = 1; // DT_REG
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(pos + 3 + nameLen + ftypeLen), (uint)childIno);

    // Bump count and di_size; di_nblocks stays 0 for short-form.
    image[dirOff] = (byte)(count + 1);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(rootOff + 56), (ulong)newDirSize);
  }

  // ── Helpers ──────────────────────────────────────────────────────────────────

  private static bool RootHasEntry(byte[] image, Geo geo, string name) {
    var rootOff = (int)InodeByteOffset(geo, RootSlot(geo));
    if (BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(rootOff)) != InodeMagic) return false;
    if (image[rootOff + 5] != 1) return false; // only short-form scanned (caller rejects others)
    var dirOff = rootOff + ForkOffset;
    var count = image[dirOff];
    var i8count = image[dirOff + 1];
    if (i8count != 0) return false;
    var ftypeLen = geo.HasFtype ? 1 : 0;
    var pos = dirOff + 6;
    for (var i = 0; i < count; i++) {
      var entNameLen = image[pos];
      var entName = Encoding.UTF8.GetString(image, pos + 3, entNameLen);
      if (string.Equals(entName, name, StringComparison.Ordinal)) return true;
      pos += 3 + entNameLen + ftypeLen + 4;
    }
    return false;
  }

  private static byte[] SbUuid(byte[] image) => image.AsSpan(32, 16).ToArray();

  private static void BackfillInodeCrc(byte[] image, Geo geo, int slot) {
    var ioff = (int)InodeByteOffset(geo, slot);
    XfsWriter.BackfillCrc(image.AsSpan(ioff, geo.InodeSize), DiCrcOffset);
  }
}
