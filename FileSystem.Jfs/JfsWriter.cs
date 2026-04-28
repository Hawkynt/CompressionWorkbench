#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Jfs;

/// <summary>
/// Writes a minimal IBM Journaled File System (JFS1) aggregate image with a single
/// allocation group, one fileset, and an inline dtree root directory.
/// <para>
/// Byte layout matches the on-disk structures in <c>linux/fs/jfs</c> and the
/// <c>jfsutils</c> reference (mkfs.jfs / fsck.jfs); validated by exit-zero from
/// <c>fsck.jfs -n -f -v</c>. All integer fields are little-endian. <c>pxd_t</c>
/// is packed as <c>len_addr = (len &amp; 0xFFFFFF) | ((addr &gt;&gt; 32) &lt;&lt; 24)</c>,
/// <c>addr2 = addr &amp; 0xFFFFFFFF</c>. Dtree slot names are UCS-2 (UTF-16 LE).
/// Round-trips through <see cref="JfsReader"/>.
/// </para>
/// <para>
/// Aggregate inode table (block 11..14, IXSIZE=16 KB) holds the AGGR_RESERVED_I (0),
/// AGGREGATE_I (1, → AIM), BMAP_I (2, → block-allocation map), LOG_I (3), BADBLOCK_I (4)
/// and FILESYSTEM_I (16, → fileset AIM) metadata inodes. The fileset inode table at
/// blocks 29..32 holds FILESET_RSVD_I (0), FILESET_EXT_I (1), ROOT_I (2, dtroot inline),
/// ACL_I (3) and user file inodes (4+).
/// </para>
/// </summary>
public sealed class JfsWriter {
  // ── spec constants ────────────────────────────────────────────────────────
  internal const int SuperblockOffset = 0x8000;   // 64 × 512 = 32768
  internal const int BlockSize = 4096;
  internal const int SectorSize = 512;
  internal const int L2BSize = 12;                // log2(4096)
  internal const int L2PBSize = 9;                // log2(512)
  internal const int L2BFactor = 3;               // 4096 / 512 = 8 = 2^3
  internal const uint JfsMagic = 0x3153464A;      // "JFS1" little-endian
  internal const uint JfsVersion = 2;
  internal const int InodeSize = 512;             // DISIZE = 512
  internal const int InodesPerExtent = 32;        // INOSPEREXT = 32 → IXSIZE = 16384
  internal const int InodeExtentSize = 16384;     // 4 blocks @ 4 KB
  internal const int InodeExtentBlocks = 4;       // IXSIZE / BlockSize
  internal const int FilesetIno = 16;             // FILESYSTEM_I in aggregate inode table
  internal const int RootIno = 2;                 // ROOT_I in fileset inode table
  internal const int AclIno = 3;                  // ACL_I in fileset inode table
  internal const int FirstFileIno = 4;            // user files start here (after ACL_I)
  internal const int XtreeDataOffset = 224;       // di_data/dtroot offset inside 512-byte dinode
  internal const int DiDataSize = 288;            // size of _dtroot / _xtroot union (512 - 224)
  internal const int InostampFixed = unchecked((int)0x87878787);
  internal const int MaxFilesInRoot = 8;          // inline dtree has 9 slots (1 header + 8 entries)

  // ── inode numbers (jfs_filsys.h) ─────────────────────────────────────────
  private const int AggrReservedI = 0;
  private const int AggregateI = 1;               // aggregate inode map inode
  private const int BmapI = 2;                    // aggregate block allocation map inode
  private const int LogI = 3;                     // aggregate inline log inode
  private const int BadblockI = 4;                // aggregate bad block inode
  private const int FilesetExtI = 1;              // fileset inode extension (in fileset inode table)

  // ── di_mode bits (jfs_dinode.h) ──────────────────────────────────────────
  private const uint IfReg = 0x8000;
  private const uint IfDir = 0x4000;
  private const uint IfJournal = 0x00010000;
  private const uint ISparse = 0x00020000;

  // ── btree flags (jfs_btree.h, jfs_types.h) ──────────────────────────────
  private const byte BtRoot = 0x01;
  private const byte BtLeaf = 0x02;
  private const byte DxdIndex = 0x80;
  // For B+-tree root pages on-disk: flag = DXD_INDEX | BT_ROOT | BT_LEAF = 0x83
  private const byte BtRootLeafFlag = DxdIndex | BtRoot | BtLeaf;

  // ── dmap constants (jfs_dmap.h) ──────────────────────────────────────────
  private const int Dmap_Treesize = 256 + 64 + 16 + 4 + 1;   // 341
  private const int Dmap_Leafind = 64 + 16 + 4 + 1;          // 85
  private const int Dmap_Lperdmap = 256;
  private const int Dmap_L2lperdmap = 8;
  private const int Dmap_Bperdmap = 8192;                     // BPERDMAP
  private const int Dmap_L2bperdmap = 13;
  private const int Dmap_Budmin = 5;                          // L2DBWORD
  private const int Dmapctl_Treesize = 1024 + 256 + 64 + 16 + 4 + 1;   // 1365
  private const int Dmapctl_Leafind = 256 + 64 + 16 + 4 + 1;           // 341
  private const int Dmapctl_Lperctl = 1024;
  private const int Dmapctl_L2lperctl = 10;
  private const sbyte Dmap_Nofree = -1;

  // ── imap constants (jfs_imap.h) ──────────────────────────────────────────
  private const int Iag_Smapsz = 4;
  private const int Iag_Extsperiag = 128;

  // ── layout (block addresses) — fixed at 4 KB block size ──────────────────
  // jfs_filsys.h fixes these at: SUPER1_OFF=0x8000 (block 8), AIMAP_OFF=0x9000
  // (block 9), AITBL_OFF=0xB000 (block 11), SUPER2_OFF=0xF000 (block 15),
  // BMAP_OFF=0x10000 (block 16). Following blocks are arranged to leave room
  // for the level-0 dmapctl + dmaps before the secondary AIM/AIT and fileset
  // structures.
  private const int Super1Block = 8;              // primary superblock (fixed)
  private const int AimBlock = 9;                 // primary AIM (2 blocks: dinomap + IAG)
  private const int AitBlock = 11;                // primary AIT (4 blocks)
  private const int Super2Block = 15;             // secondary superblock (fixed)
  private const int BmapBlock = 16;               // dbmap control_page (fixed)
  // For ≤ 8 M blocks (level 0): next_page = BMAP_OFF + PSIZE + PSIZE*(2-0) = 16+1+2 = 19.
  // L0 dmapctl at block 19, dmaps follow at 20, 21, ... one per Dmap_Bperdmap blocks.
  private const int L0DmapctlBlock = 19;
  private const int FirstDmapBlock = 20;

  // The remaining structures float after the BMAP. We pick fixed positions for
  // small images (the only case this writer targets) so the layout is deterministic.
  // For one dmap (≤ 8192 usable blocks), BMAP occupies blocks 16..20 (5 blocks).
  // For two dmaps (≤ 16384 usable blocks), BMAP occupies blocks 16..21 (6 blocks).
  // We always reserve up to 2 dmaps to keep the layout simple.
  private const int BmapTotalBlocks = 6;          // 16..21 inclusive (control + 2 unused + dmapctl + 2 dmaps)
  private const int SecondaryAimBlock = 22;       // 2 blocks
  private const int SecondaryAitBlock = 24;       // 4 blocks
  private const int FilesetAimBlock = 28;         // 2 blocks (dinomap + fileset IAG)
  private const int FsitBlock = 30;               // fileset inode table (4 blocks)
  private const int DataStartBlock = 34;          // user file data starts here
  private const int MinUsableBlocks = 4096;       // 16 MB minimum (kernel hard floor)

  private readonly List<(string Name, byte[] Data)> _files = [];
  private readonly byte[] _volumeUuid = Guid.NewGuid().ToByteArray();
  private readonly byte[] _logUuid = Guid.NewGuid().ToByteArray();
  private uint _writeTimestamp;                                                           // captured at WriteTo() start so primary/secondary copies match byte-for-byte

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (this._files.Count >= MaxFilesInRoot)
      throw new InvalidOperationException($"JfsWriter supports at most {MaxFilesInRoot} files in the inline root dtree.");
    var leaf = Path.GetFileName(name);
    this._files.Add((leaf, data));
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    this._writeTimestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ── allocate file data blocks ─────────────────────────────────────────
    var fileDataBlocks = new int[this._files.Count];
    var fileBlockCounts = new int[this._files.Count];
    var nextBlock = DataStartBlock;
    for (var i = 0; i < this._files.Count; i++) {
      fileDataBlocks[i] = nextBlock;
      fileBlockCounts[i] = Math.Max(1, (this._files[i].Data.Length + BlockSize - 1) / BlockSize);
      nextBlock += fileBlockCounts[i];
    }

    // ── image sizing ──────────────────────────────────────────────────────
    // Image must hold all data + fsck workspace + inline log past the visible
    // filesystem boundary. fsck computes fsck_blkmap_size = ceil(agg/BITSPERPAGE)+1+50
    // pages = 52 pages for small images. mkfs.jfs default inline log = 256 blocks.
    const int FsckWspBlocks = 52;
    const int InlineLogBlocks = 256;
    var usableBlocks = Math.Max(MinUsableBlocks, nextBlock + 4);
    var totalBlocks = usableBlocks + FsckWspBlocks + InlineLogBlocks;
    var image = new byte[(long)totalBlocks * BlockSize];

    WriteSuperblock(image, usableBlocks, FsckWspBlocks, InlineLogBlocks);

    // Track allocated blocks for the dmap pmap/wmap. All metadata + file data is
    // marked allocated; the rest of the usable region is free.
    var allocated = new bool[usableBlocks];
    MarkRange(allocated, 0, Super1Block + 1);                      // boot sectors + super1
    MarkRange(allocated, AimBlock, 2);                             // AIM (dinomap + IAG)
    MarkRange(allocated, AitBlock, InodeExtentBlocks);             // primary AIT (4 blocks)
    MarkRange(allocated, Super2Block, 1);                          // super2
    MarkRange(allocated, BmapBlock, BmapTotalBlocks);              // dbmap + L0 dmapctl + dmaps
    MarkRange(allocated, SecondaryAimBlock, 2);                    // secondary AIM
    MarkRange(allocated, SecondaryAitBlock, InodeExtentBlocks);    // secondary AIT
    MarkRange(allocated, FilesetAimBlock, 2);                      // fileset AIM
    MarkRange(allocated, FsitBlock, InodeExtentBlocks);            // fileset inode table
    for (var i = 0; i < this._files.Count; i++)
      MarkRange(allocated, fileDataBlocks[i], fileBlockCounts[i]);

    // ── write metadata structures ─────────────────────────────────────────
    // Both primary and secondary AIM/AIT must be byte-for-byte identical (fsck
    // verifies them as redundant copies). All di_ixpxd fields point at the
    // PRIMARY AIT (AitBlock) regardless of where the inodes physically live.
    WriteAggregateInodeMap(image, AimBlock, agStart: 0);
    this.WriteAggregateInodeTable(image, AitBlock, ixpxdBlock: AitBlock);
    WriteAggregateInodeMap(image, SecondaryAimBlock, agStart: 0);
    this.WriteAggregateInodeTable(image, SecondaryAitBlock, ixpxdBlock: AitBlock);
    WriteFilesetInodeMap(image, FilesetAimBlock, fileCount: this._files.Count);
    WriteFilesetInodeTable(image, fileDataBlocks, fileBlockCounts);
    WriteBlockMap(image, usableBlocks, allocated);

    // File data
    for (var i = 0; i < this._files.Count; i++) {
      var data = this._files[i].Data;
      if (data.Length > 0)
        data.CopyTo(image, (long)fileDataBlocks[i] * BlockSize);
    }

    output.Write(image);
  }

  private static void MarkRange(bool[] map, int start, int count) {
    var end = Math.Min(map.Length, start + count);
    for (var b = start; b < end; b++) map[b] = true;
  }

  // ── superblock (jfs_superblock, le) ──────────────────────────────────────
  private void WriteSuperblock(byte[] image, int usableBlocks, int fsckWspBlocks, int logBlocks) {
    WriteSuperblockAt(image, SuperblockOffset, usableBlocks, fsckWspBlocks, logBlocks);
    var super2Offset = Super2Block * BlockSize;
    WriteSuperblockAt(image, super2Offset, usableBlocks, fsckWspBlocks, logBlocks);
  }

  private void WriteSuperblockAt(byte[] image, int offset, int usableBlocks, int fsckWspBlocks, int logBlocks) {
    var sb = image.AsSpan(offset);
    "JFS1"u8.CopyTo(sb);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[4..], 1);                                  // s_version
    BinaryPrimitives.WriteUInt64LittleEndian(sb[8..], (ulong)usableBlocks * (BlockSize / SectorSize)); // s_size in HW blocks
    BinaryPrimitives.WriteUInt32LittleEndian(sb[16..], BlockSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sb[20..], L2BSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sb[22..], L2BFactor);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[24..], SectorSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sb[28..], L2PBSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sb[30..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[32..], (uint)Dmap_Bperdmap);              // s_agsize = 8192 (1<<L2BPERDMAP)
    const uint JfsLinux = 0x10000000u;
    const uint JfsGroupCommit = 0x00000100u;
    const uint JfsInlineLog = 0x00000800u;
    BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], JfsLinux | JfsGroupCommit | JfsInlineLog);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[40..], 0);                                // s_state = FM_CLEAN
    BinaryPrimitives.WriteUInt32LittleEndian(sb[44..], 0);                                // s_compress
    WritePxd(sb[48..], length: (uint)InodeExtentBlocks, address: SecondaryAitBlock);      // s_ait2
    WritePxd(sb[56..], length: 2, address: SecondaryAimBlock);                            // s_aim2
    BinaryPrimitives.WriteUInt32LittleEndian(sb[64..], 0);                                // s_logdev (inline)
    BinaryPrimitives.WriteUInt32LittleEndian(sb[68..], 1);                                // s_logserial
    var fsckAddr = (ulong)usableBlocks;
    var logAddr = fsckAddr + (ulong)fsckWspBlocks;
    WritePxd(sb[72..], length: (uint)logBlocks, address: logAddr);                        // s_logpxd
    WritePxd(sb[80..], length: (uint)fsckWspBlocks, address: fsckAddr);                   // s_fsckpxd
    BinaryPrimitives.WriteUInt32LittleEndian(sb[88..], this._writeTimestamp);             // s_time.tv_sec
    BinaryPrimitives.WriteUInt32LittleEndian(sb[92..], 0);                                // s_time.tv_nsec
    BinaryPrimitives.WriteUInt32LittleEndian(sb[96..], 50);                               // s_fsckloglen
    sb[100] = 0;                                                                          // s_fscklog
    var pack = Encoding.ASCII.GetBytes("JFS-WORKBENCH\0");
    pack.AsSpan(0, Math.Min(pack.Length, 11)).CopyTo(sb[101..]);
    BinaryPrimitives.WriteUInt64LittleEndian(sb[112..], (ulong)usableBlocks * (BlockSize / SectorSize)); // s_xsize
    WritePxd(sb[120..], length: (uint)fsckWspBlocks, address: fsckAddr);                  // s_xfsckpxd
    WritePxd(sb[128..], length: (uint)logBlocks, address: logAddr);                       // s_xlogpxd
    this._volumeUuid.CopyTo(sb[136..]);                                                   // s_uuid[16]
    var label = Encoding.ASCII.GetBytes("JFS Workbench\0\0\0");
    label.AsSpan(0, Math.Min(label.Length, 16)).CopyTo(sb[152..]);
    this._logUuid.CopyTo(sb[168..]);                                                      // s_loguuid[16]
  }

  // ── aggregate inode map: block AimBlock=dinomap, AimBlock+1=IAG #0 ──────
  // Aggregate AIM has 5 metadata inodes initialized (0..4) plus FILESYSTEM_I (16).
  // wmap[0] = pmap[0] = bits for inodes 0..4 (high bits) + bit for inode 16
  //                   = 0xF8000000 | 0x00008000 = 0xF8008000
  // The bit ordering in JFS bitmaps is MSB=lowest inode, LSB=highest.
  private static void WriteAggregateInodeMap(byte[] image, int aimBlock, long agStart) {
    var dinomapOff = (long)aimBlock * BlockSize;
    var iagOff = dinomapOff + BlockSize;

    // ── dinomap (control page) ──────────────────────────────────────────
    var dm = image.AsSpan((int)dinomapOff, BlockSize);
    dm.Clear();
    BinaryPrimitives.WriteInt32LittleEndian(dm[0..], -1);            // in_freeiag
    BinaryPrimitives.WriteInt32LittleEndian(dm[4..], 1);             // in_nextiag
    BinaryPrimitives.WriteInt32LittleEndian(dm[8..], InodesPerExtent); // in_numinos
    BinaryPrimitives.WriteInt32LittleEndian(dm[12..], InodesPerExtent - 6); // in_numfree (6 = 5 aggr + FS)
    BinaryPrimitives.WriteInt32LittleEndian(dm[16..], InodeExtentBlocks); // in_nbperiext = 4
    BinaryPrimitives.WriteInt32LittleEndian(dm[20..], 2);            // in_l2nbperiext
    BinaryPrimitives.WriteInt32LittleEndian(dm[24..], 0);            // in_diskblock
    BinaryPrimitives.WriteInt32LittleEndian(dm[28..], 0);            // in_maxag
    // in_agctl[0] at offset 2048 (dmap pad section starts at 32, ends at 2047)
    // struct iagctl { int32 inofree; int32 extfree; int32 numinos; int32 numfree; }
    BinaryPrimitives.WriteInt32LittleEndian(dm[2048..], 0);          // inofree = iag 0
    BinaryPrimitives.WriteInt32LittleEndian(dm[2052..], 0);          // extfree = iag 0
    BinaryPrimitives.WriteInt32LittleEndian(dm[2056..], InodesPerExtent); // numinos
    BinaryPrimitives.WriteInt32LittleEndian(dm[2060..], InodesPerExtent - 6); // numfree
    // in_agctl[1..127] = (inofree=-1, extfree=-1, numinos=0, numfree=0) — leave zeroed except inofree/extfree=-1
    for (var i = 1; i < 128; i++) {
      var off = 2048 + i * 16;
      BinaryPrimitives.WriteInt32LittleEndian(dm[off..], -1);
      BinaryPrimitives.WriteInt32LittleEndian(dm[(off + 4)..], -1);
    }

    // ── IAG #0 ──────────────────────────────────────────────────────────
    var iag = image.AsSpan((int)iagOff, BlockSize);
    iag.Clear();
    BinaryPrimitives.WriteInt64LittleEndian(iag[0..], agStart);      // agstart
    BinaryPrimitives.WriteInt32LittleEndian(iag[8..], 0);            // iagnum
    BinaryPrimitives.WriteInt32LittleEndian(iag[12..], -1);          // inofreefwd
    BinaryPrimitives.WriteInt32LittleEndian(iag[16..], -1);          // inofreeback
    BinaryPrimitives.WriteInt32LittleEndian(iag[20..], -1);          // extfreefwd
    BinaryPrimitives.WriteInt32LittleEndian(iag[24..], -1);          // extfreeback
    BinaryPrimitives.WriteInt32LittleEndian(iag[28..], -1);          // iagfree
    // inosmap[0..3]: bit n = "extent n has all-allocated OR not backed".
    // Extent 0 is backed but not all-allocated → bit 31 (highest, MSB) = 0; extents 1..31 not backed → bits 30..0 = 1.
    // → inosmap[0] = 0x7FFFFFFF, inosmap[1..3] = 0xFFFFFFFF (extents 32..127 not backed).
    BinaryPrimitives.WriteUInt32LittleEndian(iag[32..], 0x7FFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[36..], 0xFFFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[40..], 0xFFFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[44..], 0xFFFFFFFFu);
    // extsmap[0..3]: bit n = "extent n is backed".
    // Only extent 0 backed → bit 31 of extsmap[0] = 1, all others = 0.
    BinaryPrimitives.WriteUInt32LittleEndian(iag[48..], 0x80000000u);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[52..], 0x00000000u);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[56..], 0x00000000u);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[60..], 0x00000000u);
    BinaryPrimitives.WriteInt32LittleEndian(iag[64..], InodesPerExtent - 6); // nfreeinos = 26
    BinaryPrimitives.WriteInt32LittleEndian(iag[68..], Iag_Extsperiag - 1);  // nfreeexts = 127
    // wmap[0] / pmap[0]: bits for inodes 0..4 (top 5) and 16 (bit 15 from the top).
    // MSB = inode 0, LSB = inode 31. inodes {0,1,2,3,4} → 0xF8000000; inode 16 → 0x00008000.
    const uint AggrUsedBitmap = 0xF8000000u | 0x00008000u;           // 0xF8008000
    BinaryPrimitives.WriteUInt32LittleEndian(iag[2048..], AggrUsedBitmap);   // wmap[0]
    BinaryPrimitives.WriteUInt32LittleEndian(iag[2560..], AggrUsedBitmap);   // pmap[0]
    // inoext[0]: pxd(len=4, addr=AitBlock). All other inoext entries are zero.
    WritePxd(iag[3072..], length: (uint)InodeExtentBlocks, address: (ulong)AitBlock);
  }

  // ── fileset inode map: block FilesetAimBlock=dinomap, +1=IAG #0 ─────────
  // Fileset AIM has FILESET_RSVD_I (0), FILESET_EXT_I (1), ROOT_I (2), ACL_I (3)
  // always allocated, plus user file inodes at index 4+.
  private static void WriteFilesetInodeMap(byte[] image, int aimBlock, int fileCount) {
    var dinomapOff = (long)aimBlock * BlockSize;
    var iagOff = dinomapOff + BlockSize;
    var inodesUsed = 4 + fileCount;                                   // 0,1,2,3 + files

    var dm = image.AsSpan((int)dinomapOff, BlockSize);
    dm.Clear();
    BinaryPrimitives.WriteInt32LittleEndian(dm[0..], -1);
    BinaryPrimitives.WriteInt32LittleEndian(dm[4..], 1);
    BinaryPrimitives.WriteInt32LittleEndian(dm[8..], InodesPerExtent);
    BinaryPrimitives.WriteInt32LittleEndian(dm[12..], InodesPerExtent - inodesUsed);
    BinaryPrimitives.WriteInt32LittleEndian(dm[16..], InodeExtentBlocks);
    BinaryPrimitives.WriteInt32LittleEndian(dm[20..], 2);
    BinaryPrimitives.WriteInt32LittleEndian(dm[24..], 0);
    BinaryPrimitives.WriteInt32LittleEndian(dm[28..], 0);
    BinaryPrimitives.WriteInt32LittleEndian(dm[2048..], 0);          // in_agctl[0].inofree
    BinaryPrimitives.WriteInt32LittleEndian(dm[2052..], 0);          // .extfree
    BinaryPrimitives.WriteInt32LittleEndian(dm[2056..], InodesPerExtent);
    BinaryPrimitives.WriteInt32LittleEndian(dm[2060..], InodesPerExtent - inodesUsed);
    for (var i = 1; i < 128; i++) {
      var off = 2048 + i * 16;
      BinaryPrimitives.WriteInt32LittleEndian(dm[off..], -1);
      BinaryPrimitives.WriteInt32LittleEndian(dm[(off + 4)..], -1);
    }

    var iag = image.AsSpan((int)iagOff, BlockSize);
    iag.Clear();
    BinaryPrimitives.WriteInt64LittleEndian(iag[0..], 0);
    BinaryPrimitives.WriteInt32LittleEndian(iag[8..], 0);
    BinaryPrimitives.WriteInt32LittleEndian(iag[12..], -1);
    BinaryPrimitives.WriteInt32LittleEndian(iag[16..], -1);
    BinaryPrimitives.WriteInt32LittleEndian(iag[20..], -1);
    BinaryPrimitives.WriteInt32LittleEndian(iag[24..], -1);
    BinaryPrimitives.WriteInt32LittleEndian(iag[28..], -1);
    // inosmap[0]: bit 31 = 0 (extent 0 has free); bits 30..0 = 1.
    BinaryPrimitives.WriteUInt32LittleEndian(iag[32..], 0x7FFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[36..], 0xFFFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[40..], 0xFFFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[44..], 0xFFFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[48..], 0x80000000u);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[52..], 0u);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[56..], 0u);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[60..], 0u);
    BinaryPrimitives.WriteInt32LittleEndian(iag[64..], InodesPerExtent - inodesUsed);
    BinaryPrimitives.WriteInt32LittleEndian(iag[68..], Iag_Extsperiag - 1);
    // wmap[0]/pmap[0]: top `inodesUsed` bits set (0xF0000000 base for 4 + 1 per file).
    var bitmap = 0u;
    for (var i = 0; i < inodesUsed && i < 32; i++) bitmap |= 0x80000000u >> i;
    BinaryPrimitives.WriteUInt32LittleEndian(iag[2048..], bitmap);   // wmap[0]
    BinaryPrimitives.WriteUInt32LittleEndian(iag[2560..], bitmap);   // pmap[0]
    WritePxd(iag[3072..], length: (uint)InodeExtentBlocks, address: (ulong)FsitBlock);
  }

  // ── aggregate inode table (4 blocks at aitBlock) ─────────────────────────
  // Holds: 0=AGGR_RESERVED_I, 1=AGGREGATE_I, 2=BMAP_I, 3=LOG_I, 4=BADBLOCK_I, 16=FILESYSTEM_I.
  // All have di_fileset = AGGREGATE_I (1) and di_ixpxd = (length=4, addr=ixpxdBlock).
  // <paramref name="ixpxdBlock"/> identifies the *canonical* AIT location (always
  // the primary AitBlock) so secondary AIT inodes' ixpxd matches the primary.
  private void WriteAggregateInodeTable(byte[] image, int aitBlock, int ixpxdBlock) {
    var aitOff = (long)aitBlock * BlockSize;

    // Inode 0: AGGR_RESERVED_I (di_nlink=1, IFJOURNAL|IFREG, no data)
    var ino0 = (int)(aitOff + (long)AggrReservedI * InodeSize);
    this.WriteAitInode(image, ino0, AggrReservedI, IfJournal | IfReg, ixpxdBlock,
      size: 0, nblocks: 0, hasXtreeData: false, xtreeEntries: null);

    // Inode 1: AGGREGATE_I — xtree → AIM (AimBlock, 2 blocks)
    var ino1 = (int)(aitOff + (long)AggregateI * InodeSize);
    this.WriteAitInode(image, ino1, AggregateI, IfJournal | IfReg, ixpxdBlock,
      size: 2L * BlockSize, nblocks: 2, hasXtreeData: true,
      xtreeEntries: [(0, 2u, (ulong)AimBlock)]);

    // Inode 2: BMAP_I — xtree → BMAP (BmapBlock, BmapTotalBlocks)
    var ino2 = (int)(aitOff + (long)BmapI * InodeSize);
    this.WriteAitInode(image, ino2, BmapI, IfJournal | IfReg, ixpxdBlock,
      size: (long)BmapTotalBlocks * BlockSize, nblocks: BmapTotalBlocks, hasXtreeData: true,
      xtreeEntries: [(0, (uint)BmapTotalBlocks, (ulong)BmapBlock)]);

    // Inode 3: LOG_I — no data (inline log inode placeholder)
    var ino3 = (int)(aitOff + (long)LogI * InodeSize);
    this.WriteAitInode(image, ino3, LogI, IfJournal | IfReg, ixpxdBlock,
      size: 0, nblocks: 0, hasXtreeData: true, xtreeEntries: []);

    // Inode 4: BADBLOCK_I — sparse, no data
    var ino4 = (int)(aitOff + (long)BadblockI * InodeSize);
    this.WriteAitInode(image, ino4, BadblockI, IfJournal | IfReg | ISparse, ixpxdBlock,
      size: 0, nblocks: 0, hasXtreeData: true, xtreeEntries: []);

    // Inode 16: FILESYSTEM_I — xtree → fileset AIM (FilesetAimBlock, 2 blocks)
    var ino16 = (int)(aitOff + (long)FilesetIno * InodeSize);
    this.WriteAitInode(image, ino16, FilesetIno, IfJournal | IfReg, ixpxdBlock,
      size: 2L * BlockSize, nblocks: 2, hasXtreeData: true,
      xtreeEntries: [(0, 2u, (ulong)FilesetAimBlock)], gengen: 1);
  }

  // ── fileset inode table: 4 blocks at FsitBlock ───────────────────────────
  // Holds: 0=FILESET_RSVD_I, 1=FILESET_EXT_I, 2=ROOT_I (dtroot inline),
  // 3=ACL_I, 4+=user file inodes (one xtree extent each).
  private void WriteFilesetInodeTable(byte[] image, int[] fileDataBlocks, int[] fileBlockCounts) {
    var fsitOff = (long)FsitBlock * BlockSize;

    // FILESET_RSVD_I (0)
    var ino0 = (int)(fsitOff + 0 * InodeSize);
    this.WriteFsitInode(image, ino0, ino: 0, fileset: FilesetIno,
      mode: IfJournal | IfReg, size: 0, nblocks: 0,
      hasXtreeData: true, xtreeEntries: []);

    // FILESET_EXT_I (1)
    var ino1 = (int)(fsitOff + 1 * InodeSize);
    this.WriteFsitInode(image, ino1, ino: FilesetExtI, fileset: FilesetIno,
      mode: IfJournal | IfReg, size: 0, nblocks: 0,
      hasXtreeData: true, xtreeEntries: []);

    // ROOT_I (2) — directory with inline dtroot.
    // di_size for an inline-rooted directory is IDATASIZE = 256 (sizeof(dinode)
    // - offsetof(di_inlinedata)); fsck enforces di_size <= IDATASIZE when
    // this_inode.data_size == 0 (no out-of-line blocks).
    const int IdataSize = 256;
    var ino2 = (int)(fsitOff + 2 * InodeSize);
    this.WriteFsitInode(image, ino2, ino: RootIno, fileset: FilesetIno,
      mode: IfJournal | IfDir | 0x1ED, // 0755
      size: IdataSize, nblocks: 0,
      hasXtreeData: false, xtreeEntries: null,
      nlink: 2, nextIndex: 2);
    WriteRootDtree(image.AsSpan(ino2 + XtreeDataOffset, DiDataSize));

    // ACL_I (3)
    var ino3 = (int)(fsitOff + 3 * InodeSize);
    this.WriteFsitInode(image, ino3, ino: AclIno, fileset: FilesetIno,
      mode: IfJournal | IfReg, size: 0, nblocks: 0,
      hasXtreeData: true, xtreeEntries: []);

    // User file inodes start at FirstFileIno = 4
    for (var i = 0; i < this._files.Count; i++) {
      var inoNum = FirstFileIno + i;
      var inoOff = (int)(fsitOff + (long)inoNum * InodeSize);
      var data = this._files[i].Data;
      this.WriteFsitInode(image, inoOff, ino: (uint)inoNum, fileset: FilesetIno,
        mode: IfJournal | IfReg | 0x1A4,                            // 0644
        size: data.Length, nblocks: fileBlockCounts[i],
        hasXtreeData: true,
        xtreeEntries: [(0, (uint)fileBlockCounts[i], (ulong)fileDataBlocks[i])]);
    }
  }

  // ── helpers: writing inodes ──────────────────────────────────────────────
  private void WriteAitInode(byte[] image, int ioff, uint ino, uint mode, int aitBlock,
      long size, long nblocks, bool hasXtreeData,
      (ulong offset, uint length, ulong address)[]? xtreeEntries,
      uint gengen = 0) {
    this.WriteCommonInodeHeader(image, ioff, fileset: AggregateI, ino: ino, mode: mode,
      ixpxdLength: (uint)InodeExtentBlocks, ixpxdAddress: (ulong)aitBlock,
      size: size, nblocks: nblocks, nlink: 1, nextIndex: 2);

    if (gengen != 0) {
      // di_gengen lives in di_data union at u._file._u1._imap._gengen, offset 128 + 4 = 132.
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ioff + 132), gengen);
    }

    if (hasXtreeData) {
      WriteXtreeRoot(image.AsSpan(ioff + XtreeDataOffset, DiDataSize), xtreeEntries ?? []);
    }
  }

  private void WriteFsitInode(byte[] image, int ioff, uint ino, int fileset, uint mode,
      long size, long nblocks, bool hasXtreeData,
      (ulong offset, uint length, ulong address)[]? xtreeEntries,
      uint nlink = 1, uint nextIndex = 2) {
    this.WriteCommonInodeHeader(image, ioff, fileset: fileset, ino: ino, mode: mode,
      ixpxdLength: (uint)InodeExtentBlocks, ixpxdAddress: (ulong)FsitBlock,
      size: size, nblocks: nblocks, nlink: nlink, nextIndex: nextIndex);

    if (hasXtreeData) {
      WriteXtreeRoot(image.AsSpan(ioff + XtreeDataOffset, DiDataSize), xtreeEntries ?? []);
    }
  }

  private void WriteCommonInodeHeader(byte[] image, int ioff, int fileset, uint ino,
      uint mode, uint ixpxdLength, ulong ixpxdAddress, long size, long nblocks,
      uint nlink, uint nextIndex) {
    var di = image.AsSpan(ioff, InodeSize);
    di.Clear();
    BinaryPrimitives.WriteInt32LittleEndian(di[0..], InostampFixed);
    BinaryPrimitives.WriteInt32LittleEndian(di[4..], fileset);
    BinaryPrimitives.WriteUInt32LittleEndian(di[8..], ino);
    BinaryPrimitives.WriteUInt32LittleEndian(di[12..], 1);                // di_gen
    WritePxd(di[16..], ixpxdLength, ixpxdAddress);                        // di_ixpxd
    BinaryPrimitives.WriteInt64LittleEndian(di[24..], size);              // di_size
    BinaryPrimitives.WriteInt64LittleEndian(di[32..], nblocks);           // di_nblocks
    BinaryPrimitives.WriteUInt32LittleEndian(di[40..], nlink);            // di_nlink
    BinaryPrimitives.WriteUInt32LittleEndian(di[44..], 0);                // di_uid
    BinaryPrimitives.WriteUInt32LittleEndian(di[48..], 0);                // di_gid
    BinaryPrimitives.WriteUInt32LittleEndian(di[52..], mode);             // di_mode
    for (var t = 0; t < 4; t++) {
      BinaryPrimitives.WriteUInt32LittleEndian(di[(56 + t * 8)..], this._writeTimestamp);
      BinaryPrimitives.WriteUInt32LittleEndian(di[(60 + t * 8)..], 0);
    }
    BinaryPrimitives.WriteUInt32LittleEndian(di[120..], nextIndex);       // di_next_index
    BinaryPrimitives.WriteUInt32LittleEndian(di[124..], 0);               // di_acltype
    // di_data union starts at offset 128. Caller fills it in (xtree or dtree).
  }

  // ── xtree root in di_data (288 bytes at offset 224..511) ────────────────
  // Layout: next(8) + prev(8) + flag(1) + rsrvd1(1) + nextindex(le16) +
  //         maxentry(le16) + rsrvd2(le16) + self pxd(8) + xad slots[16].
  // First xad starts at slot index 2 (XTENTRYSTART).
  // flag = DXD_INDEX | BT_ROOT | BT_LEAF = 0x83.
  private static void WriteXtreeRoot(Span<byte> data, (ulong offset, uint length, ulong address)[] entries) {
    data.Clear();
    const int XtentryStart = 2;
    var maxEntry = data.Length / 16;                                      // 18 for 288-byte area
    data[16] = BtRootLeafFlag;                                            // 0x83
    data[17] = 0;                                                         // rsrvd1
    BinaryPrimitives.WriteUInt16LittleEndian(data[18..], (ushort)(XtentryStart + entries.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(data[20..], (ushort)maxEntry);
    BinaryPrimitives.WriteUInt16LittleEndian(data[22..], 0);
    WritePxd(data[24..], length: 0, address: 0);                          // self
    for (var i = 0; i < entries.Length; i++) {
      var entryOff = (XtentryStart + i) * 16;
      WriteXad(data.Slice(entryOff, 16), entries[i].offset, entries[i].length, entries[i].address);
    }
  }

  private static void WriteXad(Span<byte> dst, ulong offset, uint length, ulong address) {
    dst.Clear();
    dst[0] = 0;
    dst[1] = 0; dst[2] = 0;
    dst[3] = (byte)((offset >> 32) & 0xFF);
    BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], (uint)(offset & 0xFFFFFFFF));
    WritePxd(dst[8..], length, address);
  }

  internal static void WritePxd(Span<byte> dst, uint length, ulong address) {
    var lenMasked = length & 0xFFFFFFu;
    var addrHi = (uint)((address >> 32) & 0xFF) << 24;
    BinaryPrimitives.WriteUInt32LittleEndian(dst[0..], lenMasked | addrHi);
    BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], (uint)(address & 0xFFFFFFFF));
  }

  // ── dtroot inline directory at offset 224, 288 bytes total ──────────────
  // Slot 0 = header (DASD 16 + flag 1 + nextindex 1 + freecnt 1 + freelist 1 +
  //          idotdot 4 + stbl[8]). Slots 1..8 = dtslot[32] each (ldtentry per file).
  private void WriteRootDtree(Span<byte> data) {
    data.Clear();
    // DASD (16 bytes) at +0 — zero
    data[16] = BtRootLeafFlag;                                            // flag = 0x83
    data[17] = (byte)this._files.Count;                                   // nextindex (count of populated stbl entries)
    var freeStart = 1 + this._files.Count;
    sbyte freecnt = (sbyte)Math.Max(0, 8 - this._files.Count);
    data[18] = (byte)freecnt;                                             // freecnt
    data[19] = (byte)(freecnt == 0 ? -1 : freeStart);                     // freelist head
    BinaryPrimitives.WriteUInt32LittleEndian(data[20..], RootIno);        // idotdot
    // stbl[8]: populated entries point at slot indices 1..count; unused stay 0
    // (per mkfs.jfs init_fileset_inode_table; not -1 / 0xFF).
    for (var i = 0; i < this._files.Count; i++)
      data[24 + i] = (byte)(i + 1);

    for (var i = 0; i < this._files.Count; i++) {
      var slotOff = (i + 1) * 32;
      var childIno = (uint)(FirstFileIno + i);
      BinaryPrimitives.WriteUInt32LittleEndian(data[slotOff..], childIno);
      var name = this._files[i].Name;
      var nameLen = Math.Min(name.Length, 11);
      data[slotOff + 4] = unchecked((byte)-1);
      data[slotOff + 5] = (byte)nameLen;
      for (var c = 0; c < nameLen; c++)
        BinaryPrimitives.WriteUInt16LittleEndian(data[(slotOff + 6 + c * 2)..], name[c]);
      BinaryPrimitives.WriteUInt32LittleEndian(data[(slotOff + 28)..], (uint)i);
    }

    // Free-list chain over remaining dtslots (indices freeStart..8). Each free
    // slot has next = next-free-slot (-1 for the last) and cnt = 1, mirroring
    // mkfs.jfs init_fileset_inode_table.
    for (var s = freeStart; s <= 8; s++) {
      var slotOff = s * 32;
      data[slotOff] = (byte)(s < 8 ? s + 1 : unchecked((byte)-1));      // next
      data[slotOff + 1] = 1;                                             // cnt = 1
    }
  }

  // ── block allocation map (BMAP) ────────────────────────────────────────
  // Layout for level 0 (≤ MAXL0SIZE = 1<<23 = 8M blocks):
  //   block 16: dbmap (control_page)
  //   block 17, 18: reserved (level skip placeholder per `next_page` calc)
  //   block 19: L0 dmapctl
  //   block 20+: dmaps, one per BPERDMAP=8192 blocks
  //
  // We allocate space for up to 2 dmaps (covering 16384 blocks). For the WORM
  // image any usableBlocks ≤ Dmap_Bperdmap = 8192 needs 1 dmap; up to 16384
  // needs 2. The BMAP_I xtree always claims `BmapTotalBlocks = 6` blocks
  // (16..21) so the layout after BMAP is fixed.
  private static void WriteBlockMap(byte[] image, int usableBlocks, bool[] allocated) {
    var ndmaps = (usableBlocks + Dmap_Bperdmap - 1) / Dmap_Bperdmap;
    if (ndmaps < 1) ndmaps = 1;
    if (ndmaps > 2)
      throw new InvalidOperationException("JfsWriter does not support images requiring more than 2 dmaps.");

    var dmapMaxes = new sbyte[ndmaps];
    for (var i = 0; i < ndmaps; i++) {
      var dmapStartBlock = i * Dmap_Bperdmap;
      var dmapBlockCount = Math.Min(Dmap_Bperdmap, usableBlocks - dmapStartBlock);
      var dmapPageBlock = FirstDmapBlock + i;
      dmapMaxes[i] = WriteDmap(image, dmapPageBlock, dmapStartBlock, dmapBlockCount, allocated);
    }

    var ctlMax = WriteDmapctl(image, L0DmapctlBlock, level: 0, dmapMaxes, usableBlocks);
    WriteDbmap(image, BmapBlock, usableBlocks, allocated, agl2size: Dmap_L2bperdmap,
      maxfreebud: ctlMax);
  }

  /// <summary>Writes one dmap page; returns the maximum free-buddy exponent (root of dmaptree).</summary>
  private static sbyte WriteDmap(byte[] image, int pageBlock, long startBlk, int nblocks, bool[] allocated) {
    var off = (int)((long)pageBlock * BlockSize);
    var page = image.AsSpan(off, BlockSize);
    page.Clear();

    // dmap header
    BinaryPrimitives.WriteInt32LittleEndian(page[0..], nblocks);                          // nblocks
    BinaryPrimitives.WriteInt64LittleEndian(page[8..], startBlk);                         // start

    // dmap layout (struct dmap, jfs_dmap.h):
    //   header(16) + dmaptree(360) + pad[1672] = 2048 bytes before wmap
    //   wmap[LPERDMAP=256] at offset 2048 (1024 bytes)
    //   pmap[LPERDMAP=256] at offset 3072 (1024 bytes)
    var wmapOff = 2048;
    var pmapOff = 3072;
    var nfree = 0;
    for (var leaf = 0; leaf < Dmap_Lperdmap; leaf++) {
      var word = 0u;
      for (var bit = 0; bit < 32; bit++) {
        var blk = startBlk + leaf * 32 + bit;
        if (blk >= startBlk + nblocks) {
          // blocks past nblocks: mark as allocated (out of range)
          word |= 0x80000000u >> bit;
          continue;
        }
        var idx = (int)blk;
        if (idx < allocated.Length && allocated[idx]) {
          word |= 0x80000000u >> bit;                                                     // bit set = allocated
        } else {
          ++nfree;
        }
      }
      BinaryPrimitives.WriteUInt32LittleEndian(page[(wmapOff + leaf * 4)..], word);
      BinaryPrimitives.WriteUInt32LittleEndian(page[(pmapOff + leaf * 4)..], word);
    }

    // dmap.nfree at offset 4
    BinaryPrimitives.WriteInt32LittleEndian(page[4..], nfree);

    // ── dmaptree at offset 16 (struct dmaptree, 360 bytes) ─────────────────
    BinaryPrimitives.WriteInt32LittleEndian(page[16..], Dmap_Lperdmap);                   // nleafs = 256
    BinaryPrimitives.WriteInt32LittleEndian(page[20..], Dmap_L2lperdmap);                 // l2nleafs = 8
    BinaryPrimitives.WriteInt32LittleEndian(page[24..], Dmap_Leafind);                    // leafidx = 85
    BinaryPrimitives.WriteInt32LittleEndian(page[28..], 4);                               // height
    page[32] = Dmap_Budmin;                                                               // budmin = 5

    // dmaptree.stree[Treesize] starts at offset 33.
    // Leaves are at stree[Dmap_Leafind .. Dmap_Leafind+Dmap_Lperdmap-1] = stree[85..340].
    // Each leaf records the maximum free-string exponent in its 32-bit wmap word.
    var streeBase = 33;
    var leaves = new sbyte[Dmap_Lperdmap];
    for (var leaf = 0; leaf < Dmap_Lperdmap; leaf++) {
      var word = BinaryPrimitives.ReadUInt32LittleEndian(page[(wmapOff + leaf * 4)..]);
      leaves[leaf] = MaxFreeStringExponent(word);
    }
    for (var leaf = 0; leaf < Dmap_Lperdmap; leaf++)
      page[streeBase + Dmap_Leafind + leaf] = unchecked((byte)leaves[leaf]);

    // Adjust internal nodes by combining buddies (ujfs_adjtree).
    // The tree uses a 4-ary aggregation but per jfs_dmap.h adjtree it's
    // log2-based: each internal node = MAX(child0, child1) + 1 if both children
    // are equal max (a "buddy combine"), else MAX over 4 children.
    // Per ujfs_adjtree the actual algorithm is: for each non-leaf node, it picks
    // MAX(MAX(c0,c1),MAX(c2,c3)) but also if all four children equal level L,
    // they combine into a buddy of level L+1 — which is bounded by budmin+l2numleavesPerNode.
    // We implement the canonical mkfs algorithm below.
    var max = AdjTree(page, streeBase, Dmap_L2lperdmap, Dmap_Budmin);
    return max;
  }

  /// <summary>Writes one dmapctl page covering up to LPERCTL leaves.</summary>
  private static sbyte WriteDmapctl(byte[] image, int pageBlock, int level, sbyte[] childMaxes, int usableBlocks) {
    var off = (int)((long)pageBlock * BlockSize);
    var page = image.AsSpan(off, BlockSize);
    page.Clear();

    BinaryPrimitives.WriteInt32LittleEndian(page[0..], Dmapctl_Lperctl);                  // nleafs = 1024
    BinaryPrimitives.WriteInt32LittleEndian(page[4..], Dmapctl_L2lperctl);                // l2nleafs = 10
    BinaryPrimitives.WriteInt32LittleEndian(page[8..], Dmapctl_Leafind);                  // leafidx = 341
    BinaryPrimitives.WriteInt32LittleEndian(page[12..], 5);                               // height (mkfs uses 5 always)
    page[16] = (byte)(Dmap_L2bperdmap + level * Dmapctl_L2lperctl);                       // budmin (13 for L0)

    var streeBase = 17;
    // Initialise all leaves to NOFREE (-1).
    for (var i = 0; i < Dmapctl_Lperctl; i++)
      page[streeBase + Dmapctl_Leafind + i] = unchecked((byte)Dmap_Nofree);

    // First N leaves get the actual child-dmap max values.
    for (var i = 0; i < childMaxes.Length; i++)
      page[streeBase + Dmapctl_Leafind + i] = unchecked((byte)childMaxes[i]);

    var max = AdjTree(page, streeBase, Dmapctl_L2lperctl, Dmap_L2bperdmap + level * Dmapctl_L2lperctl);
    return max;
  }

  /// <summary>Writes the dbmap (BMAP control_page).</summary>
  private static void WriteDbmap(byte[] image, int pageBlock, int usableBlocks, bool[] allocated,
      int agl2size, sbyte maxfreebud) {
    var off = (int)((long)pageBlock * BlockSize);
    var page = image.AsSpan(off, BlockSize);
    page.Clear();

    var nfree = 0;
    for (var i = 0; i < usableBlocks && i < allocated.Length; i++)
      if (!allocated[i]) ++nfree;

    BinaryPrimitives.WriteInt64LittleEndian(page[0..], usableBlocks);                     // dn_mapsize
    BinaryPrimitives.WriteInt64LittleEndian(page[8..], nfree);                            // dn_nfree
    BinaryPrimitives.WriteInt32LittleEndian(page[16..], 0);                               // dn_l2nbperpage
    BinaryPrimitives.WriteInt32LittleEndian(page[20..], (usableBlocks + Dmap_Bperdmap - 1) / Dmap_Bperdmap); // dn_numag
    BinaryPrimitives.WriteInt32LittleEndian(page[24..], 0);                               // dn_maxlevel
    BinaryPrimitives.WriteInt32LittleEndian(page[28..], 0);                               // dn_maxag
    BinaryPrimitives.WriteInt32LittleEndian(page[32..], 0);                               // dn_agpref
    BinaryPrimitives.WriteInt32LittleEndian(page[36..], 0);                               // dn_aglevel
    BinaryPrimitives.WriteInt32LittleEndian(page[40..], 0);                               // dn_agheigth
    BinaryPrimitives.WriteInt32LittleEndian(page[44..], 1);                               // dn_agwidth
    BinaryPrimitives.WriteInt32LittleEndian(page[48..], Dmapctl_Leafind);                 // dn_agstart = 341
    BinaryPrimitives.WriteInt32LittleEndian(page[52..], agl2size);                        // dn_agl2size = 13
    // dn_agfree[MAXAG=128] starts at offset 56 (8-byte aligned).
    var agSize = 1L << agl2size;
    var numAg = (usableBlocks + (int)agSize - 1) / (int)agSize;
    for (var ag = 0; ag < numAg; ag++) {
      var agStart = ag * agSize;
      var agEnd = Math.Min(agStart + agSize, usableBlocks);
      var agFree = 0L;
      for (var b = agStart; b < agEnd && b < allocated.Length; b++)
        if (!allocated[b]) ++agFree;
      BinaryPrimitives.WriteInt64LittleEndian(page[(56 + ag * 8)..], agFree);
    }
    // dn_agsize at 56 + 128*8 = 1080
    BinaryPrimitives.WriteInt64LittleEndian(page[1080..], agSize);
    // dn_maxfreebud at 1088
    page[1088] = unchecked((byte)maxfreebud);
  }

  // ── tree adjustment (ujfs_adjtree from jfsutils libfs/diskmap.c) ─────────
  // Two-phase algorithm:
  //   Phase 1: walk the leaf array in pairs of size `bsize` (1, 2, 4, ...).
  //     If two adjacent buddies both equal `l2free`, combine: left leaf gets
  //     `l2free+1`, right leaf gets NOFREE (-1). l2free starts at l2min and
  //     increments to l2min + l2leaves.
  //   Phase 2: bubble values up. Each parent at index `(leaf-1)/4` is set to
  //     MAX(four children). Walk from leaf level up to the root (index 0).
  //
  // Tree layout in stree (4-ary):
  //   index 0      = root
  //   index 1..4   = level 1
  //   index 5..20  = level 2
  //   index 21..84 = level 3
  //   index 85..   = leaves (for dmap, 256 leaves → 85..340)
  //   leaf_index = (nleaves - 1) / 3
  private static sbyte AdjTree(Span<byte> page, int streeBase, int l2leaves, int l2min) {
    var nleaves = 1 << l2leaves;
    var leafIndex = (nleaves - 1) / 3;
    var l2max = l2min + l2leaves;

    // Phase 1: combine buddies in the leaf array.
    var bsize = 1;
    for (var l2free = l2min; l2free < l2max; l2free++, bsize <<= 1) {
      var nextb = bsize << 1;
      for (var idx = 0; idx < nleaves; idx += nextb) {
        var leftIdx = streeBase + leafIndex + idx;
        var rightIdx = streeBase + leafIndex + idx + bsize;
        if ((sbyte)page[leftIdx] == l2free && (sbyte)page[rightIdx] == l2free) {
          page[leftIdx] = (byte)(l2free + 1);
          page[rightIdx] = unchecked((byte)Dmap_Nofree);
        }
      }
    }

    // Phase 2: bubble up the tree, 4 children → parent (max of 4).
    var leaf = leafIndex;
    var numAtLevel = nleaves >> 2;
    while (numAtLevel > 0) {
      var parent = (leaf - 1) >> 2;
      for (var i = 0; i < numAtLevel; i++) {
        var c0 = (sbyte)page[streeBase + leaf + i * 4 + 0];
        var c1 = (sbyte)page[streeBase + leaf + i * 4 + 1];
        var c2 = (sbyte)page[streeBase + leaf + i * 4 + 2];
        var c3 = (sbyte)page[streeBase + leaf + i * 4 + 3];
        var max = (sbyte)Math.Max(Math.Max(c0, c1), Math.Max(c2, c3));
        page[streeBase + parent + i] = unchecked((byte)max);
      }
      numAtLevel >>= 2;
      leaf = parent;
    }

    return (sbyte)page[streeBase + 0];
  }

  /// <summary>Compute the maximum buddy-free exponent for a 32-bit wmap word.</summary>
  /// <remarks>
  /// Bit-1 = allocated, bit-0 = free. Returns:
  ///   <list type="bullet">
  ///     <item>BUDMIN (5) if word == 0 (all 32 bits free).</item>
  ///     <item>BUDMIN-1 (4) if either u16 half is 0 (16 contiguous free bits).</item>
  ///     <item>NOFREE (-1) if word == 0xFFFFFFFF (all allocated).</item>
  ///     <item>Otherwise max(budtab[byte0..byte3]) where budtab maps each byte
  ///       to its largest free-buddy exponent (0..3).</item>
  ///   </list>
  /// Mirrors <c>ujfs_maxbuddy</c> from jfsutils libfs/diskmap.c.
  /// </remarks>
  private static sbyte MaxFreeStringExponent(uint word) {
    if (word == 0u) return Dmap_Budmin;
    if (word == 0xFFFFFFFFu) return Dmap_Nofree;
    var hi = (ushort)(word >> 16);
    var lo = (ushort)(word & 0xFFFF);
    if (hi == 0 || lo == 0) return Dmap_Budmin - 1;
    var b0 = BudTab[(byte)(word >> 24)];
    var b1 = BudTab[(byte)(word >> 16)];
    var b2 = BudTab[(byte)(word >> 8)];
    var b3 = BudTab[(byte)word];
    return (sbyte)Math.Max(Math.Max(b0, b1), Math.Max(b2, b3));
  }

  /// <summary>Buddy table from jfsutils libfs/diskmap.c (256 entries, sbyte).</summary>
  private static readonly sbyte[] BudTab = [
    3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, -1,
  ];
}
