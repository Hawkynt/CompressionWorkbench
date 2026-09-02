#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
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
///   <item><description>Segment 7: SSA (Segment Summary Area) — one f2fs_summary_block per main segment.</description></item>
///   <item><description>Segments 8+: Main area, laid out as contiguous multi-segment regions.</description></item>
/// </list>
/// <para>
/// The main area holds, in order, the populated regions sized to their actual block counts —
/// HOT_NODE (root inode), WARM_NODE (subdirectory + file inodes), HOT_DATA (directory dentry
/// data blocks), WARM_DATA (file data blocks) — followed by six reserved, empty "current"
/// segments (one per <c>CURSEG_*</c> type). Every written block therefore lives in an
/// ordinary, non-current segment whose owner is recorded in the on-disk SSA, and the
/// checkpoint's <c>cur_*_blkoff</c> are all zero. This keeps fsck's two summary sources (the
/// checkpoint for current segments, the SSA for everything else) from ever disagreeing.
/// </para>
/// <para>
/// Small directories use inline dentries (<c>F2FS_INLINE_DENTRY</c>) embedded in the inode at
/// <c>i_addr[1]</c> (offset 364). Larger directories spill into regular 4 KiB dentry data
/// blocks organised by the kernel's multi-level hash-bucket scheme (see
/// <c>PlanHashBucketDentries</c>): a name lands in bucket <c>hash % dir_buckets(level)</c> at
/// the lowest level whose target bucket has room, so <c>fsck.f2fs</c>'s
/// <c>f2fs_check_dirent_position</c> agrees with where each name is stored.
/// </para>
/// <para>
/// SIT entries (written for every main segment) encode the valid-block count (low 10 bits)
/// and the segment type (high 6 bits); the SSA footer entry_type classifies each segment as
/// node or data. fsck cross-checks all of these against the reachable inode/dentry tree.
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

  // Block addresses per direct node block and node ids per indirect node block:
  // (4096 - 24 footer) / 4 = 1018 (kernel DEF_ADDRS_PER_BLOCK / NIDS_PER_BLOCK).
  internal const int AddrsPerBlock = 1018;
  internal const int NidsPerBlock = 1018;

  // i_nid[5] at 360 + 923*4: two direct nodes, two indirect nodes, one double-indirect.
  internal const int INidOffset = 360 + AddrsPerInode * 4; // 4052

  // ofs_of_node values the kernel's get_node_path() assigns (fs/f2fs/node.c). They
  // identify a node's place in the file's tree and go in the footer's flag field.
  internal const int NoffsetDir1 = 1;
  internal const int NoffsetDir2 = 2;
  internal const int NoffsetInd1 = 3;
  internal const int NoffsetInd2 = 4 + NidsPerBlock;
  internal const int NoffsetDind = 5 + NidsPerBlock * 2;

  // The footer's flag field carries ofs_of_node shifted left by OFFSET_BIT_SHIFT.
  internal const int OffsetBitShift = 3;

  /// <summary>Largest file the inode's pointers and node tree can address, in blocks.</summary>
  internal const long MaxFileBlocks =
    AddrsPerInode
    + 2L * AddrsPerBlock
    + 2L * NidsPerBlock * AddrsPerBlock
    + (long)NidsPerBlock * NidsPerBlock * AddrsPerBlock;

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
  internal const int SegMain = SegSsa + 1; // 8 — with the minimum one-segment SSA.

  /// <summary>
  /// Segments the Segment Summary Area needs: one 4 KiB summary block per main-area
  /// segment. A single segment covers 512 of them, i.e. a 1 GB main area — past that
  /// the SSA has to grow or the summaries for the tail segments have nowhere to live.
  /// </summary>
  private static int SsaSegmentsFor(int totalSegments)
    => Math.Max(1, (totalSegments + BlocksPerSeg - 1) / BlocksPerSeg);

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

  // Minimum total segment count the writer can build. The main area must hold the populated
  // node/data regions (HOT_NODE + WARM_NODE + HOT_DATA + WARM_DATA, up to one segment each in
  // the smallest case) PLUS the six reserved current segments, PLUS slack. With the metadata
  // area at SegMain=8 and one segment of pre-roll padding, segment_count_main = total - 9, so
  // we need total >= 9 (meta+pad) + 10 (4 worst-case small regions + 6 cursegs) + slack.
  /// <summary>
  /// The smallest volume this writes: 32 segments, 64 MiB.
  /// </summary>
  /// <remarks>
  /// The floor used to be 20 segments — 40 MiB — which is under what F2FS
  /// itself accepts: mkfs.f2fs refuses to format a device that size, and the
  /// kernel refuses to mount one, silently, because it never gets far enough
  /// to have anything to say. Volumes smaller than this are padded up rather
  /// than written unmountable.
  /// </remarks>
  internal const int MinTotalSegments = 32;

  /// <summary>
  /// The smallest total segment count <see cref="Build(int)"/> accepts. Equals the metadata
  /// area, the populated regions and the six reserved current segments plus slack
  /// (20 segments = 40 MiB).
  /// </summary>
  public const int MinimumSegmentCount = MinTotalSegments;

  private readonly List<(string Name, byte[] Data, long? StreamingSize, Func<Stream>? StreamOpener)> _files = [];
  // Empty by default, as mkfs.f2fs leaves it unless a label is asked for.
  private string _volumeLabel = "";

  /// <summary>
  /// Streaming-allocations side-effect: when non-null, every streaming entry's
  /// (firstDataBlock, blockCount, size, opener) is appended for use by
  /// <see cref="BuildToStreaming"/>'s post-stream pass. ByteOffset is computed
  /// from the first WARM_DATA block × <see cref="BlockSize"/>. When null, the
  /// writer behaves identically to before.
  /// </summary>
  private List<(long ByteOffset, long Size, Func<Stream> Opener)>? _streamingSink;

  /// <summary>
  /// Sets the UTF-16 volume label stored in the superblock's <c>volume_name</c> field.
  /// Empty or null leaves the default label. F2FS allows up to 512 UTF-16 code units.
  /// </summary>
  /// <param name="label">The desired volume label.</param>
  public void SetVolumeLabel(string? label) {
    if (!string.IsNullOrEmpty(label))
      this._volumeLabel = label;
  }

  /// <summary>
  /// Performs the add file operation.
  /// </summary>
public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    EnsureFileFits(name, data.LongLength);
    this._files.Add((name, data, null, null));
  }

  /// <summary>
  /// Adds a streaming file: <paramref name="size"/> drives the WARM_DATA block
  /// allocation + inode sizing in pass 1; bytes are pulled from
  /// <paramref name="openStream"/> in pass 2 of <see cref="BuildToStreaming"/>.
  /// Never buffered as <c>byte[]</c>. F2FS never stores file contents inline
  /// (only directory dentries are inline), so every file — regardless of size —
  /// is laid out into ordinary WARM_DATA blocks and is streamable.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "size must be >= 0.");
    EnsureFileFits(name, size);
    this._files.Add((name, System.Array.Empty<byte>(), size, openStream));
  }

  /// <summary>Rejects a file the inode's direct pointers and node tree cannot address.</summary>
  private static void EnsureFileFits(string name, long size) {
    var blocks = (size + BlockSize - 1) / BlockSize;
    if (blocks > MaxFileBlocks)
      throw new InvalidOperationException(
        $"F2FS: '{name}' needs {blocks:N0} blocks; the inode's node tree addresses {MaxFileBlocks:N0}.");
  }

  /// <summary>
  /// Performs the build operation.
  /// </summary>
public byte[] Build(int totalSegments = DefaultSegmentCount) {
    var image = this.BuildCore(totalSegments);
    if (image.TotalBytes > Array.MaxLength)
      throw new IOException(
        $"F2FS: a {image.TotalBytes:N0}-byte image exceeds the array limit; use BuildToStreaming.");
    return image.Materialise();
  }

  private SparseBlockImage BuildCore(int totalSegments = DefaultSegmentCount) {
    if (totalSegments < MinTotalSegments)
      throw new ArgumentOutOfRangeException(nameof(totalSegments),
        $"F2FS image needs at least {MinTotalSegments} segments (need 6 active main segments + slack).");

    var totalBlocks = totalSegments * BlocksPerSeg;
    // The image is sparse: only blocks actually written are materialised, so a
    // multi-gigabyte volume costs its metadata rather than its full extent.
    var disk = new SparseBlockImage(BlockSize, (long)totalBlocks * BlockSize);

    // The SSA grows with the main area; everything after it shifts along.
    var ssaSegs = SsaSegmentsFor(totalSegments);
    var segMain = SegSsa + ssaSegs;

    // ---- Plan main-area allocations ----
    // mkfs places HOT_NODE at main-relative seg 0, root inode at block 0 of that segment.
    // cur_seg values are RELATIVE to main_blkaddr (i.e. main-relative segno).
    var mainStart = segMain * BlocksPerSeg;

    // ---- Section / segment geometry ----
    // mkfs reserves segment 0 (one segment of pre-roll padding before segment0_blkaddr=512).
    // segment_count counts segments from segment0 onwards.
    var countedSegments = totalSegments - 1;
    var segmentCountMain = countedSegments - (segMain - SegCp); // = 31 - 7 = 24 for default.
    // section_count covers only the MAIN area (matches mkfs.f2fs); using the
    // total segment count made it 31 vs main's 24, so the kernel rejected the
    // superblock with "Invalid segment/section count (31, 31 x 1)".
    var totalSections = segmentCountMain / SegsPerSec;
    var totalZones = totalSections / SecsPerZone;

    // ---- Build the directory tree from slash-separated names ----
    // The root directory keeps RootIno; every intermediate path component becomes a
    // subdirectory inode with its own nid. Files become regular-file inodes. Each
    // directory names its direct children either with inline dentries (small dirs) or
    // with regular dentry data blocks (large dirs), so the reader's recursive walk
    // reconstructs the exact nested path.
    uint nextNid = RootIno + 1; // 4, 5, 6, ...

    var root = new DirPlan { Nid = RootIno, InodeBlock = -1 };
    var filePlan = new List<FilePlan>();
    var subdirPlan = new List<DirPlan>();

    foreach (var (name, data, streamingSize, streamOpener) in this._files) {
      var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0)
        continue;

      // Descend/create the directory chain for every component except the last.
      var dir = root;
      for (var p = 0; p < parts.Length - 1; ++p) {
        var component = parts[p];
        if (!dir.SubDirs.TryGetValue(component, out var child)) {
          child = new DirPlan {
            Nid = nextNid++,
            InodeBlock = -1,
            ParentNid = dir.Nid,
          };
          dir.SubDirs.Add(component, child);
          dir.Children.Add((child.Nid, component, FtDir));
          subdirPlan.Add(child);
        }
        dir = child;
      }

      // The final component is the file itself.
      var fileName = parts[^1];
      var fileNid = nextNid++;
      filePlan.Add(new FilePlan {
        Nid = fileNid,
        Name = fileName,
        Data = data,
        StreamingSize = streamingSize,
        StreamOpener = streamOpener,
        InodeBlock = -1,
        DataBlocks = [],
        ParentNid = dir.Nid,
      });
      dir.Children.Add((fileNid, fileName, FtRegFile));
    }

    // A file's first 923 blocks are addressed by i_addr[]; the rest hang off the
    // five node pointers in i_nid[] — two direct nodes, two indirect, one
    // double-indirect. Plan those node blocks now so the layout below can
    // reserve room for them alongside the inodes.
    foreach (var f in filePlan) {
      var blocks = f.EffectiveLength == 0 ? 0 : (int)((f.EffectiveLength + BlockSize - 1) / BlockSize);
      f.InodeDirectCount = Math.Min(blocks, AddrsPerInode);
      PlanNodeTree(f, blocks, ref nextNid);
    }

    // Decide each directory's dentry storage: inline when the children (plus "." and
    // "..") fit the inline slot count, otherwise regular dentry data blocks laid out per
    // the F2FS multi-level hash-bucket scheme (fs/f2fs/dir.c).
    // The root is the one directory that never gets inline dentries. The kernel reads a
    // mounted inode's block count as the recorded one minus the inode itself, and
    // f2fs_fill_super refuses the volume when the root's comes out zero — which is
    // exactly what an inline root records, since it owns no data block at all. That
    // refusal carries no message, so the volume simply fails to mount; mkfs.f2fs gives
    // the root a real dentry block for the same reason.
    const int dotsSlots = 2;
    var allDirs = subdirPlan.Prepend(root).ToList();
    foreach (var d in allDirs) {
      var slots = dotsSlots + d.Children.Sum(c => DentrySlotsFor(c.Name));
      if (d.Nid != RootIno && slots <= NrInlineDentry) {
        d.UsesInlineDentry = true;
      } else {
        d.UsesInlineDentry = false;
        PlanHashBucketDentries(d);
      }
    }

    // ---- Lay out the main area dynamically as contiguous multi-segment regions ----
    //   HOT_NODE   : root inode (1 block)
    //   WARM_NODE  : subdirectory + file inodes (one block each)
    //   COLD_NODE  : empty (kept type-tagged)
    //   HOT_DATA   : directory dentry data blocks (non-inline dirs)
    //   WARM_DATA  : file data blocks
    //   COLD_DATA  : empty (kept type-tagged)
    // Each region spans ceil(blocks / BlocksPerSeg) segments so it can hold far more
    // than a single segment's worth of blocks.
    var fileNodeBlockCount = filePlan.Sum(f => f.Nodes.Count);
    var warmNodeBlockCount = subdirPlan.Count + filePlan.Count + fileNodeBlockCount;
    var dentryBlockTotal = allDirs.Sum(d => d.DentryLayout.Count);
    var dataBlockTotal = filePlan.Sum(f => f.EffectiveLength == 0
      ? 0 : (int)((f.EffectiveLength + BlockSize - 1) / BlockSize));

    // Each populated region spans as many segments as its block count needs. The root
    // inode lives alone in HOT_NODE; subdir/file inodes in WARM_NODE; dentry blocks in
    // HOT_DATA; file data in WARM_DATA. COLD_NODE/COLD_DATA hold nothing.
    var hotNodeSegs = 1;
    var warmNodeSegs = Math.Max(1, (warmNodeBlockCount + BlocksPerSeg - 1) / BlocksPerSeg);
    var hotDataSegs = dentryBlockTotal == 0 ? 0 : (dentryBlockTotal + BlocksPerSeg - 1) / BlocksPerSeg;
    var warmDataSegs = dataBlockTotal == 0 ? 0 : (dataBlockTotal + BlocksPerSeg - 1) / BlocksPerSeg;

    var hotNodeSegStart = 0;
    var warmNodeSegStart = hotNodeSegStart + hotNodeSegs;
    var hotDataSegStart = warmNodeSegStart + warmNodeSegs;
    var warmDataSegStart = hotDataSegStart + hotDataSegs;
    var populatedMainSegs = warmDataSegStart + warmDataSegs;

    // F2FS keeps one "current" (open-for-append) segment per CURSEG_* type. fsck reads the
    // segment-summary entries for blocks in a current segment from the checkpoint, and for
    // every other block from the on-disk SSA. To keep the two sources from disagreeing we
    // place ALL written blocks into ordinary (non-current) segments — fully described by the
    // SSA — and reserve six fresh, EMPTY current segments after them (blkoff 0). The
    // checkpoint then describes six empty cursegs, which is trivially consistent.
    var cursegHotDataSeg = populatedMainSegs + 0;
    var cursegWarmDataSeg = populatedMainSegs + 1;
    var cursegColdDataSeg = populatedMainSegs + 2;
    var cursegHotNodeSeg = populatedMainSegs + 3;
    var cursegWarmNodeSeg = populatedMainSegs + 4;
    var cursegColdNodeSeg = populatedMainSegs + 5;
    var usedMainSegs = populatedMainSegs + 6;

    if (usedMainSegs > segmentCountMain)
      throw new InvalidOperationException(
        $"F2FS writer: payload needs {usedMainSegs} main segments but only {segmentCountMain} are available; "
        + "increase the total segment count.");

    var warmNodeBlkBase = mainStart + warmNodeSegStart * BlocksPerSeg;
    var hotDataBlkBase = mainStart + hotDataSegStart * BlocksPerSeg;
    var warmDataBlkBase = mainStart + warmDataSegStart * BlocksPerSeg;

    // Per-block segment-summary entries, keyed by absolute block address: each tells fsck
    // which node owns the block and (for data) the block's index within that node. These
    // populate the on-disk SSA so fsck can validate every reachable node/data block.
    var blockSummaries = new Dictionary<int, BlockSummary>();

    // Root inode lives at HOT_NODE block 0.
    var rootInodeBlock = mainStart + hotNodeSegStart * BlocksPerSeg + 0;
    root.InodeBlock = rootInodeBlock;
    blockSummaries[rootInodeBlock] = new BlockSummary(RootIno, 0, IsNode: true);

    // Assign WARM_NODE inode blocks (subdirectory inodes first, then file inodes). A node
    // block's summary records its own nid; ofs_in_node is 0 for an inode block.
    var nextWarmNodeBlk = 0;
    foreach (var d in subdirPlan) {
      d.InodeBlock = warmNodeBlkBase + nextWarmNodeBlk++;
      blockSummaries[d.InodeBlock] = new BlockSummary(d.Nid, 0, IsNode: true);
    }
    foreach (var f in filePlan) {
      f.InodeBlock = warmNodeBlkBase + nextWarmNodeBlk++;
      blockSummaries[f.InodeBlock] = new BlockSummary(f.Nid, 0, IsNode: true);
    }

    // The file's direct/indirect node blocks sit alongside its inode. A node
    // block's summary records its own nid, like an inode block's does.
    var fileNodeBlocks = new List<(uint Nid, uint Ino, int Block)>();
    foreach (var f in filePlan)
      foreach (var node in f.Nodes) {
        node.Block = warmNodeBlkBase + nextWarmNodeBlk++;
        blockSummaries[node.Block] = new BlockSummary(node.Nid, 0, IsNode: true);
        fileNodeBlocks.Add((node.Nid, f.Nid, node.Block));
      }

    // Assign HOT_DATA dentry blocks to every non-inline directory. Each used logical block
    // index (pgofs) gets a physical block; the summary records the owning directory inode
    // and the block's logical index (ofs_in_node = pgofs).
    var nextHotDataBlk = 0;
    foreach (var d in allDirs) {
      if (d.UsesInlineDentry)
        continue;
      foreach (var pgofs in d.DentryLayout.Keys.OrderBy(k => k)) {
        var blk = hotDataBlkBase + nextHotDataBlk++;
        d.DentryBlocks[pgofs] = blk;
        blockSummaries[blk] = new BlockSummary(d.Nid, (ushort)pgofs, IsNode: false);
      }
    }

    // Assign WARM_DATA blocks to each file; the summary records the owning file inode and
    // the block's index within that inode.
    var nextWarmDataBlk = 0;
    foreach (var f in filePlan) {
      var blocksNeeded = f.EffectiveLength == 0 ? 0 : (int)((f.EffectiveLength + BlockSize - 1) / BlockSize);
      for (var i = 0; i < blocksNeeded; ++i) {
        var blk = warmDataBlkBase + nextWarmDataBlk++;
        f.DataBlocks.Add(blk);
        blockSummaries[blk] = new BlockSummary(f.Nid, (ushort)i, IsNode: false);
      }
    }

    // Warm-node inodes are the subdirectory inodes plus the regular-file inodes, laid out
    // in allocation order (the order in which their blocks were assigned above).
    var warmNodeInodes = new List<(uint Nid, int Block)>();
    warmNodeInodes.AddRange(subdirPlan.Select(d => (d.Nid, d.InodeBlock)));
    warmNodeInodes.AddRange(filePlan.Select(f => (f.Nid, f.InodeBlock)));
    warmNodeInodes.Sort((a, b) => a.Block.CompareTo(b.Block));

    var warmNodeCount = warmNodeInodes.Count + fileNodeBlocks.Count;
    var validInodeCount = 1 + warmNodeInodes.Count;       // root + warm-node inodes.
    var validNodeCount = validInodeCount + fileNodeBlocks.Count; // plus the files' node blocks.
    var totalDataBlocks = dataBlockTotal + dentryBlockTotal;
    var validBlockCount = validNodeCount + totalDataBlocks;

    // ---- 1) Write file data and file inodes (regular files in WARM_NODE/WARM_DATA) ----
    foreach (var f in filePlan) {
      if (f.StreamOpener != null) {
        // Streaming entry: leave the WARM_DATA blocks zero here; BuildToStreaming
        // post-fills them from the source in 64 KB chunks. The data blocks are
        // contiguous (allocated in order above), so the file's bytes occupy
        // [firstDataBlock*BlockSize, +EffectiveLength). Block tail past
        // EffectiveLength stays sparse-zero from the disk init.
        if (this._streamingSink != null && f.DataBlocks.Count > 0 && f.EffectiveLength > 0)
          this._streamingSink.Add(((long)f.DataBlocks[0] * BlockSize, f.EffectiveLength, f.StreamOpener));
      } else {
        var remaining = f.Data.Length;
        for (var i = 0; i < f.DataBlocks.Count; ++i) {
          var len = Math.Min(BlockSize, remaining);
          f.Data.AsSpan(i * BlockSize, len).CopyTo(disk.Block(f.DataBlocks[i]));
          remaining -= len;
        }
      }
      // A direct node's addresses are the slice of the file's data blocks it owns.
      foreach (var node in f.Nodes) {
        if (node.IsIndirect) continue;
        for (var i = 0; i < node.DataCount; ++i)
          node.Addresses.Add(f.DataBlocks[node.FirstDataIndex + i]);
      }

      WriteRegularFileInode(disk, f.InodeBlock, f.Nid, f.Name, f.EffectiveLength, f, f.ParentNid);
      foreach (var node in f.Nodes)
        WriteNodeBlock(disk, node, f.Nid);
    }

    // ---- 2) Write directory inodes (root + subdirectories). Small directories keep
    //         inline dentries; large directories spill into regular dentry data blocks. ----
    foreach (var d in allDirs) {
      var isRoot = d.Nid == RootIno;
      var dirParent = isRoot ? RootIno : d.ParentNid;
      if (d.UsesInlineDentry)
        WriteDirectoryInodeInline(disk, d.InodeBlock, d.Nid, parentNid: dirParent,
          d.Children, isRoot: isRoot);
      else
        WriteDirectoryInodeWithDataBlocks(disk, d.InodeBlock, d.Nid, parentNid: dirParent,
          d.Children, d, isRoot: isRoot);
    }

    // ---- 3) Write NAT entries on disk (file/dir entries; root NAT also lives in journal) ----
    var natBlkAddr = SegNat * BlocksPerSeg;
    // mkfs uses block_addr=1 for reserved node/meta inodes (a sentinel — a real block address would be 0 or main).
    WriteNatEntry(disk, natBlkAddr, NodeIno, NodeIno, 1u);
    WriteNatEntry(disk, natBlkAddr, MetaIno, MetaIno, 1u);
    WriteNatEntry(disk, natBlkAddr, RootIno, RootIno, (uint)rootInodeBlock);
    foreach (var (nid, block) in warmNodeInodes)
      WriteNatEntry(disk, natBlkAddr, nid, nid, (uint)block);
    // A non-inode node block's NAT entry names the file it belongs to, not itself.
    foreach (var (nid, ino, block) in fileNodeBlocks)
      WriteNatEntry(disk, natBlkAddr, nid, ino, (uint)block);

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

    // Marks `count` consecutive valid blocks starting at the given region's first
    // segment, spilling across as many segments as needed and tagging each with `type`.
    void MarkRegion(int segStart, int segSpan, int count, int type) {
      for (var s = 0; s < segSpan; ++s)
        sitTypes[segStart + s] = type;
      for (var b = 0; b < count; ++b) {
        var seg = segStart + b / BlocksPerSeg;
        SetBit(sitMaps[seg], b % BlocksPerSeg);
        ++sitVblocks[seg];
      }
    }

    // Populated regions: typed to match the content they hold and with their valid-block
    // bitmaps reflecting the blocks actually written.
    MarkRegion(hotNodeSegStart, hotNodeSegs, 1, CursegHotNode);                  // root inode.
    MarkRegion(warmNodeSegStart, warmNodeSegs, warmNodeCount, CursegWarmNode);   // subdir + file inodes.
    MarkRegion(hotDataSegStart, hotDataSegs, dentryBlockTotal, CursegHotData);   // dentry data blocks.
    MarkRegion(warmDataSegStart, warmDataSegs, dataBlockTotal, CursegWarmData);  // file data blocks.

    // The six reserved current segments are empty but must carry their CURSEG_* type so
    // the checkpoint's cur_*_segno/blkoff and the SIT type agree.
    sitTypes[cursegHotDataSeg] = CursegHotData;
    sitTypes[cursegWarmDataSeg] = CursegWarmData;
    sitTypes[cursegColdDataSeg] = CursegColdData;
    sitTypes[cursegHotNodeSeg] = CursegHotNode;
    sitTypes[cursegWarmNodeSeg] = CursegWarmNode;
    sitTypes[cursegColdNodeSeg] = CursegColdNode;

    // Write SIT entries for every main segment to disk. fsck reads the on-disk SIT for any
    // segment not carried in the checkpoint's SIT journal; we write them all so the typed,
    // valid-block-counted entries are authoritative.
    for (var seg = 0; seg < segmentCountMain; ++seg)
      WriteSitEntry(disk, sitBlkAddr, seg,
        (ushort)((sitTypes[seg] << 10) | (sitVblocks[seg] & 0x3FF)), sitMaps[seg], nowSecs);

    // ---- Write the on-disk Segment Summary Area (one f2fs_summary_block per main segment). ----
    var ssaBlkAddr = SegSsa * BlocksPerSeg;
    WriteSegmentSummaryArea(disk, ssaBlkAddr, segmentCountMain, mainStart, sitTypes, blockSummaries);

    // ---- 5) Write checkpoint pack (both copies). ----
    var cpBlkAddr = SegCp * BlocksPerSeg;
    var freeSegments = segmentCountMain - usedMainSegs; // segments not occupied by node/data regions.
    // user_block_count formula from mkfs: (free + 6 - ovp) * blocks_per_seg.
    // Need user_block_count < segment_count_main * blocks_per_seg per fsck sanity check.
    var rsvdSegments = 2u;        // fsmeta needs >= 9 (2+2+2+1+rsvd).
    var ovpSegments = 6u;         // > rsvd; modest reservation.
    var userBlockCount = ((ulong)(freeSegments + usedMainSegs) - ovpSegments) * BlocksPerSeg;

    // The six current segments (one per CURSEG_* type), as main-relative segment numbers.
    // Data types come first (HOT=0, WARM=1, COLD=2), then node types (HOT=3, WARM=4, COLD=5),
    // matching the kernel CURSEG_* enum ordering used throughout the checkpoint.
    int[] curSegnos = [
      cursegHotDataSeg, cursegWarmDataSeg, cursegColdDataSeg,
      cursegHotNodeSeg, cursegWarmNodeSeg, cursegColdNodeSeg,
    ];

    var cpArgs = new CheckpointArgs {
      ValidBlockCount = (ulong)validBlockCount,
      ValidInodeCount = (uint)validInodeCount,
      ValidNodeCount = (uint)validNodeCount,
      NextFreeNid = nextNid,
      UserBlockCount = userBlockCount,
      CountedSegments = (uint)countedSegments,
      FreeSegments = (uint)freeSegments,
      RsvdSegments = rsvdSegments,
      OvpSegments = ovpSegments,
      CurSegnos = curSegnos,
      NowSecs = nowSecs,
    };

    // Copy CP pack to slot 0 (segment 1) with the newer version (1).
    // mkfs.f2fs stamps a fresh volume with an arbitrary checkpoint version rather
    // than starting at one, and fsck prints it. Always writing the same number is a
    // fingerprint no volume the tool made ever carries; only the ordering of the
    // two packs matters, so the pair moves together.
    var checkpointVersion = NewCheckpointVersion();
    var cpPack = BuildCheckpointPack(cpArgs, checkpointVersion);
    disk.Write((long)cpBlkAddr * BlockSize, cpPack);
    // Copy CP pack to slot 1 (segment 2). mkfs uses checkpoint_ver 0 for the unused pack so
    // the newer one (ver 1) is selected.
    var cpPack2 = BuildCheckpointPack(cpArgs, checkpointVersion - 1);
    disk.Write((long)(cpBlkAddr + BlocksPerSeg) * BlockSize, cpPack2);

    // ---- 6) Write both superblock copies ----
    var seg0BlkAddr = (uint)cpBlkAddr; // mkfs sets segment0_blkaddr = cp_blkaddr.
    WriteSuperblock(disk, blockOffset: 0, totalBlocks: totalBlocks, totalSegments: (uint)countedSegments,
      totalSections: (uint)totalSections, totalZones: (uint)totalZones, seg0BlkAddr: seg0BlkAddr,
      cpBlkAddr: (uint)cpBlkAddr,
      sitBlkAddr: (uint)sitBlkAddr, natBlkAddr: (uint)natBlkAddr,
      ssaBlkAddr: (uint)ssaBlkAddr, mainBlkAddr: (uint)mainStart, ssaSegments: (uint)ssaSegs,
      segmentCountMain: (uint)segmentCountMain, volumeLabel: this._volumeLabel);
    WriteSuperblock(disk, blockOffset: BlockSize, totalBlocks: totalBlocks, totalSegments: (uint)countedSegments,
      totalSections: (uint)totalSections, totalZones: (uint)totalZones, seg0BlkAddr: seg0BlkAddr,
      cpBlkAddr: (uint)cpBlkAddr,
      sitBlkAddr: (uint)sitBlkAddr, natBlkAddr: (uint)natBlkAddr,
      ssaBlkAddr: (uint)ssaBlkAddr, mainBlkAddr: (uint)mainStart, ssaSegments: (uint)ssaSegs,
      segmentCountMain: (uint)segmentCountMain, volumeLabel: this._volumeLabel);

    return disk;
  }

  /// <summary>
  /// Builds an F2FS image sized to just hold the added files, plus metadata overhead and
  /// roughly ten percent headroom, clamped to <see cref="MinimumSegmentCount"/> (40 MiB).
  /// </summary>
  /// <returns>The generated image bytes.</returns>
  public byte[] BuildAutoSized() => this.Build(this.ComputeAutoSegmentCount());

  /// <summary>
  /// Computes the total segment count needed to hold all added files: the metadata area, the
  /// payload's node/data/dentry regions sized to the actual block counts, the six reserved
  /// current segments, plus ~10% headroom — clamped to <see cref="MinimumSegmentCount"/>.
  /// </summary>
  /// <returns>The total segment count to pass to <see cref="Build(int)"/>.</returns>
  public int ComputeAutoSegmentCount() {
    // Estimate node/data/dentry blocks conservatively without running the full layout: every
    // file is one inode (WARM_NODE) plus ceil(size/block) data blocks (WARM_DATA); each path
    // component beyond the file is at most one subdirectory inode; dentry storage is bounded by
    // the number of children. Over-estimating only adds slack.
    var dataBlocks = this._files.Sum(f => {
      var len = f.StreamingSize ?? f.Data.Length;
      return len == 0 ? 0L : (len + BlockSize - 1) / BlockSize;
    });
    // Every file is an inode plus one node block per 1018 blocks past the 923 its
    // inode addresses, plus the indirect nodes over those.
    var indirectBlocks = this._files.Sum(f => {
      var len = f.StreamingSize ?? f.Data.Length;
      var blocks = len == 0 ? 0L : (len + BlockSize - 1) / BlockSize;
      var rest = Math.Max(0, blocks - AddrsPerInode);
      var directNodes = (rest + AddrsPerBlock - 1) / AddrsPerBlock;
      return directNodes + (directNodes + NidsPerBlock - 1) / NidsPerBlock + 1;
    });
    var nodeBlocks = this._files.Count * 2L + 1 + indirectBlocks; // file + dir inodes + node tree.
    var dentryBlocks = Math.Max(1L, this._files.Count / 100); // generous bound on hash-bucket blocks.

    var dataSegs = (dataBlocks + BlocksPerSeg - 1) / BlocksPerSeg;
    var nodeSegs = (nodeBlocks + BlocksPerSeg - 1) / BlocksPerSeg;
    var dentrySegs = (dentryBlocks + BlocksPerSeg - 1) / BlocksPerSeg;

    // Metadata area (SegMain) + one pre-roll padding segment + payload regions + 6 cursegs.
    var baseSegments = SegMain + 1 + nodeSegs + dataSegs + dentrySegs + 6;
    var withHeadroom = baseSegments + Math.Max(1, baseSegments / 10);
    // The SSA grows with the volume, which pushes the main area along; account for
    // the segments it takes beyond the single one SegMain already allows for.
    var total = Math.Max(MinTotalSegments, withHeadroom);
    return (int)(total + SsaSegmentsFor((int)total) - 1);
  }

  /// <summary>
  /// Writes the to to the supplied output.
  /// </summary>
public void WriteTo(Stream output) {
    var bytes = this.Build();
    output.Write(bytes, 0, bytes.Length);
  }

  /// <summary>
  /// Two-pass streaming Build: pass 1 derives segment geometry from the declared
  /// sizes of <see cref="AddStreamingFile"/> entries and emits the full metadata
  /// image (checkpoint, SIT, NAT, SSA, superblocks, inodes, dentries) with the
  /// streaming entries' WARM_DATA blocks left zero; pass 2 seeks to each entry's
  /// first data-block byte offset and streams its bytes from the factory in
  /// 64 KB chunks. The byte output is identical to <see cref="Build(int)"/> for
  /// the same inputs — only WHERE the file-data bytes come from differs. F2FS
  /// has no per-block content checksum (its CRC-32 covers only the checkpoint
  /// header), so streaming the data blocks in afterward is byte-safe.
  /// </summary>
  /// <param name="output">A writable, seekable target stream.</param>
  /// <param name="totalSegments">Total segment count (0 = auto-size from inputs).</param>
  public void BuildToStreaming(Stream output, int totalSegments = 0) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildToStreaming requires a writable, seekable stream.", nameof(output));

    var sink = new List<(long ByteOffset, long Size, Func<Stream> Opener)>();
    this._streamingSink = sink;
    SparseBlockImage disk;
    try {
      disk = this.BuildCore(totalSegments > 0 ? totalSegments : this.ComputeAutoSegmentCount());
    } finally {
      this._streamingSink = null;
    }

    output.Position = 0;
    disk.WriteTo(output);
    output.SetLength(disk.TotalBytes);

    // Pass 2: stream each entry's bytes into its first WARM_DATA block. The data
    // blocks of a file are contiguous (allocated in order), so a single forward
    // write of exactly `size` bytes covers them.
    var buf = new byte[64 * 1024];
    foreach (var (byteOffset, size, opener) in sink) {
      if (size <= 0) continue;
      if (byteOffset < 0 || byteOffset >= output.Length) continue;
      output.Position = byteOffset;
      using var src = opener();
      long copied = 0;
      while (copied < size) {
        var want = (int)Math.Min(buf.Length, size - copied);
        var n = src.Read(buf, 0, want);
        if (n <= 0) break;
        output.Write(buf, 0, n);
        copied += n;
      }
      // Last-block tail past `size` retains zero from the disk init.
    }
    output.Flush();
  }

  // ==================================================================
  // Internal types
  // ==================================================================

  // A segment-summary entry for one main-area block: the owning node id, the block's index
  // within that node (0 for an inode block), and whether the block is a node or data block.
  private readonly record struct BlockSummary(uint Nid, ushort OfsInNode, bool IsNode);

  // Bundled checkpoint inputs shared by both checkpoint packs.
  private sealed class CheckpointArgs {
    public ulong ValidBlockCount;
    public uint ValidInodeCount;
    public uint ValidNodeCount;
    public uint NextFreeNid;
    public ulong UserBlockCount;
    public uint CountedSegments;
    public uint FreeSegments;
    public uint RsvdSegments;
    public uint OvpSegments;
    public int[] CurSegnos = [];
    public ulong NowSecs;
  }

  // One direct or indirect node block belonging to a file: a direct node lists a
  // slice of the file's data blocks, an indirect node lists child node ids.
  private sealed class NodePlan {
    public uint Nid;
    public int Block = -1;
    public int Noffset;
    public bool IsIndirect;
    public int FirstDataIndex;
    public int DataCount;
    public readonly List<NodePlan> Children = [];
    public readonly List<int> Addresses = [];
  }

  private sealed class FilePlan {
    public uint Nid;
    public string Name = string.Empty;
    public byte[] Data = [];
    public long? StreamingSize;
    public Func<Stream>? StreamOpener;
    public int InodeBlock;
    public List<int> DataBlocks = [];
    public uint ParentNid;

    // How many of the file's blocks the inode's own i_addr[] holds, the five
    // i_nid[] pointers, and every node block below them in allocation order.
    public int InodeDirectCount;
    public readonly NodePlan?[] NodePointers = new NodePlan?[5];
    public readonly List<NodePlan> Nodes = [];

    // Logical byte length: the declared streaming size for a streaming entry,
    // else the buffered byte[] length. Drives block sizing and i_size.
    public long EffectiveLength => this.StreamingSize ?? this.Data.Length;
  }

  // A planned directory inode (root or subdirectory). Children are the direct entries
  // this directory's inline dentry block names: (child nid, leaf name, file type).
  private sealed class DirPlan {
    public uint Nid;
    public int InodeBlock;
    public uint ParentNid;
    public readonly Dictionary<string, DirPlan> SubDirs = new(StringComparer.Ordinal);
    public readonly List<(uint Nid, string Name, byte Type)> Children = [];

    // Dentry storage: inline (in the inode block) for small directories, or regular
    // 4 KiB dentry data blocks for large ones laid out per the F2FS multi-level hash-bucket
    // scheme. For a non-inline directory, DentryLayout maps each used logical block index
    // (pgofs, i.e. the i_addr index) to the children placed in that block, and DentryBlocks
    // maps the same pgofs to the absolute disk-block address assigned to it.
    public bool UsesInlineDentry = true;
    public readonly Dictionary<int, List<(uint Nid, string Name, byte Type)>> DentryLayout = [];
    public readonly Dictionary<int, int> DentryBlocks = [];

    // Highest used logical block index + 1 (the directory's block span; holes allowed).
    public int DentryBlockSpan;
  }

  // ==================================================================
  // Superblock (f2fs_super_block) — block starts at blockOffset, struct
  // starts at blockOffset + 1024 (F2FS_SUPER_OFFSET).
  // ==================================================================
  private static void WriteSuperblock(
    SparseBlockImage disk, int blockOffset, int totalBlocks, uint totalSegments, uint totalSections,
    uint totalZones, uint seg0BlkAddr, uint cpBlkAddr, uint sitBlkAddr, uint natBlkAddr,
    uint ssaBlkAddr, uint mainBlkAddr, uint ssaSegments, uint segmentCountMain, string volumeLabel) {

    var off = blockOffset + SuperOffset;

    // The superblock struct lives immediately after F2FS_SUPER_OFFSET(1024) bytes of pad.
    // sizeof(f2fs_super_block) = 3072.
    var sb = new byte[3072];

    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0), F2fsMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(4), 1);   // major_ver
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(6), 0);   // minor_ver — mkfs writes 16 here; matching it changes nothing
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
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(64), ssaSegments);        // segment_count_ssa
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
    var uuid = Guid.NewGuid().ToByteArray(bigEndian: true);
    Array.Copy(uuid, 0, sb, 108, 16);

    // volume_name[512] __le16 — UTF-16LE, up to 511 code units + null terminator.
    var nameChars = volumeLabel;
    for (var i = 0; i < nameChars.Length && i < 511; ++i)
      BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(124 + i * 2), nameChars[i]);
    // end offset: 124 + 512*2 = 1148.

    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(1148), 0); // extension_count
    // extension_list[64][8] = 512 bytes — leave zero.
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(1148 + 4 + 512), 0); // cp_payload (none)

    // version[256] and init_version[256]: what made the volume, and what made it
    // first. mkfs.f2fs puts the running kernel's version line here, and fsck.f2fs
    // prints both back — a volume that leaves them empty announces that whatever
    // wrote it was not mkfs.
    var version = HostVersion();
    version.CopyTo(sb.AsSpan(VersionOffset));
    version.CopyTo(sb.AsSpan(VersionOffset + VersionLength));
    // Remaining fields (feature, encryption level, …) zero-filled.

    sb.CopyTo(disk.At(off, 3072));
  }

  /// <summary>
  /// The version to stamp a fresh volume's checkpoint with.
  /// </summary>
  /// <remarks>
  /// Any value does, as long as the standby pack's is lower — the pair is only ever
  /// compared with each other. It is kept above one so the standby's is a real
  /// number too, and away from the top so that a volume can be written to many
  /// times before it wraps.
  /// </remarks>
  private static ulong NewCheckpointVersion()
    => (ulong)System.Security.Cryptography.RandomNumberGenerator.GetInt32(2, int.MaxValue);

  /// <summary>Bytes the superblock gives each of its two version strings.</summary>
  private const int VersionLength = 256;

  private const int VersionOffset = 1148 + 4 + 512 + 4;

  /// <summary>
  /// The version line a volume records as having made it.
  /// </summary>
  /// <remarks>
  /// mkfs.f2fs writes the running kernel's <c>/proc/version</c> line, truncated to
  /// the field. Off Linux there is no such line — and no mkfs.f2fs either — so a
  /// plausible one stands in rather than leaving the field empty, which is the one
  /// value no volume made by the tool ever has.
  /// </remarks>
  private static byte[] HostVersion() {
    var line = "Linux version 6.12.0";
    try {
      if (File.Exists("/proc/version")) {
        var read = File.ReadAllText("/proc/version").Trim();
        if (read.Length > 0) line = read;
      }
    } catch (IOException) {
      // No /proc here; the fallback stands.
    } catch (UnauthorizedAccessException) {
      // Likewise.
    }

    var bytes = new byte[VersionLength];
    var encoded = Encoding.UTF8.GetBytes(line);
    encoded.AsSpan(0, Math.Min(encoded.Length, VersionLength - 1)).CopyTo(bytes);
    return bytes;
  }

  // ==================================================================
  // Checkpoint pack: 6 blocks (cp1 + compact_summary + 3 node summaries + cp2).
  //
  // Every written node/data block lives in an ordinary (non-current) segment and is
  // described by the on-disk SSA. The six current segments are empty, so the pack's
  // data/node summaries and the checkpoint's cur_*_blkoff are all zero.
  // ==================================================================
  private static byte[] BuildCheckpointPack(CheckpointArgs a, ulong checkpointVer) {
    var pack = new byte[CpPackTotalBlockCount * BlockSize];

    // ---- Block 0 / 5: f2fs_checkpoint header ----
    var cp = BuildCheckpointBlock(a, checkpointVer);
    Array.Copy(cp, 0, pack, 0 * BlockSize, BlockSize);

    // ---- Block 1: compact summary block (NAT/SIT journals; no data summaries — empty cursegs) ----
    var compact = BuildCompactSummaryBlock(a);
    Array.Copy(compact, 0, pack, 1 * BlockSize, BlockSize);

    // ---- Blocks 2-4: HOT/WARM/COLD node summaries — all empty (current node segments are empty) ----
    Array.Copy(BuildNodeSummaryBlock([]), 0, pack, 2 * BlockSize, BlockSize);
    Array.Copy(BuildNodeSummaryBlock([]), 0, pack, 3 * BlockSize, BlockSize);
    Array.Copy(BuildNodeSummaryBlock([]), 0, pack, 4 * BlockSize, BlockSize);

    // ---- Block 5: cp_page_2 (same as cp_page_1) ----
    Array.Copy(cp, 0, pack, 5 * BlockSize, BlockSize);

    return pack;
  }

  // Build the checkpoint header (cp_page_1 / cp_page_2 — same content, same CRC).
  private static byte[] BuildCheckpointBlock(CheckpointArgs a, ulong checkpointVer) {
    var cp = new byte[BlockSize];

    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(0), checkpointVer);      // checkpoint_ver
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(8), a.UserBlockCount);   // user_block_count
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(16), a.ValidBlockCount); // valid_block_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(24), a.RsvdSegments);    // rsvd_segment_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(28), a.OvpSegments);     // overprov_segment_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(32), a.FreeSegments);    // free_segment_count

    // cur_node_segno[8] at 36 (×4), cur_node_blkoff[8] at 68 (×2).
    // cur_data_segno[8] at 84 (×4), cur_data_blkoff[8] at 116 (×2).
    // CurSegnos = [HOT_DATA, WARM_DATA, COLD_DATA, HOT_NODE, WARM_NODE, COLD_NODE].
    var unused = 0xFFFFFFFFu;
    for (var i = 0; i < 8; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(36 + i * 4), unused);
      BinaryPrimitives.WriteUInt16LittleEndian(cp.AsSpan(68 + i * 2), 0);
      BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(84 + i * 4), unused);
      BinaryPrimitives.WriteUInt16LittleEndian(cp.AsSpan(116 + i * 2), 0);
    }
    // Data cursegs occupy indices 0..2 of CurSegnos; node cursegs occupy 3..5. All blkoffs
    // stay 0 because the current segments are empty.
    for (var i = 0; i < 3; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(84 + i * 4), (uint)a.CurSegnos[i]);     // cur_data_segno
      BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(36 + i * 4), (uint)a.CurSegnos[3 + i]); // cur_node_segno
    }

    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(132), CpUmountFlag | CpCompactSumFlag); // ckpt_flags
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(136), CpPackTotalBlockCount);           // cp_pack_total_block_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(140), 1);                               // cp_pack_start_sum (= 1 + cp_payload).
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(144), a.ValidNodeCount);                // valid_node_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(148), a.ValidInodeCount);               // valid_inode_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(152), a.NextFreeNid);                   // next_free_nid
    // sit_ver_bitmap_bytesize / nat_ver_bitmap_bytesize per fsck sanity check:
    //   ((segment_count_(sit|nat) / 2) << log_blocks_per_seg) / 8 = ((2/2)*512)/8 = 64 bytes each.
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(156), 64); // sit_ver_bitmap_bytesize
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(160), 64); // nat_ver_bitmap_bytesize

    const int checksumOffset = 4092; // CP_CHKSUM_OFFSET = BLKSIZE - 4
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(164), checksumOffset);                 // checksum_offset
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(168), 0UL);                            // elapsed_time
    // alloc_type[16] at 176 — all zero (LFS).
    // sit_nat_version_bitmap at 192 — leave zero (0 set bits ⇒ NAT/SIT pack 1 only).

    // Sentinel magic immediately before the checksum (round-trip / spec-offset convention
    // shared with our reader and existing tests).
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(checksumOffset - 4), F2fsMagic);

    // f2fs-tools lib/libf2fs.c:f2fs_checkpoint_chksum: CRC seed = F2FS_SUPER_MAGIC, range = bytes [0, checksum_offset).
    var crc = F2fsCrc32(F2fsMagic, new ReadOnlySpan<byte>(cp, 0, checksumOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(checksumOffset), crc);

    return cp;
  }

  // Compact summary block: { f2fs_journal (NAT journal[507]), SIT journal[507], data summaries…, footer[5] }.
  // With empty current segments there are no data-summary entries; the journals merely
  // mirror the on-disk NAT root entry and the six current SIT entries.
  private static byte[] BuildCompactSummaryBlock(CheckpointArgs a) {
    var block = new byte[BlockSize];

    // ---- NAT journal: n_nats(2) then nat_journal_entry[] (each 13 bytes: nid, version, ino, block_addr). ----
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0), 0); // n_nats = 0 (root NAT is on disk).

    // ---- SIT journal at offset SUM_JOURNAL_SIZE (507): n_sits(2) then sit_journal_entry[] (each 78 bytes). ----
    const int sitJournalOff = SumJournalSize;
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(sitJournalOff), 6);
    for (var i = 0; i < a.CurSegnos.Length; ++i) {
      var entryOff = sitJournalOff + 2 + i * 78;
      // f2fs_sit_entry: vblocks(2) + valid_map[64] + mtime(8) = 74 bytes. Current segments are
      // empty; the type bits live in vblocks but with valid count 0.
      var type = i < 3 ? CursegHotData + i : CursegHotNode + (i - 3);
      BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(entryOff), (uint)a.CurSegnos[i]);
      var seOff = entryOff + 4;
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(seOff), (ushort)(type << 10));
      BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(seOff + 66), a.NowSecs);
    }

    // Data summaries (offset 2 * SUM_JOURNAL_SIZE) and the footer are left zero: empty cursegs.
    return block;
  }

  // ==================================================================
  // On-disk Segment Summary Area: one f2fs_summary_block per main segment at
  // ssa_blkaddr + segno. fsck validates each reachable node/data block against the
  // summary entry at its block-offset within the owning segment's SSA block.
  // ==================================================================
  private static void WriteSegmentSummaryArea(SparseBlockImage disk, int ssaBlkAddr, int segmentCountMain,
    int mainBlkAddr, int[] sitTypes, Dictionary<int, BlockSummary> blockSummaries) {

    for (var seg = 0; seg < segmentCountMain; ++seg) {
      var ssaOff = (ssaBlkAddr + seg) * BlockSize;
      var firstBlock = mainBlkAddr + seg * BlocksPerSeg;

      // Footer entry_type follows the segment's content type: node segments → SUM_TYPE_NODE.
      var type = sitTypes[seg];
      var isNodeSeg = type is CursegHotNode or CursegWarmNode or CursegColdNode;

      for (var b = 0; b < BlocksPerSeg; ++b) {
        if (!blockSummaries.TryGetValue(firstBlock + b, out var bs))
          continue;
        var entryOff = ssaOff + b * 7;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.At(entryOff, 4), bs.Nid); // parent nid
        disk[entryOff + 4] = 1;                                                  // version
        BinaryPrimitives.WriteUInt16LittleEndian(disk.At(entryOff + 5, 2), bs.OfsInNode); // ofs_in_node
      }

      // summary_footer follows the entries AND the journal: offset = SUM_ENTRIES_SIZE +
      // SUM_JOURNAL_SIZE = 3584 + 507 = 4091. Layout: entry_type(1) + check_sum(4). fsck reads
      // entry_type for node/data classification; the SSA check_sum is not validated.
      disk[ssaOff + SumEntriesSize + SumJournalSize] = (byte)(isNodeSeg ? 1 : 0); // SUM_TYPE_NODE : SUM_TYPE_DATA
    }
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
  private static void WriteNatEntry(SparseBlockImage disk, int natBlkAddr, uint nid, uint ino, uint blockAddr) {
    const int natEntriesPerBlock = 4095 / 9; // = 455 (kernel NAT_ENTRY_PER_BLOCK)
    var natBlock = (int)(nid / (uint)natEntriesPerBlock);
    var natIdx = (int)(nid % (uint)natEntriesPerBlock);
    var off = (long)(natBlkAddr + natBlock) * BlockSize + natIdx * 9;
    var entry = disk.At(off, 9);
    entry[0] = 1; // version
    BinaryPrimitives.WriteUInt32LittleEndian(entry[1..], ino);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[5..], blockAddr);
  }

  // ==================================================================
  // SIT entry writer. Entry layout: { __le16 vblocks, u8 valid_map[64], __le64 mtime }.
  // 4096 / 74 = 55 entries per SIT block (with 26-byte trailing pad).
  // ==================================================================
  private static void WriteSitEntry(SparseBlockImage disk, int sitBlkAddr, int segno,
    ushort vblocks, ReadOnlySpan<byte> validMap, ulong mtime) {
    const int sitEntriesPerBlock = BlockSize / 74; // 55
    var sitBlock = segno / sitEntriesPerBlock;
    var sitIdx = segno % sitEntriesPerBlock;
    var off = (long)(sitBlkAddr + sitBlock) * BlockSize + sitIdx * 74;
    var entry = disk.At(off, 74);
    BinaryPrimitives.WriteUInt16LittleEndian(entry, vblocks);
    validMap.CopyTo(entry.Slice(2, 64));
    BinaryPrimitives.WriteUInt64LittleEndian(entry[66..], mtime);
  }

  /// <summary>
  /// Assigns the node blocks a file needs beyond the 923 addresses its inode holds,
  /// mirroring the kernel's get_node_path() shape: i_nid[0] and i_nid[1] are direct
  /// nodes, i_nid[2] and i_nid[3] indirect nodes over direct ones, and i_nid[4] a
  /// double-indirect node. Each node's ofs_of_node is the value the kernel would
  /// compute for that position.
  /// </summary>
  private static void PlanNodeTree(FilePlan f, int blocks, ref uint nextNid) {
    var consumed = f.InodeDirectCount;
    var rest = blocks - consumed;
    if (rest <= 0) return;

    NodePlan Direct(int noffset, ref uint nid) {
      var take = Math.Min(rest, AddrsPerBlock);
      var node = new NodePlan {
        Nid = nid++,
        Noffset = noffset,
        FirstDataIndex = consumed,
        DataCount = take,
      };
      consumed += take;
      rest -= take;
      f.Nodes.Add(node);
      return node;
    }

    // i_nid[0], i_nid[1]: direct node blocks.
    if (rest > 0) f.NodePointers[0] = Direct(NoffsetDir1, ref nextNid);
    if (rest > 0) f.NodePointers[1] = Direct(NoffsetDir2, ref nextNid);

    // i_nid[2], i_nid[3]: indirect node blocks, each over up to 1018 direct nodes.
    for (var slot = 2; slot <= 3 && rest > 0; ++slot) {
      var baseNoffset = slot == 2 ? NoffsetInd1 : NoffsetInd2;
      var indirect = new NodePlan { Nid = nextNid++, Noffset = baseNoffset, IsIndirect = true };
      f.Nodes.Add(indirect);
      f.NodePointers[slot] = indirect;
      while (rest > 0 && indirect.Children.Count < NidsPerBlock)
        indirect.Children.Add(Direct(baseNoffset + 1 + indirect.Children.Count, ref nextNid));
    }

    // i_nid[4]: double-indirect node.
    if (rest > 0) {
      var dind = new NodePlan { Nid = nextNid++, Noffset = NoffsetDind, IsIndirect = true };
      f.Nodes.Add(dind);
      f.NodePointers[4] = dind;
      while (rest > 0 && dind.Children.Count < NidsPerBlock) {
        var slot = dind.Children.Count;
        var indirect = new NodePlan {
          Nid = nextNid++,
          Noffset = NoffsetDind + 1 + slot * (NidsPerBlock + 1),
          IsIndirect = true,
        };
        f.Nodes.Add(indirect);
        dind.Children.Add(indirect);
        while (rest > 0 && indirect.Children.Count < NidsPerBlock)
          indirect.Children.Add(Direct(indirect.Noffset + 1 + indirect.Children.Count, ref nextNid));
      }
    }

    if (rest > 0)
      throw new InvalidOperationException(
        $"F2FS: '{f.Name}' still needs {rest:N0} blocks after the node tree is full.");
  }

  // ==================================================================
  // A direct or indirect node block: 1018 block addresses or node ids,
  // followed by the 24-byte node footer.
  // ==================================================================
  private static void WriteNodeBlock(SparseBlockImage disk, NodePlan node, uint ino) {
    var s = disk.Block(node.Block);
    if (node.IsIndirect) {
      for (var i = 0; i < node.Children.Count; ++i)
        BinaryPrimitives.WriteUInt32LittleEndian(s[(i * 4)..], node.Children[i].Nid);
    } else {
      for (var i = 0; i < node.Addresses.Count; ++i)
        BinaryPrimitives.WriteUInt32LittleEndian(s[(i * 4)..], (uint)node.Addresses[i]);
    }

    // node_footer: nid differs from ino, which is how the kernel tells a plain
    // node block from an inode block (RAW_IS_INODE compares the two).
    var footerOff = BlockSize - 24;
    BinaryPrimitives.WriteUInt32LittleEndian(s[footerOff..], node.Nid);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 4)..], ino);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 8)..], (uint)(node.Noffset << OffsetBitShift));
    BinaryPrimitives.WriteUInt64LittleEndian(s[(footerOff + 12)..], 1UL);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 20)..], 0);
  }

  // ==================================================================
  // File inode (f2fs_inode). Regular file with direct block pointers.
  // ==================================================================
  private static void WriteRegularFileInode(SparseBlockImage disk, int block, uint ino, string name,
    long size, FilePlan f, uint parentNid) {
    var s = disk.Block(block);

    BinaryPrimitives.WriteUInt16LittleEndian(s[0..], 0x81A4); // i_mode: S_IFREG | 0644
    s[2] = 0;                                                 // i_advise
    s[3] = 0;                                                 // i_inline (no inline data/xattr)
    BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 0);      // i_uid
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0);      // i_gid
    BinaryPrimitives.WriteUInt32LittleEndian(s[12..], 1);     // i_links
    BinaryPrimitives.WriteUInt64LittleEndian(s[16..], (ulong)size);                  // i_size
    // i_blocks counts the inode, its data blocks, and every node block below it.
    BinaryPrimitives.WriteUInt64LittleEndian(s[24..], (ulong)(1 + f.DataBlocks.Count + f.Nodes.Count));
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
    BinaryPrimitives.WriteUInt32LittleEndian(s[84..], parentNid); // i_pino (parent directory)

    var nameBytes = Encoding.UTF8.GetBytes(name);
    var namelen = Math.Min(nameBytes.Length, 255);
    BinaryPrimitives.WriteUInt32LittleEndian(s[88..], (uint)namelen); // i_namelen
    nameBytes.AsSpan(0, namelen).CopyTo(s[92..]); // i_name[255]
    s[92 + 255] = 0; // i_dir_level

    // i_ext (f2fs_extent) at offset 348: { __le32 fofs, __le32 blk_addr, __le32 len } = 12 bytes — leave zero.

    // i_addr[923] starts at offset 360. With no F2FS_EXTRA_ATTR, ofs = 0.
    const int iAddrOff = 360;
    for (var i = 0; i < f.InodeDirectCount; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(s[(iAddrOff + i * 4)..], (uint)f.DataBlocks[i]);

    // i_nid[5] at 4052: the two direct, two indirect and one double-indirect node
    // pointers. All zero when the file fits the inode's own 923 addresses.
    for (var i = 0; i < 5; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(s[(INidOffset + i * 4)..], f.NodePointers[i]?.Nid ?? 0);

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
  // Directory inode with inline dentry entries (root or subdirectory). Layout matches kernel:
  //   At i_addr[1] (offset 364): bitmap[23] + reserved[7] + dentry[182][11] + filename[182][8].
  // i_addr[0] is the "inline reserved" slot — must remain 0. Long names span multiple
  // consecutive filename slots (each F2FS_SLOT_LEN = 8 bytes); the bitmap marks every
  // occupied slot but only the first slot of a name carries the f2fs_dir_entry.
  // ==================================================================
  private static void WriteDirectoryInodeInline(SparseBlockImage disk, int block, uint ino, uint parentNid,
    IReadOnlyList<(uint Nid, string Name, byte Type)> children, bool isRoot) {
    var s = disk.Block(block);

    BinaryPrimitives.WriteUInt16LittleEndian(s[0..], 0x41ED); // i_mode: S_IFDIR | 0755
    s[2] = 0;                                                 // i_advise
    s[3] = (byte)(F2fsInlineDentry | F2fsDataExist);          // i_inline (kernel: INLINE_DENTRY implies DATA_EXIST)
    BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 0);       // i_uid
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0);       // i_gid
    // i_links: 2 for self+"." plus one per child subdirectory ("..").
    var childDirCount = children.Count(c => c.Type == FtDir);
    BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)(2 + childDirCount));
    // i_size: with inline_dentry, mkfs uses one block size (4096) for the dentry block.
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
    BinaryPrimitives.WriteUInt32LittleEndian(s[84..], isRoot ? 0 : parentNid); // i_pino (root has no parent).
    BinaryPrimitives.WriteUInt32LittleEndian(s[88..], 0);      // i_namelen = 0 (the inode does not name itself).

    // i_addr starts at offset 360. i_addr[0] is the "inline reserved" slot — keep 0.
    // Inline dentry data starts at i_addr[1] (offset 364).
    const int inlineDataOffset = 360 + InlineReservedAddrSlots * 4; // 364
    var bitmapOff = inlineDataOffset;
    var dentryOff = inlineDataOffset + InlineDentryBitmapSize + InlineDentryReserved;
    var nameOff = dentryOff + NrInlineDentry * 11;

    // "." points at this directory; ".." points at the parent (root's parent is itself).
    var slot = 0;
    slot += WriteDentry(s, bitmapOff, dentryOff, nameOff, slot, ino, ".", FtDir);
    slot += WriteDentry(s, bitmapOff, dentryOff, nameOff, slot, isRoot ? RootIno : parentNid, "..", FtDir);
    foreach (var (childNid, name, type) in children)
      slot += WriteDentry(s, bitmapOff, dentryOff, nameOff, slot, childNid, name, type);

    // Node footer at end of block.
    var footerOff = BlockSize - 24;
    BinaryPrimitives.WriteUInt32LittleEndian(s[footerOff..], ino);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 4)..], ino);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 8)..], 0);
    BinaryPrimitives.WriteUInt64LittleEndian(s[(footerOff + 12)..], 1UL);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 20)..], 0);
  }

  // ==================================================================
  // F2FS multi-level hash-bucket directory layout (fs/f2fs/dir.c).
  //
  // A non-inline directory's dentries are spread across levels. Level n holds
  // dir_buckets(n) buckets of bucket_blocks(n) blocks each; a name lands in bucket
  // (hash % dir_buckets(level)) at the lowest level whose target bucket still has room.
  // "." and ".." occupy slots 0 and 1 of level-0 block 0. The directory inode's i_addr[]
  // index equals the block's logical offset (pgofs); unused buckets leave i_addr holes.
  // ==================================================================
  internal const int MaxDirHashDepth = 63;
  internal static int MaxDirBuckets => 1 << (MaxDirHashDepth / 2 - 1);

  private static int DirBuckets(int level) =>
    level < MaxDirHashDepth / 2 ? 1 << level : MaxDirBuckets;

  private static int BucketBlocks(int level) => level < MaxDirHashDepth / 2 ? 2 : 4;

  // First logical block (pgofs) of the given level's bucket 0.
  private static int DirLevelBaseBlock(int level) {
    var bidx = 0;
    for (var i = 0; i < level; ++i)
      bidx += DirBuckets(i) * BucketBlocks(i);
    return bidx;
  }

  // First logical block of a specific bucket at a level.
  private static int DirBlockIndex(int level, int bucket) =>
    DirLevelBaseBlock(level) + bucket * BucketBlocks(level);

  /// <summary>
  /// Plans the hash-bucket placement for a non-inline directory: assigns "." , ".." and
  /// every child to a logical block (pgofs) per the kernel's <c>f2fs_add_link</c> rule, so
  /// <c>fsck.f2fs</c>'s <c>f2fs_check_dirent_position</c> agrees with where each name sits.
  /// </summary>
  private static void PlanHashBucketDentries(DirPlan d) {
    // Per-logical-block remaining free slots (NrDentryInBlock minus what is placed).
    var freeSlots = new Dictionary<int, int>();
    int Remaining(int blk) => freeSlots.TryGetValue(blk, out var r) ? r : NrDentryInBlock;

    void PlaceInBlock(int blk, (uint Nid, string Name, byte Type) child, int slots) {
      if (!d.DentryLayout.TryGetValue(blk, out var list)) {
        list = [];
        d.DentryLayout[blk] = list;
      }
      list.Add(child);
      freeSlots[blk] = Remaining(blk) - slots;
      if (blk + 1 > d.DentryBlockSpan)
        d.DentryBlockSpan = blk + 1;
    }

    // "." and ".." live in level-0 block 0.
    PlaceInBlock(0, (d.Nid, ".", FtDir), 1);
    PlaceInBlock(0, (d.Nid == RootIno ? RootIno : d.ParentNid, "..", FtDir), 1);

    foreach (var child in d.Children) {
      var slots = DentrySlotsFor(child.Name);
      var hash = F2fsNameHash(Encoding.UTF8.GetBytes(child.Name));
      var placed = false;
      for (var level = 0; level < MaxDirHashDepth && !placed; ++level) {
        var nbucket = DirBuckets(level);
        var bucket = (int)(hash % (uint)nbucket);
        var startBlk = DirBlockIndex(level, bucket);
        var nblock = BucketBlocks(level);
        for (var b = 0; b < nblock; ++b) {
          var blk = startBlk + b;
          if (Remaining(blk) >= slots) {
            PlaceInBlock(blk, child, slots);
            placed = true;
            break;
          }
        }
      }
      if (!placed)
        throw new InvalidOperationException(
          $"F2FS writer: directory '{d.Nid}' exceeds the maximum hash-bucket depth.");
    }
  }

  // Directory inode whose dentries live in regular 4 KiB hash-bucket data blocks.
  private static void WriteDirectoryInodeWithDataBlocks(SparseBlockImage disk, int block, uint ino, uint parentNid,
    IReadOnlyList<(uint Nid, string Name, byte Type)> children, DirPlan d, bool isRoot) {
    var s = disk.Block(block);

    BinaryPrimitives.WriteUInt16LittleEndian(s[0..], 0x41ED); // i_mode: S_IFDIR | 0755
    s[2] = 0;                                                 // i_advise
    s[3] = 0;                                                 // i_inline: no inline dentry/data.
    BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 0);       // i_uid
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0);       // i_gid
    var childDirCount = children.Count(c => c.Type == FtDir);
    BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)(2 + childDirCount)); // i_links
    // i_size spans the directory's logical block range (including holes for empty buckets).
    BinaryPrimitives.WriteUInt64LittleEndian(s[16..], (ulong)((long)d.DentryBlockSpan * BlockSize));
    BinaryPrimitives.WriteUInt64LittleEndian(s[24..], (ulong)(1 + d.DentryBlocks.Count)); // i_blocks (inode + allocated data blocks).
    var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt64LittleEndian(s[32..], now);
    BinaryPrimitives.WriteUInt64LittleEndian(s[40..], now);
    BinaryPrimitives.WriteUInt64LittleEndian(s[48..], now);
    BinaryPrimitives.WriteUInt32LittleEndian(s[56..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[60..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[64..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[68..], 0);
    // i_current_depth = number of hash levels in use (kernel increments past level 0).
    BinaryPrimitives.WriteUInt32LittleEndian(s[72..], (uint)DirCurrentDepth(d.DentryBlockSpan));
    BinaryPrimitives.WriteUInt32LittleEndian(s[76..], 0);      // i_xattr_nid
    BinaryPrimitives.WriteUInt32LittleEndian(s[80..], 0);      // i_flags
    BinaryPrimitives.WriteUInt32LittleEndian(s[84..], isRoot ? 0 : parentNid); // i_pino
    BinaryPrimitives.WriteUInt32LittleEndian(s[88..], 0);      // i_namelen = 0

    // Direct block pointers: i_addr[923] at offset 360, indexed by logical block (pgofs).
    const int iAddrOff = 360;
    foreach (var (pgofs, blk) in d.DentryBlocks)
      BinaryPrimitives.WriteUInt32LittleEndian(s[(iAddrOff + pgofs * 4)..], (uint)blk);

    // Node footer at end of block.
    var footerOff = BlockSize - 24;
    BinaryPrimitives.WriteUInt32LittleEndian(s[footerOff..], ino);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 4)..], ino);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 8)..], 0);
    BinaryPrimitives.WriteUInt64LittleEndian(s[(footerOff + 12)..], 1UL);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 20)..], 0);

    // Fill each allocated dentry block with the children planned for it.
    foreach (var (pgofs, list) in d.DentryLayout) {
      var dentryBlock = disk.Block(d.DentryBlocks[pgofs]);
      const int bitmapOff = 0;
      const int dentryBase = DentryBlockBitmapSize + DentryBlockReserved;
      const int nameBase = dentryBase + NrDentryInBlock * 11;
      var slot = 0;
      foreach (var (childNid, name, type) in list)
        slot += WriteDentry(dentryBlock, bitmapOff, dentryBase, nameBase, slot, childNid, name, type);
    }
  }

  // i_current_depth: the number of hash levels the directory's block span reaches into. A
  // directory that fits in level 0 (blocks 0..1) reports depth 1; each further level adds 1.
  private static int DirCurrentDepth(int blockSpan) {
    var depth = 1;
    for (var level = 0; level < MaxDirHashDepth; ++level) {
      var end = DirLevelBaseBlock(level) + DirBuckets(level) * BucketBlocks(level);
      if (blockSpan <= end)
        return level + 1;
      depth = level + 2;
    }
    return depth;
  }

  /// <summary>
  /// Number of inline-dentry slots a name occupies: the filename area stores the UTF-8 name
  /// across <c>ceil(byteLength / F2FS_SLOT_LEN)</c> consecutive slots (kernel GET_DENTRY_SLOTS).
  /// </summary>
  private static int DentrySlotsFor(string name) {
    var len = Encoding.UTF8.GetByteCount(name);
    var slots = (len + SlotLen - 1) / SlotLen;
    return slots < 1 ? 1 : slots;
  }

  // Writes one directory entry starting at the given slot, spanning as many filename slots
  // as the name needs. Returns the number of slots consumed.
  private static int WriteDentry(Span<byte> s, int bitmapOff, int dentryBase,
    int nameBase, int slot, uint ino, string name, byte fileType) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var nameLen = Math.Min(nameBytes.Length, 255);
    var slots = DentrySlotsFor(name);

    // Mark every occupied slot in the bitmap (LSB-first, matching the reader).
    for (var i = 0; i < slots; ++i) {
      var b = slot + i;
      s[bitmapOff + b / 8] |= (byte)(1 << (b % 8));
    }

    // f2fs_dir_entry lives in the first slot's dentry array position.
    var entryOff = dentryBase + slot * 11;
    var hash = F2fsNameHash(nameBytes.AsSpan(0, nameLen));
    BinaryPrimitives.WriteUInt32LittleEndian(s[entryOff..], hash);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(entryOff + 4)..], ino);
    BinaryPrimitives.WriteUInt16LittleEndian(s[(entryOff + 8)..], (ushort)nameLen);
    s[entryOff + 10] = fileType;

    // Filename bytes fill consecutive 8-byte slots in the filename area.
    var fnOff = nameBase + slot * SlotLen;
    nameBytes.AsSpan(0, nameLen).CopyTo(s[fnOff..]);

    return slots;
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
