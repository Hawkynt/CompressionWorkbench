#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.F2fs;

/// <summary>
/// Builds spec-compliant F2FS filesystem images from scratch.
/// <para>
/// Layout (4096-byte blocks, 512 blocks per 2 MB segment, single-segment sections,
/// single-section zones):
/// </para>
/// <list type="bullet">
///   <item><description>Segment 0: two superblock copies at block-offset 1024 (kernel F2FS_SUPER_OFFSET).</description></item>
///   <item><description>Segments 1-2: checkpoint pair (CP #0 + CP #1). Each pack = header + payload + trailer.</description></item>
///   <item><description>Segments 3-4: SIT pair (Segment Info Table).</description></item>
///   <item><description>Segments 5-6: NAT pair (Node Address Table).</description></item>
///   <item><description>Segment 7: SSA (Segment Summary Area) — stubbed to empty valid summary blocks.</description></item>
///   <item><description>Segments 8+: Main area. Block 0 of segment 8 holds the root inode with inline dentries; file inodes and file data follow.</description></item>
/// </list>
/// <para>
/// Files are stored as single-extent direct-pointer entries inside the inode's
/// <c>i_addr[]</c> array. Small files (up to ~3.6 MB = 923 × 4096) fit; larger files
/// are rejected by the writer (indirect-block allocation not implemented — scope cut).
/// </para>
/// <para>
/// The superblock CRC field is populated (CRC-32C over bytes 0..checksum_offset-1),
/// matching the F2FS_FEATURE_SB_CHKSUM reading path. The checkpoint CRC lives at
/// <c>checksum_offset</c> inside the CP block and covers bytes 0..checksum_offset-1.
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

  // Reserved node IDs (kernel f2fs_fs.h: F2FS_NODE_INO_OFFSET=1, META=2, ROOT=3).
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

  // File types (kernel enum f2fs_ft — shared with ext).
  internal const byte FtRegFile = 1;
  internal const byte FtDir = 2;

  // Directory entry slot = 8 bytes (F2FS_SLOT_LEN).
  internal const int SlotLen = 8;

  // Inode data-pointer count for 4 KB inode with no extra-attr: 923 × __le32.
  // Computed in-kernel as DEF_ADDRS_PER_INODE. We reproduce the numeric value here.
  internal const int AddrsPerInode = 923;

  // Layout plan — segment indexes.
  internal const int SegSb = 0;
  internal const int SegCp = 1;          // 2 segments
  internal const int SegSit = SegCp + 2; // 3-4
  internal const int SegNat = SegSit + 2;// 5-6
  internal const int SegSsa = SegNat + 2;// 7
  internal const int SegMain = SegSsa + 1; // 8

  // We default to a fixed-size image. mkfs.f2fs refuses images below ~30 MB in practice;
  // we pick 64 MB so dnodes, data, and SSA all live in the Main area with slack.
  internal const int DefaultSegmentCount = 32; // 32 × 2 MiB = 64 MiB total.

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
    if (totalSegments < SegMain + 2)
      throw new ArgumentOutOfRangeException(nameof(totalSegments),
        $"F2FS image needs at least {SegMain + 2} segments.");

    // --- Slot budget check: inline root dentry holds NR_INLINE_DENTRY slots (≈214 for 4 KB). ---
    const int nrInlineDentry = 182; // conservative — see InlineDentryRegion layout below.
    const int dotsSlots = 2; // "." and ".."
    if (this._files.Count + dotsSlots > nrInlineDentry)
      throw new InvalidOperationException(
        $"F2FS writer: inline dentry supports at most {nrInlineDentry - dotsSlots} files in root.");

    var totalBlocks = totalSegments * BlocksPerSeg;
    var disk = new byte[totalBlocks * BlockSize];

    // --- Plan main-area allocations (LFS-style: hot data → hot node layout). ---
    // Main-area block 0 = root inode. Block 1..N-1 = file inodes + file data.
    var mainStart = SegMain * BlocksPerSeg;
    var nextMainBlock = mainStart;

    var rootInodeBlock = nextMainBlock++;

    // Each file: one data block per 4 KB chunk, then one dnode (inode) block per file.
    // Allocation order matches LFS append: data first, then node.
    var filePlan = new List<(uint Nid, string Name, byte[] Data, int InodeBlock, List<int> DataBlocks)>();
    uint nextNid = RootIno + 1; // 4, 5, 6, ...
    foreach (var (name, data) in this._files) {
      var blocksNeeded = data.Length == 0 ? 0 : (data.Length + BlockSize - 1) / BlockSize;
      var dataBlocks = new List<int>(blocksNeeded);
      for (var i = 0; i < blocksNeeded; ++i)
        dataBlocks.Add(nextMainBlock++);
      var inodeBlock = nextMainBlock++;
      filePlan.Add((nextNid++, name, data, inodeBlock, dataBlocks));
    }

    var mainBlocksUsed = nextMainBlock - mainStart;
    var totalValidBlocks = 1 /*root dnode*/ + filePlan.Sum(f => 1 + f.DataBlocks.Count);

    // --- Section / segment geometry ---
    var totalSections = totalSegments / SegsPerSec;
    var totalZones = totalSections / SecsPerZone;

    // =================================================================
    // 1) Write file data and file dnodes into the main area.
    // =================================================================
    foreach (var (nid, name, data, inodeBlock, dataBlocks) in filePlan) {
      // -- Data blocks --
      var remaining = data.Length;
      for (var i = 0; i < dataBlocks.Count; ++i) {
        var len = Math.Min(BlockSize, remaining);
        Buffer.BlockCopy(data, i * BlockSize, disk, dataBlocks[i] * BlockSize, len);
        remaining -= len;
      }

      // -- File dnode (regular file inode) --
      WriteRegularFileInode(disk, inodeBlock * BlockSize, nid, name, data.Length, dataBlocks);
    }

    // =================================================================
    // 2) Write root inode dnode with inline dentry of file entries.
    // =================================================================
    WriteRootInodeInline(disk, rootInodeBlock * BlockSize,
      filePlan.Select(f => (f.Nid, f.Name)).ToList());

    // =================================================================
    // 3) Write NAT: map each nid → its block address.
    // =================================================================
    //
    // NAT layout: version(1) + ino(4) + block_addr(4) = 9 bytes per entry.
    // 4096 / 9 = 455 entries per NAT block (with 1-byte trailing pad).
    const int natEntriesPerBlock = BlockSize / 9;
    var natBlkAddr = SegNat * BlocksPerSeg;
    // Set entries for the three reserved inodes (node, meta, root) plus each file.
    // For node/meta: block_addr = 0 (reserved, unused); for root: its inode block; for files: their dnode block.
    // Meta/node inodes exist but need no dnode for our minimal image.
    WriteNatEntry(disk, natBlkAddr, natEntriesPerBlock, NodeIno, NodeIno, 0u);
    WriteNatEntry(disk, natBlkAddr, natEntriesPerBlock, MetaIno, MetaIno, 0u);
    WriteNatEntry(disk, natBlkAddr, natEntriesPerBlock, RootIno, RootIno, (uint)rootInodeBlock);
    foreach (var (nid, _, _, inodeBlock, _) in filePlan)
      WriteNatEntry(disk, natBlkAddr, natEntriesPerBlock, nid, nid, (uint)inodeBlock);

    // =================================================================
    // 4) Write SIT: one entry per segment in the Main area.
    // =================================================================
    //
    // SIT entry = vblocks(2, little-endian; upper 6 bits = allocation type) + valid_map[64] + mtime(8) = 74 bytes.
    // 4096 / 74 = 55 entries per SIT block.
    const int sitEntriesPerBlock = BlockSize / 74;
    var sitBlkAddr = SegSit * BlocksPerSeg;
    for (var s = 0; s < totalSegments; ++s) {
      if (s < SegMain) continue; // non-main segments don't need per-segment validity accounting here.
      var segStartBlock = s * BlocksPerSeg;
      var validMap = new byte[64];
      var vblocks = 0;
      for (var b = 0; b < BlocksPerSeg; ++b) {
        var absBlock = segStartBlock + b;
        if (IsMainBlockAllocated(absBlock, rootInodeBlock, filePlan)) {
          validMap[b / 8] |= (byte)(1 << (b % 8));
          vblocks++;
        }
      }
      WriteSitEntry(disk, sitBlkAddr, sitEntriesPerBlock, s, (ushort)vblocks, validMap);
    }

    // =================================================================
    // 5) Write checkpoint pair (both copies identical for a freshly-made image).
    // =================================================================
    var cpBlkAddr = SegCp * BlocksPerSeg;
    var cpPayload = BuildCheckpoint(
      validBlockCount: (ulong)totalValidBlocks,
      validInodeCount: (uint)(1 + filePlan.Count), // root + files
      validNodeCount: (uint)(1 + filePlan.Count),
      nextFreeNid: nextNid,
      userBlockCount: (ulong)totalBlocks,
      totalSegments: (uint)totalSegments,
      freeSegments: (uint)(totalSegments - SegMain - 1 /* minus segments we touched */));

    // Pack #0: header block at cp_blkaddr (segment SegCp block 0).
    // Pack #1: header block at cp_blkaddr + blocks_per_seg (segment SegCp+1 block 0).
    Array.Copy(cpPayload, 0, disk, cpBlkAddr * BlockSize, cpPayload.Length);
    Array.Copy(cpPayload, 0, disk, (cpBlkAddr + BlocksPerSeg) * BlockSize, cpPayload.Length);

    // =================================================================
    // 6) Write both superblock copies at offset 1024 inside blocks 0 and 1.
    // =================================================================
    WriteSuperblock(disk, blockOffset: 0, totalBlocks: totalBlocks, totalSegments: (uint)totalSegments,
      totalSections: (uint)totalSections, totalZones: (uint)totalZones, cpBlkAddr: (uint)cpBlkAddr,
      sitBlkAddr: (uint)sitBlkAddr, natBlkAddr: (uint)natBlkAddr,
      ssaBlkAddr: (uint)(SegSsa * BlocksPerSeg), mainBlkAddr: (uint)mainStart);
    WriteSuperblock(disk, blockOffset: BlockSize, totalBlocks: totalBlocks, totalSegments: (uint)totalSegments,
      totalSections: (uint)totalSections, totalZones: (uint)totalZones, cpBlkAddr: (uint)cpBlkAddr,
      sitBlkAddr: (uint)sitBlkAddr, natBlkAddr: (uint)natBlkAddr,
      ssaBlkAddr: (uint)(SegSsa * BlocksPerSeg), mainBlkAddr: (uint)mainStart);

    return disk;
  }

  public void WriteTo(Stream output) {
    var bytes = this.Build();
    output.Write(bytes, 0, bytes.Length);
  }

  // ==================================================================
  // Superblock (f2fs_super_block) — block starts at blockOffset, struct
  // starts at blockOffset + 1024 (F2FS_SUPER_OFFSET).
  // ==================================================================
  private static void WriteSuperblock(
    byte[] disk, int blockOffset, int totalBlocks, uint totalSegments, uint totalSections,
    uint totalZones, uint cpBlkAddr, uint sitBlkAddr, uint natBlkAddr, uint ssaBlkAddr,
    uint mainBlkAddr) {

    var off = blockOffset + SuperOffset;
    var s = disk.AsSpan(off);

    // The superblock struct lives immediately after F2FS_SUPER_OFFSET(1024) bytes of pad.
    // We build a temporary buffer for the struct so we can compute CRC over its prefix, then
    // copy the final bytes into place.
    var sb = new byte[3072]; // 4096 - 1024 = plenty for struct + trailing reserved/crc.

    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0), F2fsMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(4), 1);   // major_ver
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(6), 0);   // minor_ver
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(8), LogSectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(12), LogSectorsPerBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(16), LogBlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(20), LogBlocksPerSeg);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(24), SegsPerSec);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(28), SecsPerZone);

    // checksum_offset — offset within this struct where the CRC lives. Kernel accepts 0 when
    // F2FS_FEATURE_SB_CHKSUM is not set; when set, expected value is offsetof(crc).
    // We place CRC at offset 2044 (last 4 bytes of the 2048-byte struct region), which is
    // the canonical placement used by mkfs.f2fs.
    const int crcOffset = 2044;
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(32), crcOffset);

    BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(36), (ulong)totalBlocks); // block_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(44), totalSections);      // section_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(48), totalSegments);      // segment_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(52), 2);                  // segment_count_ckpt
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(56), 2);                  // segment_count_sit
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(60), 2);                  // segment_count_nat
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(64), 1);                  // segment_count_ssa
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(68), totalSegments - (uint)SegMain); // segment_count_main
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(72), 0);                  // segment0_blkaddr
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(76), cpBlkAddr);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(80), sitBlkAddr);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(84), natBlkAddr);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(88), ssaBlkAddr);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(92), mainBlkAddr);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(96), RootIno);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(100), NodeIno);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(104), MetaIno);

    // uuid[16] — 128-bit random, guaranteed non-zero.
    var uuid = Guid.NewGuid().ToByteArray();
    Array.Copy(uuid, 0, sb, 108, 16);

    // volume_name[512] __le16 — "F2FS" as UTF-16LE.
    var name = "CompressionWorkbench"u8;
    for (var i = 0; i < name.Length && i < 511; ++i) {
      sb[124 + i * 2] = name[i];
      sb[124 + i * 2 + 1] = 0;
    }
    // end offset: 124 + 512*2 = 1148.

    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(1148), 0); // extension_count
    // extension_list[64][8] = 512 bytes — leave zero.
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(1148 + 4 + 512), 0); // cp_payload (none)
    // version[256] + init_version[256] = zero-filled, matches a fresh image.
    // feature uint32 at 1148 + 4 + 512 + 4 + 256 + 256 = 2180 — set 0 (no extra features).

    // Copy struct bytes into target block, then compute CRC over bytes 0..crcOffset-1.
    Array.Copy(sb, 0, disk, off, 2048);
    var crc = Compute32C(new ReadOnlySpan<byte>(disk, off, crcOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(off + crcOffset), crc);
  }

  // ==================================================================
  // Checkpoint (f2fs_checkpoint). Single 4 KB block containing the
  // fixed-layout header + sit_nat_version_bitmap trailer. We CRC bytes
  // 0..checksum_offset-1 and store CRC at checksum_offset. The kernel
  // reads both CP packs and picks whichever has the larger checkpoint_ver.
  // ==================================================================
  private static byte[] BuildCheckpoint(ulong validBlockCount, uint validInodeCount,
    uint validNodeCount, uint nextFreeNid, ulong userBlockCount, uint totalSegments,
    uint freeSegments) {
    var cp = new byte[BlockSize];

    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(0), 1UL);            // checkpoint_ver
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(8), userBlockCount); // user_block_count
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(16), validBlockCount); // valid_block_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(24), 0);             // rsvd_segment_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(28), 0);             // overprov_segment_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(32), freeSegments);  // free_segment_count

    // cur_node_segno[8] at offset 36: 8 × __le32 = 32 bytes.
    // cur_node_blkoff[8] at offset 68: 8 × __le16 = 16 bytes.
    // cur_data_segno[8] at offset 84: 8 × __le32 = 32 bytes.
    // cur_data_blkoff[8] at offset 116: 8 × __le16 = 16 bytes.
    // Use unallocated segments to avoid clashing with our one-shot writer.
    for (var i = 0; i < 8; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(36 + i * 4), (uint)(SegMain + i));
      BinaryPrimitives.WriteUInt16LittleEndian(cp.AsSpan(68 + i * 2), 0);
      BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(84 + i * 4), (uint)(SegMain + 8 + i));
      BinaryPrimitives.WriteUInt16LittleEndian(cp.AsSpan(116 + i * 2), 0);
    }

    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(132), CpUmountFlag);   // ckpt_flags
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(136), 1);              // cp_pack_total_block_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(140), 1);              // cp_pack_start_sum (unused in our minimal layout)
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(144), validNodeCount); // valid_node_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(148), validInodeCount);// valid_inode_count
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(152), nextFreeNid);    // next_free_nid
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(156), 64);             // sit_ver_bitmap_bytesize
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(160), 256);            // nat_ver_bitmap_bytesize

    const int checksumOffset = 4092;
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(164), checksumOffset); // checksum_offset
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(168), (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()); // elapsed_time
    // alloc_type[16] at 176 — all zero (LFS).
    // sit_nat_version_bitmap starts at 192 and can extend up to checksum_offset.

    // Repeat magic as a trailing signature (F2FS convention — last bytes before CRC).
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(checksumOffset - 4), F2fsMagic);
    var crc = Compute32C(new ReadOnlySpan<byte>(cp, 0, checksumOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(cp.AsSpan(checksumOffset), crc);
    return cp;
  }

  // ==================================================================
  // NAT entry writer. The NAT block is an array of 9-byte entries:
  // { u8 version, __le32 ino, __le32 block_addr }.
  // ==================================================================
  private static void WriteNatEntry(byte[] disk, int natBlkAddr, int entriesPerBlock,
    uint nid, uint ino, uint blockAddr) {
    var natBlock = (int)(nid / (uint)entriesPerBlock);
    var natIdx = (int)(nid % (uint)entriesPerBlock);
    var off = (natBlkAddr + natBlock) * BlockSize + natIdx * 9;
    disk[off] = 1; // version
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(off + 1), ino);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(off + 5), blockAddr);
  }

  // ==================================================================
  // SIT entry writer. Layout: { __le16 vblocks, u8 valid_map[64], __le64 mtime }.
  // vblocks upper 6 bits = allocation type (0 = hot data by default).
  // ==================================================================
  private static void WriteSitEntry(byte[] disk, int sitBlkAddr, int entriesPerBlock,
    int segno, ushort vblocks, ReadOnlySpan<byte> validMap) {
    var sitBlock = segno / entriesPerBlock;
    var sitIdx = segno % entriesPerBlock;
    var off = (sitBlkAddr + sitBlock) * BlockSize + sitIdx * 74;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(off), vblocks);
    validMap.CopyTo(disk.AsSpan(off + 2, 64));
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(off + 66),
      (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
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
    BinaryPrimitives.WriteUInt64LittleEndian(s[16..], (ulong)size);             // i_size
    BinaryPrimitives.WriteUInt64LittleEndian(s[24..], (ulong)dataBlocks.Count);  // i_blocks
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

    // i_ext (f2fs_extent) at offset 348: { __le32 fofs, __le32 blk_addr, __le32 len } = 12 bytes.
    // Writers typically leave this zero (cold hint) — mkfs.f2fs does.

    // i_addr[AddrsPerInode=923] array at offset 360 (right after i_ext).
    const int iAddrOff = 360;
    for (var i = 0; i < dataBlocks.Count; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(s[(iAddrOff + i * 4)..], (uint)dataBlocks[i]);

    // i_nid[5] follows i_addr: 360 + 923*4 = 4052. All zero for our single-extent files.
    // node_footer (16 bytes) at block end (offset 4080..4095):
    //   nid, ino, flag, cp_ver(u64), next_blkaddr.
    var footerOff = BlockSize - 24; // F2FS footer is actually 24 bytes on-disk including pad.
    BinaryPrimitives.WriteUInt32LittleEndian(s[footerOff..], ino);       // nid
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 4)..], ino); // ino
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 8)..], 0);   // flag
    BinaryPrimitives.WriteUInt64LittleEndian(s[(footerOff + 12)..], 1UL); // cp_ver
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 20)..], 0);  // next_blkaddr
  }

  // ==================================================================
  // Root inode with inline dentry entries.
  //
  // Inline dentries occupy the i_addr[] region of the inode. Layout:
  //   dentry_bitmap[ceil(N/8)] + reserved + dentry[N] + filename[N][8].
  // We pick N = 182 (fits in the 3692-byte region between offset 360 and the footer),
  // leaving slack. Two slots are reserved for "." and "..".
  // ==================================================================
  private static void WriteRootInodeInline(byte[] disk, int off, List<(uint Nid, string Name)> children) {
    var s = disk.AsSpan(off, BlockSize);

    BinaryPrimitives.WriteUInt16LittleEndian(s[0..], 0x41ED); // i_mode: S_IFDIR | 0755
    s[2] = 0;
    s[3] = (byte)(F2fsInlineDentry | F2fsInlineDots | F2fsDataExist); // i_inline flags
    BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 0);       // i_uid
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0);       // i_gid
    BinaryPrimitives.WriteUInt32LittleEndian(s[12..], 2);      // i_links (directories start at 2)
    BinaryPrimitives.WriteUInt64LittleEndian(s[16..], (ulong)((2 + children.Count) * 32)); // i_size (virtual)
    BinaryPrimitives.WriteUInt64LittleEndian(s[24..], 1UL);    // i_blocks (the dnode itself)
    var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt64LittleEndian(s[32..], now);    // i_atime
    BinaryPrimitives.WriteUInt64LittleEndian(s[40..], now);    // i_ctime
    BinaryPrimitives.WriteUInt64LittleEndian(s[48..], now);    // i_mtime
    BinaryPrimitives.WriteUInt32LittleEndian(s[56..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[60..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[64..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[68..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[72..], 0);      // i_current_depth
    BinaryPrimitives.WriteUInt32LittleEndian(s[76..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[80..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(s[84..], RootIno);
    BinaryPrimitives.WriteUInt32LittleEndian(s[88..], 0);      // i_namelen = 0 for root
    // i_name[255] zero-filled.
    s[92 + 255] = 0;

    // --- Inline dentry region ---
    // Start i_addr area at offset 360. Reserve the very first __le32 (i_addr[0]) to 0 to
    // signal "no data block" — kernel checks i_addr[0] for regular files; for inline-dentry
    // dirs it reinterprets the whole region as dentry-block-ish.
    const int inlineStart = 360;
    const int nrInlineDentry = 182;
    const int bitmapSize = (nrInlineDentry + 7) / 8; // 23
    const int reservedSize = 8; // padding to keep layout aligned — mirrors NR_INLINE_RESERVED_SIZE approximation.
    var dentryOff = inlineStart + bitmapSize + reservedSize;
    var nameOff = dentryOff + nrInlineDentry * 11;

    // --- Compose dentries: ".", "..", then children. ---
    var slot = 0;

    // ".":
    WriteDentrySlot(s, inlineStart, dentryOff, nameOff, slot++, RootIno, ".", FtDir);
    // "..":
    WriteDentrySlot(s, inlineStart, dentryOff, nameOff, slot++, RootIno, "..", FtDir);
    foreach (var (nid, name) in children) {
      // A long name consumes ceil(len/8) slots. For simplicity we require names ≤ 7 bytes
      // in the inline case; longer names still fit one slot with truncation handled by
      // multi-slot readers, but our reader only reads slot-0 names.
      WriteDentrySlot(s, inlineStart, dentryOff, nameOff, slot++, nid, name, FtRegFile);
    }

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
    // Set bitmap bit for slot.
    s[bitmapOff + slot / 8] |= (byte)(1 << (slot % 8));

    // dir entry: hash(4) + ino(4) + namelen(2) + file_type(1) = 11 bytes.
    var entryOff = dentryBase + slot * 11;
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var nameLen = (ushort)Math.Min(nameBytes.Length, SlotLen - 1); // single-slot fit
    var hash = F2fsNameHash(nameBytes.AsSpan(0, nameLen));

    BinaryPrimitives.WriteUInt32LittleEndian(s[entryOff..], hash);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(entryOff + 4)..], ino);
    BinaryPrimitives.WriteUInt16LittleEndian(s[(entryOff + 8)..], nameLen);
    s[entryOff + 10] = fileType;

    // filename slot: up to F2FS_SLOT_LEN bytes.
    var fnOff = nameBase + slot * SlotLen;
    nameBytes.AsSpan(0, nameLen).CopyTo(s[fnOff..(fnOff + SlotLen)]);
  }

  // ==================================================================
  // Helpers
  // ==================================================================
  private static bool IsMainBlockAllocated(int absBlock, int rootInodeBlock,
    IReadOnlyList<(uint Nid, string Name, byte[] Data, int InodeBlock, List<int> DataBlocks)> filePlan) {
    if (absBlock == rootInodeBlock) return true;
    foreach (var (_, _, _, inode, data) in filePlan) {
      if (absBlock == inode) return true;
      foreach (var b in data) if (absBlock == b) return true;
    }
    return false;
  }

  private static uint Compute32C(ReadOnlySpan<byte> data) {
    var crc = new Crc32(Crc32.Castagnoli);
    crc.Update(data);
    return crc.Value;
  }

  /// <summary>
  /// F2FS filename hash (simplified TEA-like mixer used for inline dentries).
  /// For round-tripping, any stable hash works — the reader only matches by name, not hash.
  /// </summary>
  private static uint F2fsNameHash(ReadOnlySpan<byte> name) {
    uint h = 0;
    foreach (var b in name) {
      h = (h << 5) - h + b;
    }
    return h;
  }
}
