#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.F2fs;

/// <summary>
/// Builds spec-compliant F2FS filesystem images that are accepted by Linux <c>fsck.f2fs</c>.
/// <para>
/// Layout (4 KiB blocks, 512 blocks per 2 MiB segment, single-segment sections,
/// single-section zones):
/// </para>
/// <list type="bullet">
///   <item><description>Block 0..1: superblock copies (struct at offset 1024 inside block 0/1).</description></item>
///   <item><description>Segment 1 reserved (segment0 region) — empty per mkfs convention.</description></item>
///   <item><description>Segments 1-2: checkpoint pair. Each pack = 6 blocks (cp1 + compact-summary + 3 node summaries + cp2).</description></item>
///   <item><description>Segments 3-4: SIT pair (Segment Information Table).</description></item>
///   <item><description>Segments 5-6: NAT pair (Node Address Table).</description></item>
///   <item><description>Segment 7: SSA (Segment Summary Area).</description></item>
///   <item><description>Segments 8+: Main area. Six "current" segments host root inode,
///       file inodes, dentry data, and file data with type-correct SIT entries.</description></item>
/// </list>
/// <para>
/// Main-area segment assignment (relative to <c>main_blkaddr</c>):
/// </para>
/// <list type="bullet">
///   <item><description>Main seg 0 = HOT_NODE — root inode at block 0.</description></item>
///   <item><description>Main seg 1 = WARM_NODE — regular-file inodes at blocks 0..N-1.</description></item>
///   <item><description>Main seg 2 = COLD_NODE — unused (kept type-tagged for fsck).</description></item>
///   <item><description>Main seg 3 = HOT_DATA — root dentry blocks (when not using inline-dentry; typically empty).</description></item>
///   <item><description>Main seg 4 = WARM_DATA — file data blocks.</description></item>
///   <item><description>Main seg 5 = COLD_DATA — unused (kept type-tagged for fsck).</description></item>
/// </list>
/// <para>
/// The root inode uses inline dentries (<c>F2FS_INLINE_DENTRY</c>) with the kernel-spec
/// layout starting at <c>i_addr[1]</c> (offset 364 — i.e. one reserved address pointer for
/// the inline-data flag): bitmap[24] + reserved[16] + dir_entry[192] + filename[192][8].
/// </para>
/// <para>
/// SIT entries encode both the valid-block count (low 10 bits) and the segment type
/// (high 6 bits). Each "current" segment in the checkpoint must carry a SIT entry whose
/// type matches the corresponding <c>CURSEG_*</c> constant, and whose valid_map covers
/// exactly the blocks 0..(blkoff-1) — fsck cross-checks this.
/// </para>
/// </summary>
public sealed class F2fsWriter {
  // --- F2FS on-disk constants (kernel include/linux/f2fs_fs.h) ---
  internal const uint F2fsMagic = 0xF2F52010;
  internal const int SuperOffset = 1024; // F2FS_SUPER_OFFSET inside the SB block
  internal const int BlockSize = 4096;
  internal const int LogBlockSize = 12;
  internal const int LogSectorSize = 9;
  internal const int LogSectorsPerBlock = 3;
  internal const int LogBlocksPerSeg = 9;
  internal const int BlocksPerSeg = 1 << LogBlocksPerSeg; // 512
  internal const int SegmentSize = BlocksPerSeg * BlockSize; // 2 MiB
  internal const int SegsPerSec = 1;
  internal const int SecsPerZone = 1;

  // Reserved node IDs (kernel f2fs_fs.h: NODE=1, META=2, ROOT=3).
  internal const uint NodeIno = 1;
  internal const uint MetaIno = 2;
  internal const uint RootIno = 3;

  // Inline inode flags (i_inline byte).
  internal const byte F2fsInlineXattr = 0x01;
  internal const byte F2fsInlineData = 0x02;
  internal const byte F2fsInlineDentry = 0x04;
  internal const byte F2fsDataExist = 0x08;
  internal const byte F2fsInlineDots = 0x10;

  // Checkpoint flags.
  internal const uint CpUmountFlag = 0x00000001;
  internal const uint CpCompactSumFlag = 0x00000004;

  // Curseg types — same as the kernel's CURSEG_* enum.
  internal const int CursegHotData = 0;
  internal const int CursegWarmData = 1;
  internal const int CursegColdData = 2;
  internal const int CursegHotNode = 3;
  internal const int CursegWarmNode = 4;
  internal const int CursegColdNode = 5;

  // File types (kernel enum f2fs_ft — shared with ext).
  internal const byte FtRegFile = 1;
  internal const byte FtDir = 2;

  // Directory entry slot = 8 bytes (F2FS_SLOT_LEN).
  internal const int SlotLen = 8;

  // Inode data-pointer count for a 4 KiB inode with no extra-attr: 923 × __le32.
  internal const int AddrsPerInode = 923;

  // Inline-dentry layout (kernel constants).
  //
  // CRITICAL: When i_inline contains F2FS_INLINE_DENTRY (and no F2FS_FEATURE_FLEXIBLE_INLINE_XATTR
  // feature is set), the kernel `get_inline_xattr_addrs()` returns DEFAULT_INLINE_XATTR_ADDRS = 50,
  // reserving 50 __le32 slots (200 bytes) at the END of the i_addr region for inline xattrs.
  // So MAX_INLINE_DATA = 4 * (923 - 50 - 0 - 1) = 4 * 872 = 3488 bytes (NOT 3688).
  //
  // NR_INLINE_DENTRY = MAX_INLINE_DATA * 8 / ((11 + 8) * 8 + 1) = 3488*8/153 = 182.
  // INLINE_DENTRY_BITMAP_SIZE = (182 + 7) / 8 = 23.
  // INLINE_RESERVED_SIZE = 3488 - ((11+8)*182 + 23) = 3488 - 3481 = 7.
  //
  // The inline-dentry region itself starts at i_addr[0 + 1] = offset 364 (the +1 is
  // DEF_INLINE_RESERVED_SIZE — the kernel uses i_addr[0] as the inline-data flag slot).
  internal const int InlineXattrAddrSlots = 50; // DEFAULT_INLINE_XATTR_ADDRS when INLINE_DENTRY is set
  internal const int InlineReservedAddrSlots = 1; // DEF_INLINE_RESERVED_SIZE
  internal const int MaxInlineDataBytes = (AddrsPerInode - InlineXattrAddrSlots - InlineReservedAddrSlots) * 4; // 3488
  internal const int NrInlineDentry = 182;
  internal const int InlineDentryBitmapSize = (NrInlineDentry + 7) / 8; // 23
  internal const int InlineDentryReserved = MaxInlineDataBytes
      - (NrInlineDentry * (11 + SlotLen) + InlineDentryBitmapSize); // 7

  // Dentry block constants for non-inline directories.
  internal const int NrDentryInBlock = 214;
  internal const int DentryBlockBitmapSize = (NrDentryInBlock + 7) / 8; // 27
  internal const int DentryBlockReserved =
      BlockSize - (NrDentryInBlock * (11 + SlotLen) + DentryBlockBitmapSize); // 3

  // Layout plan — segment indexes (relative to image start, NOT segment0_blkaddr).
  // segment0_blkaddr = block 512 (one segment of pre-roll padding before metadata starts).
  internal const int SegSb = 0;       // blocks 0..511 (only first 2 used for SBs).
  internal const int SegCp = 1;       // 2 segments (segments 1-2).
  internal const int SegSit = SegCp + 2; // 3-4
  internal const int SegNat = SegSit + 2; // 5-6
  internal const int SegSsa = SegNat + 2; // 7
  internal const int SegMain = SegSsa + 1; // 8

  // Main-area segment offsets (from main_blkaddr in segments — NOT absolute).
  internal const int MainSegHotNode = 0;
  internal const int MainSegWarmNode = 1;
  internal const int MainSegColdNode = 2;
  internal const int MainSegHotData = 3;
  internal const int MainSegWarmData = 4;
  internal const int MainSegColdData = 5;

  // Default 64 MiB image — 32 segments at 2 MiB each.
  internal const int DefaultSegmentCount = 32;

  // CP pack layout — 6 blocks when cp_payload = 0:
  //   block 0: cp_page_1
  //   block 1: compact summary (NAT journal + SIT journal + data summaries)
  //   block 2: HOT_NODE summary
  //   block 3: WARM_NODE summary
  //   block 4: COLD_NODE summary
  //   block 5: cp_page_2
  internal const int CpPackTotalBlockCount = 6;

  // f2fs_summary_block layout: entries[512] (3584 bytes) + journal (507) + footer (5) = 4096.
  internal const int SumEntriesSize = 7 * 512; // 3584
  internal const int SumJournalSize = 507;
  internal const int SumFooterSize = 5;

  private readonly List<(string Name, byte[] Data)> _files = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (data.Length > (long)AddrsPerInode * BlockSize)
      throw new InvalidOperationException(
        $"F2FS writer supports only direct-pointer files (<= {(long)AddrsPerInode * BlockSize} bytes).");
    this._files.Add((name, data));
  }

  public byte[] Build(int totalSegments = DefaultSegmentCount) {
    if (totalSegments < SegMain + 8)
      throw new ArgumentOutOfRangeException(nameof(totalSegments),
        $"F2FS image needs at least {SegMain + 8} segments (need 6 active main segments + slack).");

    // Inline dentry budget: NrInlineDentry slots minus 2 for "." and "..".
    const int dotsSlots = 2;
    if (this._files.Count + dotsSlots > NrInlineDentry)
      throw new InvalidOperationException(
        $"F2FS writer: inline dentry supports at most {NrInlineDentry - dotsSlots} files in root.");

    var totalBlocks = totalSegments * BlocksPerSeg;
    var disk = new byte[totalBlocks * BlockSize];

    // ---- Plan main-area allocations ----
    // mkfs places HOT_NODE at main-relative seg 0, root inode at block 0 of that segment.
    // cur_seg values are RELATIVE to main_blkaddr (i.e. main-relative segno).
    var mainStart = SegMain * BlocksPerSeg;

    // Root inode block (HOT_NODE seg, block 0).
    var rootInodeBlock = mainStart + MainSegHotNode * BlocksPerSeg + 0;

    // File inode and data block planning.
    var filePlan = new List<FilePlan>();
    uint nextNid = RootIno + 1; // 4, 5, 6, ...
    var nextWarmNodeBlk = 0;
    var nextWarmDataBlk = 0;
    var warmNodeSegStart = mainStart + MainSegWarmNode * BlocksPerSeg;
    var warmDataSegStart = mainStart + MainSegWarmData * BlocksPerSeg;
    foreach (var (name, data) in this._files) {
      var dataBlocksNeeded = data.Length == 0 ? 0 : (data.Length + BlockSize - 1) / BlockSize;
      if (nextWarmNodeBlk + 1 > BlocksPerSeg
          || nextWarmDataBlk + dataBlocksNeeded > BlocksPerSeg)
        throw new InvalidOperationException(
          "F2FS writer: WARM_NODE/WARM_DATA segment full (single-segment writer limit).");

      var inodeBlock = warmNodeSegStart + nextWarmNodeBlk;
      ++nextWarmNodeBlk;

      var dataBlocks = new List<int>(dataBlocksNeeded);
      for (var i = 0; i < dataBlocksNeeded; ++i) {
        dataBlocks.Add(warmDataSegStart + nextWarmDataBlk);
        ++nextWarmDataBlk;
      }

      filePlan.Add(new FilePlan {
        Nid = nextNid++,
        Name = name,
        Data = data,
        InodeBlock = inodeBlock,
        DataBlocks = dataBlocks,
      });
    }

    var validNodeCount = 1 + filePlan.Count;
    var validInodeCount = validNodeCount;
    var totalDataBlocks = filePlan.Sum(f => f.DataBlocks.Count);
    var validBlockCount = validNodeCount + totalDataBlocks;

    // ---- Section / segment geometry ----
    // mkfs reserves segment 0 (one segment of pre-roll padding before segment0_blkaddr=512).
    // segment_count counts segments from segment0 onwards.
    var countedSegments = totalSegments - 1;
    var segmentCountMain = countedSegments - (SegMain - SegCp); // = 31 - 7 = 24 for default.
    var totalSections = countedSegments / SegsPerSec;
    var totalZones = totalSections / SecsPerZone;

    // ---- 1) Write file data and file inodes (regular files in WARM_NODE/WARM_DATA) ----
    foreach (var f in filePlan) {
      var remaining = f.Data.Length;
      for (var i = 0; i < f.DataBlocks.Count; ++i) {
        var len = Math.Min(BlockSize, remaining);
        Buffer.BlockCopy(f.Data, i * BlockSize, disk, f.DataBlocks[i] * BlockSize, len);
        remaining -= len;
      }
      WriteRegularFileInode(disk, f.InodeBlock * BlockSize, f.Nid, f.Name, f.Data.Length, f.DataBlocks);
    }

    // ---- 2) Write root inode with inline dentries ----
    WriteRootInodeInline(disk, rootInodeBlock * BlockSize,
      filePlan.Select(f => (f.Nid, f.Name)).ToList());

    // ---- 3) Write NAT entries on disk (file entries; root NAT also lives in journal) ----
    var natBlkAddr = SegNat * BlocksPerSeg;
    // mkfs uses block_addr=1 for reserved node/meta inodes (a sentinel — a real block address would be 0 or main).
    WriteNatEntry(disk, natBlkAddr, NodeIno, NodeIno, 1u);
    WriteNatEntry(disk, natBlkAddr, MetaIno, MetaIno, 1u);
    WriteNatEntry(disk, natBlkAddr, RootIno, RootIno, (uint)rootInodeBlock);
    foreach (var f in filePlan)
      WriteNatEntry(disk, natBlkAddr, f.Nid, f.Nid, (uint)f.InodeBlock);

    // ---- 4) Write SIT entries for ALL main-area segments (typed correctly). ----
    var sitBlkAddr = SegSit * BlocksPerSeg;
    var nowSecs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Active segments — type and valid count.
    var sitTypes = new int[segmentCountMain];
    var sitVblocks = new int[segmentCountMain];
    var sitMaps = new byte[segmentCountMain][];
    for (var i = 0; i < segmentCountMain; ++i) {
      sitTypes[i] = CursegHotData; // default for "free" main segments (matches mkfs).
      sitVblocks[i] = 0;
      sitMaps[i] = new byte[64];
    }

    // HOT_NODE: 1 valid block (root inode).
    sitTypes[MainSegHotNode] = CursegHotNode;
    sitVblocks[MainSegHotNode] = 1;
    SetBit(sitMaps[MainSegHotNode], 0);

    // WARM_NODE: file inode count.
    sitTypes[MainSegWarmNode] = CursegWarmNode;
    sitVblocks[MainSegWarmNode] = filePlan.Count;
    for (var i = 0; i < filePlan.Count; ++i)
      SetBit(sitMaps[MainSegWarmNode], i);

    // COLD_NODE: empty.
    sitTypes[MainSegColdNode] = CursegColdNode;

    // HOT_DATA: empty (root uses inline dentry).
    sitTypes[MainSegHotData] = CursegHotData;

    // WARM_DATA: file data block count.
    sitTypes[MainSegWarmData] = CursegWarmData;
    sitVblocks[MainSegWarmData] = totalDataBlocks;
    for (var i = 0; i < totalDataBlocks; ++i)
      SetBit(sitMaps[MainSegWarmData], i);

    // COLD_DATA: empty.
    sitTypes[MainSegColdData] = CursegColdData;

    // Write SIT entries to disk. mkfs.f2fs leaves the SIT region zero-filled and stores
    // entries for active segments in the SIT journal of the checkpoint pack. We follow the
    // same pattern: disk SIT remains zero (which fsck interprets as type=HOT_DATA, count=0
    // for "free" segments), and the journal carries the entries for the 6 active cursegs.
    // Writing typed entries to disk is also accepted by fsck — but mkfs's behaviour is more
    // tested, so we mirror it.
    // (No-op: SIT region stays zero-initialised in `disk`.)

    // ---- 5) Write checkpoint pack (both copies). ----
    var cpBlkAddr = SegCp * BlocksPerSeg;
    var freeSegments = segmentCountMain - 6; // 6 active main segments.
    // user_block_count formula from mkfs: (free + 6 - ovp) * blocks_per_seg.
    // Need user_block_count < segment_count_main * blocks_per_seg per fsck sanity check.
    var rsvdSegments = 2u;        // fsmeta needs >= 9 (2+2+2+1+rsvd).
    var ovpSegments = 6u;         // > rsvd; modest reservation.
    var userBlockCount = ((ulong)(freeSegments + 6) - ovpSegments) * BlocksPerSeg;

    // Build the 6 CP-pack blocks once and copy into both pack slots.
    var cpPack = BuildCheckpointPack(
      validBlockCount: (ulong)validBlockCount,
      validInodeCount: (uint)validInodeCount,
      validNodeCount: (uint)validNodeCount,
      nextFreeNid: nextNid,
      userBlockCount: userBlockCount,
      countedSegments: (uint)countedSegments,
      freeSegments: (uint)freeSegments,
      rsvdSegments: rsvdSegments,
      ovpSegments: ovpSegments,
      sitTypes: sitTypes,
      sitVblocks: sitVblocks,
      sitMaps: sitMaps,
      filePlan: filePlan,
      mainBlkAddr: mainStart,
      nowSecs: nowSecs,
      checkpointVer: 1UL);

    // Copy CP pack to slot 0 (segment 1).
    Array.Copy(cpPack, 0, disk, cpBlkAddr * BlockSize, cpPack.Length);
    // Copy CP pack to slot 1 (segment 2). Build a separate copy with checkpoint_ver = 0 for the
    // older pack — mkfs uses 0 for the unused pack so the newer one (ver=1) is selected.
    var cpPack2 = BuildCheckpointPack(
      validBlockCount: (ulong)validBlockCount,
      validInodeCount: (uint)validInodeCount,
      validNodeCount: (uint)validNodeCount,
      nextFreeNid: nextNid,
      userBlockCount: userBlockCount,
      countedSegments: (uint)countedSegments,
      freeSegments: (uint)freeSegments,
      rsvdSegments: rsvdSegments,
      ovpSegments: ovpSegments,
      sitTypes: sitTypes,
      sitVblocks: sitVblocks,
      sitMaps: sitMaps,
      filePlan: filePlan,
      mainBlkAddr: mainStart,
      nowSecs: nowSecs,
      checkpointVer: 0UL);
    Array.Copy(cpPack2, 0, disk, (cpBlkAddr + BlocksPerSeg) * BlockSize, cpPack2.Length);

    // ---- 6) Write both superblock copies ----
    var seg0BlkAddr = (uint)cpBlkAddr; // mkfs sets segment0_blkaddr = cp_blkaddr.
    WriteSuperblock(disk, blockOffset: 0, totalBlocks: totalBlocks, totalSegments: (uint)countedSegments,
      totalSections: (uint)totalSections, totalZones: (uint)totalZones, seg0BlkAddr: seg0BlkAddr,
      cpBlkAddr: (uint)cpBlkAddr,
      sitBlkAddr: (uint)sitBlkAddr, natBlkAddr: (uint)natBlkAddr,
      ssaBlkAddr: (uint)(SegSsa * BlocksPerSeg), mainBlkAddr: (uint)mainStart,
      segmentCountMain: (uint)segmentCountMain);
    WriteSuperblock(disk, blockOffset: BlockSize, totalBlocks: totalBlocks, totalSegments: (uint)countedSegments,
      totalSections: (uint)totalSections, totalZones: (uint)totalZones, seg0BlkAddr: seg0BlkAddr,
      cpBlkAddr: (uint)cpBlkAddr,
      sitBlkAddr: (uint)sitBlkAddr, natBlkAddr: (uint)natBlkAddr,
      ssaBlkAddr: (uint)(SegSsa * BlocksPerSeg), mainBlkAddr: (uint)mainStart,
      segmentCountMain: (uint)segmentCountMain);

    return disk;
  }

  public void WriteTo(Stream output) {
    var bytes = this.Build();
    output.Write(bytes, 0, bytes.Length);
  }

  // ==================================================================
  // Internal types
  // ==================================================================
  private sealed class FilePlan {
    public uint Nid;
    public string Name = string.Empty;
    public byte[] Data = [];
    public int InodeBlock;
    public List<int> DataBlocks = [];
  }

  // ==================================================================
  // Superblock (f2fs_super_block) — block starts at blockOffset, struct
  // starts at blockOffset + 1024 (F2FS_SUPER_OFFSET).
  // ==================================================================
  private static void WriteSuperblock(
    byte[] disk, int blockOffset, int totalBlocks, uint totalSegments, uint totalSections,
    uint totalZones, uint seg0BlkAddr, uint cpBlkAddr, uint sitBlkAddr, uint natBlkAddr,
    uint ssaBlkAddr, uint mainBlkAddr, uint segmentCountMain) {

    var off = blockOffset + SuperOffset;

    // The superblock struct lives immediately after F2FS_SUPER_OFFSET(1024) bytes of pad.
    // sizeof(f2fs_super_block) = 3072.
    var sb = new byte[3072];

    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0), F2fsMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(4), 1);   // major_ver
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(6), 0);   // minor_ver
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(8), LogSectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(12), LogSectorsPerBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(16), LogBlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(20), LogBlocksPerSeg);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(24), SegsPerSec);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(28), SecsPerZone);

    // checksum_offset — we don't set F2FS_FEATURE_SB_CHKSUM, so 0 is fine.
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(32), 0);

    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(36), (ulong)totalBlocks); // block_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(44), totalSections);      // section_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(48), totalSegments);      // segment_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(52), 2);                  // segment_count_ckpt
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(56), 2);                  // segment_count_sit
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(60), 2);                  // segment_count_nat
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(64), 1);                  // segment_count_ssa
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(68), segmentCountMain);   // segment_count_main
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(72), seg0BlkAddr);        // segment0_blkaddr
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(76), cpBlkAddr);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(80), sitBlkAddr);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(84), natBlkAddr);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(88), ssaBlkAddr);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(92), mainBlkAddr);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(96), RootIno);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(100), NodeIno);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(104), MetaIno);

    // uuid[16] — 128-bit, guaranteed non-zero.
    var uuid = Guid.NewGuid().ToByteArray();
    Array.Copy(uuid, 0, sb, 108, 16);

    // volume_name[512] __le16 — "CompressionWorkbench" as UTF-16LE.
    var name = "CompressionWorkbench"u8;
    for (var i = 0; i < name.Length && i < 511; ++i) {
      sb[124 + i * 2] = name[i];
      sb[124 + i * 2 + 1] = 0;
    }
    // end offset: 124 + 512*2 = 1148.

    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(1148), 0); // extension_count
    // extension_list[64][8] = 512 bytes — leave zero.
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(1148 + 4 + 512), 0); // cp_payload (none)
    // Remaining fields (version[256], init_version[256], feature, …) zero-filled.

    Array.Copy(sb, 0, disk, off, 3072);
  }

  // ==================================================================
  // Checkpoint pack: 6 blocks (cp1 + compact_summary + 3 node summaries + cp2).
  // ==================================================================
  private static byte[] BuildCheckpointPack(ulong validBlockCount, uint validInodeCount,
    uint validNodeCount, uint nextFreeNid, ulong userBlockCount, uint countedSegments,
    uint freeSegments, uint rsvdSegments, uint ovpSegments,
    int[] sitTypes, int[] sitVblocks, byte[][] sitMaps,
    List<FilePlan> filePlan, int mainBlkAddr, ulong nowSecs, ulong checkpointVer) {

    var pack = new byte[CpPackTotalBlockCount * BlockSize];

    // ---- Block 0 / 5: f2fs_checkpoint header ----
    var cp = BuildCheckpointBlock(
      validBlockCount, validInodeCount, validNodeCount, nextFreeNid, userBlockCount,
      countedSegments, freeSegments, rsvdSegments, ovpSegments, mainBlkAddr,
      filePlanCount: filePlan.Count, totalDataBlocks: filePlan.Sum(f => f.DataBlocks.Count),
      checkpointVer: checkpointVer);

    Array.Copy(cp, 0, pack, 0 * BlockSize, BlockSize);

    // ---- Block 1: compact summary block ----
    var compact = BuildCompactSummaryBlock(filePlan, mainBlkAddr, sitTypes, sitVblocks, sitMaps, nowSecs);
    Array.Copy(compact, 0, pack, 1 * BlockSize, BlockSize);

    // ---- Block 2: HOT_NODE summary (root inode) ----
    var hotNodeSum = BuildNodeSummaryBlock([RootIno]);
    Array.Copy(hotNodeSum, 0, pack, 2 * BlockSize, BlockSize);

    // ---- Block 3: WARM_NODE summary (file inodes) ----
    var warmNodeSum = BuildNodeSummaryBlock(filePlan.Select(f => f.Nid).ToArray());
    Array.Copy(warmNodeSum, 0, pack, 3 * BlockSize, BlockSize);

    // ---- Block 4: COLD_NODE summary (empty) ----
    var coldNodeSum = BuildNodeSummaryBlock([]);
    Array.Copy(coldNodeSum, 0, pack, 4 * BlockSize, BlockSize);

    // ---- Block 5: cp_page_2 (same as cp_page_1) ----
    Array.Copy(cp, 0, pack, 5 * BlockSize, BlockSize);

    return pack;
  }

  // Build the checkpoint header (cp_page_1 / cp_page_2 — same content, same CRC).
  private static byte[] BuildCheckpointBlock(ulong validBlockCount, uint validInodeCount,
    uint validNodeCount, uint nextFreeNid, ulong userBlockCount, uint countedSegments,
    uint freeSegments, uint rsvdSegments, uint ovpSegments, int mainBlkAddr,
    int filePlanCount, int totalDataBlocks, ulong checkpointVer) {

    var cp = new byte[BlockSize];

    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(0), checkpointVer);    // checkpoint_ver
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(8), userBlockCount);   // user_block_count
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(16), validBlockCount); // valid_block_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(24), rsvdSegments);    // rsvd_segment_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(28), ovpSegments);     // overprov_segment_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(32), freeSegments);    // free_segment_count

    // cur_node_segno[8] at 36 (×4 = 32), cur_node_blkoff[8] at 68 (×2 = 16).
    // cur_data_segno[8] at 84 (×4 = 32), cur_data_blkoff[8] at 116 (×2 = 16).
    var unused = 0xFFFFFFFFu;
    for (var i = 0; i < 8; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(36 + i * 4), unused);
      BinaryPrimitives.WriteUInt16LittleEndian(cp.AsSpan(68 + i * 2), 0);
      BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(84 + i * 4), unused);
      BinaryPrimitives.WriteUInt16LittleEndian(cp.AsSpan(116 + i * 2), 0);
    }
    // cur_node_segno[0..2] = HOT/WARM/COLD node main-relative segno.
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(36 + 0 * 4), (uint)MainSegHotNode);
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(36 + 1 * 4), (uint)MainSegWarmNode);
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(36 + 2 * 4), (uint)MainSegColdNode);
    BinaryPrimitives.WriteUInt16LittleEndian(cp.AsSpan(68 + 0 * 2), 1);                    // 1 valid node block (root).
    BinaryPrimitives.WriteUInt16LittleEndian(cp.AsSpan(68 + 1 * 2), (ushort)filePlanCount); // file inodes.
    BinaryPrimitives.WriteUInt16LittleEndian(cp.AsSpan(68 + 2 * 2), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(84 + 0 * 4), (uint)MainSegHotData);
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(84 + 1 * 4), (uint)MainSegWarmData);
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(84 + 2 * 4), (uint)MainSegColdData);
    BinaryPrimitives.WriteUInt16LittleEndian(cp.AsSpan(116 + 0 * 2), 0);                       // no HOT_DATA blocks (inline-dentry root).
    BinaryPrimitives.WriteUInt16LittleEndian(cp.AsSpan(116 + 1 * 2), (ushort)totalDataBlocks); // file data blocks.
    BinaryPrimitives.WriteUInt16LittleEndian(cp.AsSpan(116 + 2 * 2), 0);

    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(132), CpUmountFlag | CpCompactSumFlag); // ckpt_flags
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(136), CpPackTotalBlockCount);           // cp_pack_total_block_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(140), 1);                               // cp_pack_start_sum (= 1 + cp_payload).
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(144), validNodeCount);                  // valid_node_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(148), validInodeCount);                 // valid_inode_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(152), nextFreeNid);                     // next_free_nid
    // sit_ver_bitmap_bytesize / nat_ver_bitmap_bytesize per fsck sanity check:
    //   ((segment_count_(sit|nat) / 2) << log_blocks_per_seg) / 8
    //   = ((2/2) * 512) / 8 = 64 bytes each.
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(156), 64); // sit_ver_bitmap_bytesize
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(160), 64); // nat_ver_bitmap_bytesize

    const int checksumOffset = 4092; // CP_CHKSUM_OFFSET = BLKSIZE - 4
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(164), checksumOffset);                 // checksum_offset
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(168), 0UL);                            // elapsed_time
    // alloc_type[16] at 176 — all zero (LFS).
    // sit_nat_version_bitmap at 192 — leave zero (0 set bits ⇒ NAT/SIT pack 1 only).

    // Sentinel magic immediately before the checksum (kernel doesn't require this — it's a
    // round-trip / spec-offset convention shared with our reader and existing tests).
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(checksumOffset - 4), F2fsMagic);

    // f2fs-tools lib/libf2fs.c:f2fs_checkpoint_chksum: CRC seed = F2FS_SUPER_MAGIC, range = bytes [0, checksum_offset).
    var crc = F2fsCrc32(F2fsMagic, new ReadOnlySpan<byte>(cp, 0, checksumOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(checksumOffset), crc);

    return cp;
  }

  // Compact summary block: { NAT journal[507], SIT journal[507], data summaries…, footer[5] }.
  // The data summaries cover HOT_DATA, WARM_DATA, COLD_DATA in order; each curseg writes
  // `cur_data_blkoff[i]` summary entries (7 bytes each).
  private static byte[] BuildCompactSummaryBlock(List<FilePlan> filePlan, int mainBlkAddr,
    int[] sitTypes, int[] sitVblocks, byte[][] sitMaps, ulong nowSecs) {

    var block = new byte[BlockSize];

    // ---- NAT journal (root + ino 0 padding) ----
    // Layout: __le16 n_nats then nat_journal_entry[NAT_JOURNAL_ENTRIES] (38 entries × 13 bytes = 494) + reserved[11].
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0), 1); // n_nats = 1 (root only).
    // Entry 0 = root NAT entry: {nid(4), version(1), ino(4), block_addr(4)}.
    var rootInodeBlock = mainBlkAddr + MainSegHotNode * BlocksPerSeg + 0;
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(2), RootIno);          // nid (in journal entry)
    block[6] = 0;                                                                // version
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(7), RootIno);          // ino
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(11), (uint)rootInodeBlock); // block_addr

    // ---- SIT journal at offset 507 (SUM_JOURNAL_SIZE) ----
    // Layout: __le16 n_sits then sit_journal_entry[SIT_JOURNAL_ENTRIES] (6 × 78 = 468) + reserved[37].
    const int sitJournalOff = SumJournalSize;
    var nSits = 6;
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(sitJournalOff), (ushort)nSits);

    // Each sit_journal_entry = { __le32 segno, f2fs_sit_entry se(74) } = 78 bytes.
    int[] cursegs = [
      MainSegHotData, MainSegWarmData, MainSegColdData,
      MainSegHotNode, MainSegWarmNode, MainSegColdNode,
    ];
    for (var i = 0; i < cursegs.Length; ++i) {
      var entryOff = sitJournalOff + 2 + i * 78;
      var seg = cursegs[i];
      BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(entryOff), (uint)seg);
      // f2fs_sit_entry inside: vblocks(2) + valid_map[64] + mtime(8) = 74 bytes.
      var seOff = entryOff + 4;
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(seOff),
        (ushort)((sitTypes[seg] << 10) | (sitVblocks[seg] & 0x3FF)));
      sitMaps[seg].AsSpan(0, 64).CopyTo(block.AsSpan(seOff + 2, 64));
      BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(seOff + 66), nowSecs);
    }

    // ---- Data summary entries at offset 2 * SUM_JOURNAL_SIZE = 1014 ----
    // Order: HOT_DATA, WARM_DATA, COLD_DATA (matching read_compacted_summaries loop).
    // Each curseg contributes `cur_data_blkoff[type]` entries; HOT_DATA=0, WARM_DATA=#data, COLD_DATA=0.
    var summaryOff = 2 * SumJournalSize; // 1014
    foreach (var f in filePlan) {
      // For each WARM_DATA block: nid = file inode nid, ofs_in_node = 0..N-1.
      for (var j = 0; j < f.DataBlocks.Count; ++j) {
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(summaryOff), f.Nid);
        block[summaryOff + 4] = 1; // version
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(summaryOff + 5), (ushort)j); // ofs_in_node
        summaryOff += 7;

        // mkfs format wraps to next block when offset+SUMMARY_SIZE > 4096-SUM_FOOTER_SIZE.
        // For our small file counts this never triggers.
        if (summaryOff + 7 > BlockSize - SumFooterSize)
          break;
      }
    }

    // No footer entry is written here — the footer is at byte 4091..4095 of the data summary block,
    // but the compact-summary path treats that region as zero.
    return block;
  }

  // Build a node summary block (HOT_NODE / WARM_NODE / COLD_NODE).
  // Layout: f2fs_summary entries[512] (3584 bytes) + journal[507] + footer[5] = 4096.
  private static byte[] BuildNodeSummaryBlock(uint[] nids) {
    var block = new byte[BlockSize];
    // Node summaries: entries[i].nid = node id at block i (offset 0..3584-7).
    for (var i = 0; i < nids.Length && i < 512; ++i) {
      var off = i * 7;
      BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(off), nids[i]);
      block[off + 4] = 1; // version
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(off + 5), 0); // ofs_in_node
    }
    // journal at offset 3584 — leave zero (n_nats/n_sits = 0).
    // footer at offset 4091: entry_type = SUM_TYPE_NODE (1), check_sum (4 bytes) = 0.
    block[BlockSize - SumFooterSize] = 1; // SUM_TYPE_NODE
    return block;
  }

  // ==================================================================
  // NAT entry writer. NAT block layout: 455 entries × 9 bytes + 1 byte pad.
  // Each entry = { u8 version, __le32 ino, __le32 block_addr }.
  // ==================================================================
  private static void WriteNatEntry(byte[] disk, int natBlkAddr, uint nid, uint ino, uint blockAddr) {
    const int natEntriesPerBlock = 4095 / 9; // = 455 (kernel NAT_ENTRY_PER_BLOCK)
    var natBlock = (int)(nid / (uint)natEntriesPerBlock);
    var natIdx = (int)(nid % (uint)natEntriesPerBlock);
    var off = (natBlkAddr + natBlock) * BlockSize + natIdx * 9;
    disk[off] = 1; // version
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(off + 1), ino);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(off + 5), blockAddr);
  }

  // ==================================================================
  // SIT entry writer. Entry layout: { __le16 vblocks, u8 valid_map[64], __le64 mtime }.
  // 4096 / 74 = 55 entries per SIT block (with 26-byte trailing pad).
  // ==================================================================
  private static void WriteSitEntry(byte[] disk, int sitBlkAddr, int segno,
    ushort vblocks, ReadOnlySpan<byte> validMap, ulong mtime) {
    const int sitEntriesPerBlock = BlockSize / 74; // 55
    var sitBlock = segno / sitEntriesPerBlock;
    var sitIdx = segno % sitEntriesPerBlock;
    var off = (sitBlkAddr + sitBlock) * BlockSize + sitIdx * 74;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(off), vblocks);
    validMap.CopyTo(disk.AsSpan(off + 2, 64));
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(off + 66), mtime);
  }

  // ==================================================================
  // File inode (f2fs_inode). Regular file with direct block pointers.
  // ==================================================================
  private static void WriteRegularFileInode(byte[] disk, int off, uint ino, string name,
    int size, List<int> dataBlocks) {
    var s = disk.AsSpan(off, BlockSize);

    BinaryPrimitives.WriteUInt16LittleEndian(s[0..], 0x81A4); // i_mode: S_IFREG | 0644
    s[2] = 0;                                                 // i_advise
    s[3] = 0;                                                 // i_inline (no inline data/xattr)
    BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 0);      // i_uid
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0);      // i_gid
    BinaryPrimitives.WriteUInt32LittleEndian(s[12..], 1);     // i_links
    BinaryPrimitives.WriteUInt64LittleEndian(s[16..], (ulong)size);                  // i_size
    BinaryPrimitives.WriteUInt64LittleEndian(s[24..], (ulong)(1 + dataBlocks.Count)); // i_blocks (inode itself + data blocks).
    var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt64LittleEndian(s[32..], now); // i_atime
    BinaryPrimitives.WriteUInt64LittleEndian(s[40..], now); // i_ctime
    BinaryPrimitives.WriteUInt64LittleEndian(s[48..], now); // i_mtime
    BinaryPrimitives.WriteUInt32LittleEndian(s[56..], 0);   // i_atime_nsec
    BinaryPrimitives.WriteUInt32LittleEndian(s[60..], 0);   // i_ctime_nsec
    BinaryPrimitives.WriteUInt32LittleEndian(s[64..], 0);   // i_mtime_nsec
    BinaryPrimitives.WriteUInt32LittleEndian(s[68..], 0);   // i_generation
    BinaryPrimitives.WriteUInt32LittleEndian(s[72..], 0);   // i_current_depth
    BinaryPrimitives.WriteUInt32LittleEndian(s[76..], 0);   // i_xattr_nid
    BinaryPrimitives.WriteUInt32LittleEndian(s[80..], 0);   // i_flags
    BinaryPrimitives.WriteUInt32LittleEndian(s[84..], RootIno); // i_pino (parent = root)

    var nameBytes = Encoding.UTF8.GetBytes(name);
    var namelen = Math.Min(nameBytes.Length, 255);
    BinaryPrimitives.WriteUInt32LittleEndian(s[88..], (uint)namelen); // i_namelen
    nameBytes.AsSpan(0, namelen).CopyTo(s[92..]); // i_name[255]
    s[92 + 255] = 0; // i_dir_level

    // i_ext (f2fs_extent) at offset 348: { __le32 fofs, __le32 blk_addr, __le32 len } = 12 bytes — leave zero.

    // i_addr[923] starts at offset 360. With no F2FS_EXTRA_ATTR, ofs = 0.
    const int iAddrOff = 360;
    for (var i = 0; i < dataBlocks.Count; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(s[(iAddrOff + i * 4)..], (uint)dataBlocks[i]);

    // i_nid[5] at 360 + 923*4 = 4052. All zero for direct-pointer-only files.

    // node_footer (24 bytes) at block end (offset 4072..4095):
    //   nid(4) + ino(4) + flag(4) + cp_ver(8 packed) + next_blkaddr(4).
    var footerOff = BlockSize - 24;
    BinaryPrimitives.WriteUInt32LittleEndian(s[footerOff..], ino);       // nid
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 4)..], ino); // ino
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 8)..], 0);   // flag
    BinaryPrimitives.WriteUInt64LittleEndian(s[(footerOff + 12)..], 1UL); // cp_ver
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 20)..], 0);  // next_blkaddr
  }

  // ==================================================================
  // Root inode with inline dentry entries. Layout matches kernel:
  //   At i_addr[1] (offset 364): bitmap[24] + reserved[16] + dentry[192][11] + filename[192][8].
  // i_addr[0] is the "inline reserved" slot — must remain 0.
  // ==================================================================
  private static void WriteRootInodeInline(byte[] disk, int off, List<(uint Nid, string Name)> children) {
    var s = disk.AsSpan(off, BlockSize);

    BinaryPrimitives.WriteUInt16LittleEndian(s[0..], 0x41ED); // i_mode: S_IFDIR | 0755
    s[2] = 0;                                                 // i_advise
    s[3] = (byte)(F2fsInlineDentry | F2fsDataExist);          // i_inline (kernel: INLINE_DENTRY implies DATA_EXIST)
    BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 0);       // i_uid
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0);       // i_gid
    BinaryPrimitives.WriteUInt32LittleEndian(s[12..], 2);      // i_links (directory)
    // i_size: with inline_dentry, mkfs uses one block size (4096) for an empty root dentry block.
    BinaryPrimitives.WriteUInt64LittleEndian(s[16..], (ulong)BlockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(s[24..], 1UL);    // i_blocks (the inode itself counts).
    var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt64LittleEndian(s[32..], now);
    BinaryPrimitives.WriteUInt64LittleEndian(s[40..], now);
    BinaryPrimitives.WriteUInt64LittleEndian(s[48..], now);
    BinaryPrimitives.WriteUInt32LittleEndian(s[56..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[60..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[64..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[68..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[72..], 1);      // i_current_depth
    BinaryPrimitives.WriteUInt32LittleEndian(s[76..], 0);      // i_xattr_nid
    BinaryPrimitives.WriteUInt32LittleEndian(s[80..], 0);      // i_flags
    BinaryPrimitives.WriteUInt32LittleEndian(s[84..], 0);      // i_pino (root has no parent)
    BinaryPrimitives.WriteUInt32LittleEndian(s[88..], 0);      // i_namelen = 0 for root

    // i_addr starts at offset 360. i_addr[0] is the "inline reserved" slot — keep 0.
    // Inline dentry data starts at i_addr[1] (offset 364).
    const int inlineDataOffset = 360 + InlineReservedAddrSlots * 4; // 364
    var bitmapOff = inlineDataOffset;
    var dentryOff = inlineDataOffset + InlineDentryBitmapSize + InlineDentryReserved;
    var nameOff = dentryOff + NrInlineDentry * 11;

    var slot = 0;
    // ".":
    WriteDentrySlot(s, bitmapOff, dentryOff, nameOff, slot++, RootIno, ".", FtDir);
    // "..":
    WriteDentrySlot(s, bitmapOff, dentryOff, nameOff, slot++, RootIno, "..", FtDir);
    foreach (var (nid, name) in children)
      WriteDentrySlot(s, bitmapOff, dentryOff, nameOff, slot++, nid, name, FtRegFile);

    // Node footer at end of block.
    var footerOff = BlockSize - 24;
    BinaryPrimitives.WriteUInt32LittleEndian(s[footerOff..], RootIno);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 4)..], RootIno);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 8)..], 0);
    BinaryPrimitives.WriteUInt64LittleEndian(s[(footerOff + 12)..], 1UL);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 20)..], 0);
  }

  private static void WriteDentrySlot(Span<byte> s, int bitmapOff, int dentryBase,
    int nameBase, int slot, uint ino, string name, byte fileType) {
    s[bitmapOff + slot / 8] |= (byte)(1 << (slot % 8));

    var entryOff = dentryBase + slot * 11;
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var nameLen = (ushort)Math.Min(nameBytes.Length, SlotLen - 1); // single-slot fit
    var hash = F2fsNameHash(nameBytes.AsSpan(0, nameLen));

    BinaryPrimitives.WriteUInt32LittleEndian(s[entryOff..], hash);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(entryOff + 4)..], ino);
    BinaryPrimitives.WriteUInt16LittleEndian(s[(entryOff + 8)..], nameLen);
    s[entryOff + 10] = fileType;

    var fnOff = nameBase + slot * SlotLen;
    nameBytes.AsSpan(0, nameLen).CopyTo(s[fnOff..(fnOff + SlotLen)]);
  }

  // ==================================================================
  // Helpers
  // ==================================================================
  /// <summary>
  /// Sets a bit using F2FS's MSB-first ordering (matches kernel <c>f2fs_set_bit</c>):
  /// bit position N maps to <c>map[N/8] |= 1 &lt;&lt; (7 - N%8)</c>.
  /// </summary>
  private static void SetBit(byte[] map, int bit) => map[bit / 8] |= (byte)(1 << (7 - bit % 8));

  /// <summary>
  /// F2FS-specific CRC-32 (reflected IEEE polynomial 0xEDB88320, no final XOR,
  /// caller-supplied initial seed). Matches f2fs-tools <c>lib/libf2fs.c:f2fs_cal_crc32</c>.
  /// </summary>
  internal static uint F2fsCrc32(uint seed, ReadOnlySpan<byte> data) {
    const uint CrcPolyLe = 0xEDB88320u;
    var crc = seed;
    for (var i = 0; i < data.Length; i++) {
      crc ^= data[i];
      for (var j = 0; j < 8; j++)
        crc = (crc >> 1) ^ ((crc & 1) != 0 ? CrcPolyLe : 0u);
    }
    return crc;
  }

  /// <summary>
  /// F2FS filename hash — TEA-based mixer matching the kernel's <c>f2fs_dentry_hash</c>
  /// (see <c>fs/f2fs/hash.c</c> and f2fs-tools <c>lib/libf2fs.c:__f2fs_dentry_hash</c>).
  /// Returns 0 for "." and "..", otherwise computes 16-round TEA over zero-padded chunks.
  /// </summary>
  internal static uint F2fsNameHash(ReadOnlySpan<byte> name) {
    var len = name.Length;
    // Special case: "." and ".." get hash 0 per kernel convention.
    if (len <= 2 && name[0] == '.' && (len == 1 || name[1] == '.'))
      return 0;

    Span<uint> buf = stackalloc uint[4];
    buf[0] = 0x67452301u;
    buf[1] = 0xefcdab89u;
    buf[2] = 0x98badcfeu;
    buf[3] = 0x10325476u;

    Span<uint> input = stackalloc uint[8];
    var pos = 0;
    var remaining = len;
    while (true) {
      Str2HashBuf(name, pos, remaining, input, 4);
      TeaTransform(buf, input);
      pos += 16;
      if (remaining <= 16)
        break;
      remaining -= 16;
    }
    return buf[0]; // ~F2FS_HASH_COL_BIT (bit 63) is no-op for 32-bit values.
  }

  private static void Str2HashBuf(ReadOnlySpan<byte> msg, int pos, int len, Span<uint> buf, int num) {
    var pad = (uint)len | ((uint)len << 8);
    pad |= pad << 16;

    var val = pad;
    if (len > num * 4)
      len = num * 4;
    var bufIdx = 0;
    for (var i = 0; i < len; i++) {
      if (i % 4 == 0)
        val = pad;
      val = (val << 8) + msg[pos + i];
      if (i % 4 == 3) {
        buf[bufIdx++] = val;
        val = pad;
        num--;
      }
    }
    if (--num >= 0)
      buf[bufIdx++] = val;
    while (--num >= 0)
      buf[bufIdx++] = pad;
  }

  private static void TeaTransform(Span<uint> buf, ReadOnlySpan<uint> input) {
    const uint Delta = 0x9E3779B9u;
    uint sum = 0;
    var b0 = buf[0];
    var b1 = buf[1];
    var a = input[0];
    var b = input[1];
    var c = input[2];
    var d = input[3];
    for (var n = 16; n > 0; --n) {
      sum += Delta;
      b0 += ((b1 << 4) + a) ^ (b1 + sum) ^ ((b1 >> 5) + b);
      b1 += ((b0 << 4) + c) ^ (b0 + sum) ^ ((b0 >> 5) + d);
    }
    buf[0] += b0;
    buf[1] += b1;
  }
}
