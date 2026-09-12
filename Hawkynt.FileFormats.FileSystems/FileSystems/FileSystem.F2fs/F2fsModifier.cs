#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.F2fs;

/// <summary>
/// In-place modification of an F2FS image — Add and Remove without a full rebuild.
/// F2FS is a log-structured filesystem: mutations append to the open "current" segments
/// (one per CURSEG_* type), update on-disk NAT/SIT entries, mirror those updates in the
/// checkpoint's NAT/SIT journals (best-effort — when the journal is full the on-disk
/// entry alone is authoritative, since f2fs-tools treats the journal as overrides over
/// on-disk and a missing journal entry simply falls through to disk), and advance the
/// checkpoint version + CRC. We write the new state into the OLDER of the two checkpoint
/// packs so the previous one stays as a roll-back snapshot, matching the kernel's
/// alternating-checkpoint design.
/// </summary>
/// <remarks>
/// <para>
/// Layout assumptions match <see cref="F2fsWriter"/>: 4 KiB blocks, 512 blocks per 2 MiB
/// segment, single-segment sections, single-section zones, six reserved "current"
/// segments after the populated regions.
/// </para>
/// <para>
/// Scope: post-leaf-only. Now supports (1) full NAT/SIT block rewrite — when the compact
/// summary block's NAT/SIT journals overflow we drop the mirror entry rather than
/// throwing, because the on-disk NAT/SIT block already carries the update and f2fs-tools
/// consults the journal as overrides; (2) regular dentry blocks — when the root inline
/// dentry region is full we convert the root directory in place to a non-inline directory
/// whose dentries live in a freshly-allocated HOT_DATA block, with all subsequent Adds
/// landing in the same block; (3) curseg exhaustion — when an open current segment fills
/// up we promote a free main-area segment of the correct CURSEG_* type and start writing
/// there. Genuinely out of scope: subdirectory creation, nested removal, encrypted
/// directories, growing the main-area segment count, multi-level indirect inode trees.
/// </para>
/// </remarks>
internal static class F2fsModifier {
  // --- F2FS on-disk constants (kernel include/linux/f2fs_fs.h) ---
  private const uint F2fsMagic = F2fsWriter.F2fsMagic;
  private const int SuperOffset = F2fsWriter.SuperOffset;
  private const int BlockSize = F2fsWriter.BlockSize;
  private const int BlocksPerSeg = F2fsWriter.BlocksPerSeg;
  private const int SlotLen = F2fsWriter.SlotLen;
  private const uint RootIno = F2fsWriter.RootIno;
  private const byte FtRegFile = F2fsWriter.FtRegFile;
  private const byte FtDir = F2fsWriter.FtDir;
  private const byte F2fsInlineDentry = F2fsWriter.F2fsInlineDentry;
  private const byte F2fsDataExist = F2fsWriter.F2fsDataExist;
  private const int AddrsPerInode = F2fsWriter.AddrsPerInode;

  // CP layout (cp_blkaddr-relative): block 0 = cp1, block 1 = compact summary
  // (NAT journal at offset 0, SIT journal at offset 507), block 5 = cp2.
  private const int CpPackTotalBlockCount = F2fsWriter.CpPackTotalBlockCount;
  private const int SumJournalSize = F2fsWriter.SumJournalSize;
  private const int CompactSummaryBlockOffset = 1; // within a CP pack
  private const int NatJournalEntryBytes = 13; // nid(4) + version(1) + ino(4) + block_addr(4)
  private const int SitJournalEntryBytes = 78; // segno(4) + vblocks(2) + valid_map(64) + mtime(8)
  private const int NatJournalCapacity = (SumJournalSize - 2) / NatJournalEntryBytes; // 38
  private const int SitJournalCapacity = (SumJournalSize - 2) / SitJournalEntryBytes; // 6

  // Inline-dentry region inside an inode (kernel constants — see F2fsWriter).
  private const int InlineDentryStart = 364;
  private const int NrInlineDentry = F2fsWriter.NrInlineDentry; // 182
  private const int InlineBitmapSize = F2fsWriter.InlineDentryBitmapSize; // 23
  private const int InlineDentryReserved = F2fsWriter.InlineDentryReserved; // 7
  private const int InlineDentryBase = InlineDentryStart + InlineBitmapSize + InlineDentryReserved;
  private const int InlineNameBase = InlineDentryBase + NrInlineDentry * 11;

  // Regular (block-based) dentry block layout, matching writer.
  private const int NrDentryInBlock = F2fsWriter.NrDentryInBlock; // 214
  private const int DentryBlockBitmapSize = F2fsWriter.DentryBlockBitmapSize; // 27
  private const int DentryBlockReserved = F2fsWriter.DentryBlockReserved; // 3
  private const int DentryBlockEntryBase = DentryBlockBitmapSize + DentryBlockReserved; // 30
  private const int DentryBlockNameBase = DentryBlockEntryBase + NrDentryInBlock * 11; // 30 + 2354 = 2384

  // SIT entry: vblocks(2) + valid_map(64) + mtime(8) = 74 bytes; 4096/74 = 55 entries/block.
  private const int SitEntriesPerBlock = BlockSize / 74; // 55
  private const int SitEntryBytes = 74;
  // NAT entry: version(1) + ino(4) + block_addr(4) = 9 bytes; 4095/9 = 455 entries/block.
  private const int NatEntriesPerBlock = 4095 / 9; // 455

  // Inode field offsets.
  private const int InodeInlineFlagOff = 3;
  private const int InodeSizeOff = 16;
  private const int InodeBlocksOff = 24;
  private const int InodeIAddrOff = 360;

  /// <summary>
  /// Adds the named in-memory files to the root directory of <paramref name="image"/>
  /// using log-structured appends to the open current segments. Returns the mutated image.
  /// </summary>
  public static byte[] AddFiles(byte[] image, IReadOnlyList<(string Name, byte[] Data)> files) {
    if (files.Count == 0) return image;

    var disk = (byte[])image.Clone();
    var sb = ParseSuperblock(disk);
    var activeCp = ReadActiveCheckpoint(disk, sb);
    // Seed the target pack with the active pack's bytes so the NAT/SIT journal snapshots
    // and other CP block contents carry the latest accumulated state. Without this seed,
    // toggling between slots would lose every other Add's journal mirror entries.
    CopyActivePackToTargetSlot(disk, sb, activeCp);

    foreach (var (rawName, data) in files) {
      var name = rawName.Replace('\\', '/');
      if (name.Contains('/'))
        throw new NotSupportedException(
          "F2fs: in-place Add only supports root-level files. Subdirectory creation is genuinely out of scope.");

      AddOneFile(disk, sb, ref activeCp, name, data);
    }

    WriteCheckpoint(disk, sb, activeCp);
    return disk;
  }

  /// <summary>
  /// Removes the named entries from the root directory of <paramref name="image"/>:
  /// clears the dentry bitmap bits, invalidates on-disk NAT entries, clears the SIT
  /// valid_map bits for the inode + data blocks, and bumps the checkpoint. Returns the
  /// mutated image.
  /// </summary>
  public static byte[] RemoveFiles(byte[] image, IReadOnlyList<string> entryNames) {
    if (entryNames.Count == 0) return image;

    var disk = (byte[])image.Clone();
    var sb = ParseSuperblock(disk);
    var activeCp = ReadActiveCheckpoint(disk, sb);
    CopyActivePackToTargetSlot(disk, sb, activeCp);

    foreach (var rawName in entryNames) {
      var name = rawName.Replace('\\', '/');
      if (name.Contains('/'))
        throw new NotSupportedException(
          "F2fs: in-place Remove only supports root-level files. Nested removal is genuinely out of scope.");

      RemoveOneFile(disk, sb, ref activeCp, name);
    }

    WriteCheckpoint(disk, sb, activeCp);
    return disk;
  }

  // ────────────────────────────────────────────────────────────────────────
  // Internal model
  // ────────────────────────────────────────────────────────────────────────

  private sealed class Superblock {
    public int CpBlkAddr;
    public int SitBlkAddr;
    public int NatBlkAddr;
    public int SsaBlkAddr;
    public int MainBlkAddr;
    public int SegmentCountMain;
  }

  private sealed class CheckpointState {
    public int SlotIndex;           // 0 or 1 — which CP slot we'll write the updated state to.
    public int ActiveSlotIndex;     // the slot we read the active state from (= 1 - SlotIndex).
    public ulong CheckpointVersion; // new version we'll stamp.
    public ulong ValidBlockCount;
    public uint ValidNodeCount;
    public uint ValidInodeCount;
    public uint NextFreeNid;
    public int[] CurSegnos = new int[8];   // mirrors CurSegnos layout in writer.
    public int[] CurBlkoffs = new int[8];  // 0..2 data, 3..5 node.
    public ulong NowSecs;
    public uint FreeSegments;
  }

  // ────────────────────────────────────────────────────────────────────────
  // Superblock + checkpoint parsing
  // ────────────────────────────────────────────────────────────────────────

  private static Superblock ParseSuperblock(byte[] disk) {
    var sbOff = SuperOffset;
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(sbOff));
    if (magic != F2fsMagic)
      throw new InvalidDataException("F2fs: bad superblock magic.");

    return new Superblock {
      CpBlkAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(sbOff + 76)),
      SitBlkAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(sbOff + 80)),
      NatBlkAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(sbOff + 84)),
      SsaBlkAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(sbOff + 88)),
      MainBlkAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(sbOff + 92)),
      SegmentCountMain = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(sbOff + 68)),
    };
  }

  // Picks the CP pack with the higher checkpoint_ver as the active source, and returns the
  // OTHER slot index as the write target with version = active + 1. This matches the
  // kernel's alternating-checkpoint design — the previous active stays as the rollback.
  private static CheckpointState ReadActiveCheckpoint(byte[] disk, Superblock sb) {
    var cp0Off = sb.CpBlkAddr * BlockSize;
    var cp1Off = (sb.CpBlkAddr + BlocksPerSeg) * BlockSize;
    var ver0 = BinaryPrimitives.ReadUInt64LittleEndian(disk.AsSpan(cp0Off));
    var ver1 = BinaryPrimitives.ReadUInt64LittleEndian(disk.AsSpan(cp1Off));

    var activeOff = ver0 >= ver1 ? cp0Off : cp1Off;
    var activeVer = Math.Max(ver0, ver1);
    var targetSlot = ver0 >= ver1 ? 1 : 0;

    var s = new CheckpointState {
      SlotIndex = targetSlot,
      ActiveSlotIndex = 1 - targetSlot,
      CheckpointVersion = activeVer + 1,
      ValidBlockCount = BinaryPrimitives.ReadUInt64LittleEndian(disk.AsSpan(activeOff + 16)),
      ValidNodeCount = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(activeOff + 144)),
      ValidInodeCount = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(activeOff + 148)),
      NextFreeNid = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(activeOff + 152)),
      FreeSegments = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(activeOff + 32)),
      NowSecs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    // CurSegnos layout in writer: indices 0..2 = data (HOT/WARM/COLD), 3..5 = node (HOT/WARM/COLD).
    // On disk: cur_node_segno[8] at +36, cur_node_blkoff[8] at +68 — node 0..2 = HOT/WARM/COLD.
    //          cur_data_segno[8] at +84, cur_data_blkoff[8] at +116 — data 0..2 = HOT/WARM/COLD.
    for (var i = 0; i < 3; ++i) {
      s.CurSegnos[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(activeOff + 84 + i * 4));
      s.CurBlkoffs[i] = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(activeOff + 116 + i * 2));
      s.CurSegnos[3 + i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(activeOff + 36 + i * 4));
      s.CurBlkoffs[3 + i] = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(activeOff + 68 + i * 2));
    }

    return s;
  }

  // Copies the active CP pack's 6-block content into the target slot so the seed
  // carries the latest NAT/SIT journal snapshots. Without this seed, repeated
  // alternating writes would lose every other operation's journal entries.
  private static void CopyActivePackToTargetSlot(byte[] disk, Superblock sb, CheckpointState cp) {
    var activeBase = (sb.CpBlkAddr + cp.ActiveSlotIndex * BlocksPerSeg) * BlockSize;
    var targetBase = (sb.CpBlkAddr + cp.SlotIndex * BlocksPerSeg) * BlockSize;
    Buffer.BlockCopy(disk, activeBase, disk, targetBase, CpPackTotalBlockCount * BlockSize);
  }

  // ────────────────────────────────────────────────────────────────────────
  // Add: append data + node blocks to cursegs, update NAT/SIT, insert dentry.
  // ────────────────────────────────────────────────────────────────────────

  private static void AddOneFile(byte[] disk, Superblock sb, ref CheckpointState cp,
    string name, byte[] data) {

    // Resolve current segments (writer's CurSegnos order: HOT_DATA, WARM_DATA, COLD_DATA,
    // HOT_NODE, WARM_NODE, COLD_NODE → use WARM_DATA (idx 1) for file data and
    // WARM_NODE (idx 4) for the file inode, matching the writer's main-area placement).
    var warmDataIdx = F2fsWriter.CursegWarmData;     // 1
    var warmNodeIdx = F2fsWriter.CursegWarmNode;     // 4

    var blocksNeeded = data.Length == 0 ? 0 : (data.Length + BlockSize - 1) / BlockSize;

    // ── Allocate data blocks from the WARM_DATA curseg, advancing to a fresh segment ──
    // if the open one would overflow. The kernel does this exact thing in allocate_segment_by_default.
    var dataBlocks = new List<int>(blocksNeeded);
    for (var i = 0; i < blocksNeeded; ++i) {
      EnsureCursegHasRoom(disk, sb, cp, warmDataIdx, F2fsWriter.CursegWarmData);
      var blk = sb.MainBlkAddr + cp.CurSegnos[warmDataIdx] * BlocksPerSeg + cp.CurBlkoffs[warmDataIdx];
      Array.Clear(disk, blk * BlockSize, BlockSize);
      var len = Math.Min(BlockSize, data.Length - i * BlockSize);
      Buffer.BlockCopy(data, i * BlockSize, disk, blk * BlockSize, len);
      dataBlocks.Add(blk);

      // SIT: mark valid in the curseg's on-disk valid_map + bump vblocks.
      SitMarkBlockValid(disk, sb, cp.CurSegnos[warmDataIdx], cp.CurBlkoffs[warmDataIdx]);
      cp.CurBlkoffs[warmDataIdx] += 1;
    }

    // ── Allocate the inode block from the WARM_NODE curseg. ──────────────
    EnsureCursegHasRoom(disk, sb, cp, warmNodeIdx, F2fsWriter.CursegWarmNode);
    var inodeBlock = sb.MainBlkAddr + cp.CurSegnos[warmNodeIdx] * BlocksPerSeg + cp.CurBlkoffs[warmNodeIdx];
    var newNid = cp.NextFreeNid;

    // Write the inode block.
    WriteRegularFileInode(disk, inodeBlock * BlockSize, newNid, name, data.Length, dataBlocks, parentNid: RootIno);
    SitMarkBlockValid(disk, sb, cp.CurSegnos[warmNodeIdx], cp.CurBlkoffs[warmNodeIdx]);
    cp.CurBlkoffs[warmNodeIdx] += 1;

    // ── On-disk NAT entry for the new nid. ───────────────────────────────
    WriteNatEntry(disk, sb.NatBlkAddr, newNid, ino: newNid, blockAddr: (uint)inodeBlock);

    // ── Mirror in the NAT journal (best-effort). If the journal is full we silently fall
    //    through to "on-disk only" — fsck reads journal as overrides over on-disk, so the
    //    canonical NAT entry we just wrote is found either way. ──
    TryAddOrUpdateNatJournalEntry(disk, sb, cp, newNid, ino: newNid, blockAddr: (uint)inodeBlock);

    // ── SSA entries for the inode + data blocks. ─────────────────────────
    WriteSsaEntry(disk, sb, inodeBlock, newNid, ofsInNode: 0, isNode: true);
    for (var i = 0; i < dataBlocks.Count; ++i)
      WriteSsaEntry(disk, sb, dataBlocks[i], newNid, ofsInNode: (ushort)i, isNode: false);

    // ── Insert root dentry (inline or, after conversion, a regular dentry block). ──
    InsertRootDentry(disk, sb, cp, newNid, name, FtRegFile);

    // ── Advance cursors + counts. ────────────────────────────────────────
    cp.NextFreeNid = newNid + 1;
    cp.ValidNodeCount += 1;
    cp.ValidInodeCount += 1;
    cp.ValidBlockCount += (ulong)(1 + blocksNeeded);

    // Mirror the SIT updates for the two cursegs we touched into the SIT journal too,
    // so the journal snapshot agrees with the on-disk SIT.
    UpdateSitJournalForCurseg(disk, sb, cp, warmDataIdx);
    UpdateSitJournalForCurseg(disk, sb, cp, warmNodeIdx);
  }

  // ────────────────────────────────────────────────────────────────────────
  // Remove: clear dentry, invalidate NAT, clear SIT bits, decrement counts.
  // ────────────────────────────────────────────────────────────────────────

  private static void RemoveOneFile(byte[] disk, Superblock sb, ref CheckpointState cp, string name) {
    // 1) Find dentry in root's dentry storage (inline or regular block-based) + capture nid.
    var rootInodeBlock = LookupNat(disk, sb, RootIno);
    if (rootInodeBlock <= 0)
      throw new InvalidDataException("F2fs: root NAT entry missing.");

    var rootInodeOff = rootInodeBlock * BlockSize;
    if (!FindRootDentry(disk, rootInodeOff, name, out var nid, out var loc))
      return; // not present → nothing to do (idempotent).

    // 2) Look up the inode block via NAT.
    var inodeBlock = LookupNat(disk, sb, nid);
    if (inodeBlock <= 0) {
      // Stale dentry pointing at unmapped nid — still clear the bitmap and move on.
      ClearRootDentry(disk, loc);
      return;
    }

    // 3) Collect data block addresses from i_addr[].
    var iAddrOff = inodeBlock * BlockSize + InodeIAddrOff;
    var dataBlocks = new List<int>();
    for (var i = 0; i < AddrsPerInode; ++i) {
      var addr = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(iAddrOff + i * 4));
      if (addr > 0) dataBlocks.Add(addr);
    }

    // 4) Clear dentry bitmap bits + entry bytes.
    ClearRootDentry(disk, loc);

    // 5) Invalidate on-disk NAT entry for the nid (block_addr=0, ino=0 — recycles the nid).
    WriteNatEntry(disk, sb.NatBlkAddr, nid, ino: 0, blockAddr: 0);
    TryAddOrUpdateNatJournalEntry(disk, sb, cp, nid, ino: 0, blockAddr: 0);

    // 6) Clear SIT valid_map bits + decrement vblocks for the inode block + data blocks.
    ClearOneBlockInSit(disk, sb, inodeBlock);
    foreach (var blk in dataBlocks)
      ClearOneBlockInSit(disk, sb, blk);

    // Zero the inode block + data blocks so the bytes are physically gone (wipe).
    Array.Clear(disk, inodeBlock * BlockSize, BlockSize);
    foreach (var blk in dataBlocks)
      Array.Clear(disk, blk * BlockSize, BlockSize);

    // 7) Update counts.
    cp.ValidNodeCount = cp.ValidNodeCount > 0 ? cp.ValidNodeCount - 1 : 0;
    cp.ValidInodeCount = cp.ValidInodeCount > 0 ? cp.ValidInodeCount - 1 : 0;
    var freed = (ulong)(1 + dataBlocks.Count);
    cp.ValidBlockCount = cp.ValidBlockCount > freed ? cp.ValidBlockCount - freed : 0;

    // Mirror the affected cursegs' SIT entries into the SIT journal if applicable.
    for (var idx = 0; idx < 6; ++idx)
      UpdateSitJournalForCurseg(disk, sb, cp, idx);
  }

  // ────────────────────────────────────────────────────────────────────────
  // Curseg management
  // ────────────────────────────────────────────────────────────────────────

  // Ensures the current segment for cursegIdx has at least 1 free block. When the open
  // segment is full, picks a free main-area segment, types it correctly in the SIT, and
  // makes it the new current segment with blkoff=0. This mirrors the kernel's
  // allocate_segment_by_default policy: when CURSEG_* is exhausted, allocate a fresh one.
  private static void EnsureCursegHasRoom(byte[] disk, Superblock sb, CheckpointState cp,
    int cursegIdx, int cursegType) {
    if (cp.CurBlkoffs[cursegIdx] < BlocksPerSeg)
      return;

    // Find a free segment in the main area: vblocks==0 and not already a curseg.
    var inUse = new HashSet<int>();
    for (var i = 0; i < cp.CurSegnos.Length; ++i)
      inUse.Add(cp.CurSegnos[i]);

    var freeSeg = -1;
    for (var s = 0; s < sb.SegmentCountMain; ++s) {
      if (inUse.Contains(s)) continue;
      var (_, vblocks, _) = ReadSitOnDisk(disk, sb, s);
      if (vblocks == 0) { freeSeg = s; break; }
    }
    if (freeSeg < 0)
      throw new NotSupportedException(
        $"F2fs: main-area segment count exhausted — no free segment available to promote to curseg "
        + $"type {cursegType}. Growing the image is genuinely out of scope (would require resizing "
        + $"the SB, NAT, SIT and SSA areas).");

    // Type the new curseg correctly: store type in the high bits of vblocks.
    var (sitOff, _, _) = ReadSitOnDisk(disk, sb, freeSeg);
    WriteSitOnDiskVblocks(disk, sitOff, cursegType, vblocks: 0,
      mtime: (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    cp.CurSegnos[cursegIdx] = freeSeg;
    cp.CurBlkoffs[cursegIdx] = 0;
    if (cp.FreeSegments > 0) cp.FreeSegments -= 1;

    // Refresh the SIT journal mirror for this curseg (entry tracks the new segno).
    UpdateSitJournalForCurseg(disk, sb, cp, cursegIdx);
  }

  // ────────────────────────────────────────────────────────────────────────
  // Root dentry helpers — supports inline AND regular (block-based) layouts.
  // ────────────────────────────────────────────────────────────────────────

  private readonly record struct DentryLocation(
    bool IsInline,
    int BitmapAbsOff,
    int EntryAbsOff,
    int NameAbsOff,
    int Slot,
    int SlotsConsumed,
    int SlotLenBase);

  private static bool FindRootDentry(byte[] disk, int rootInodeOff, string name,
    out uint nid, out DentryLocation loc) {
    var inlineFlag = disk[rootInodeOff + InodeInlineFlagOff];
    if ((inlineFlag & F2fsInlineDentry) != 0)
      return FindInlineDentry(disk, rootInodeOff, name, out nid, out loc);

    // Non-inline: walk regular dentry blocks in i_addr[].
    return FindBlockDentry(disk, rootInodeOff, name, out nid, out loc);
  }

  private static void ClearRootDentry(byte[] disk, DentryLocation loc) {
    for (var k = 0; k < loc.SlotsConsumed; ++k) {
      var b = loc.Slot + k;
      disk[loc.BitmapAbsOff + b / 8] &= (byte)~(1 << (b % 8));
    }
    Array.Clear(disk, loc.EntryAbsOff + loc.Slot * 11, 11);
    Array.Clear(disk, loc.NameAbsOff + loc.Slot * SlotLen, loc.SlotsConsumed * SlotLen);
  }

  private static bool FindInlineDentry(byte[] disk, int inodeOff, string name,
    out uint nid, out DentryLocation loc) {
    nid = 0;
    var bitmapOff = inodeOff + InlineDentryStart;
    var dentryOff = inodeOff + InlineDentryBase;
    var nameOff = inodeOff + InlineNameBase;
    loc = default;

    for (var i = 0; i < NrInlineDentry;) {
      var bit = (disk[bitmapOff + i / 8] >> (i % 8)) & 1;
      if (bit == 0) { ++i; continue; }

      var entryOff = dentryOff + i * 11;
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(entryOff + 4));
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(entryOff + 8));
      var slots = nameLen <= 0 ? 1 : (nameLen + SlotLen - 1) / SlotLen;

      if (ino != 0 && nameLen > 0 && nameLen <= 255) {
        var fnOff = nameOff + i * SlotLen;
        var thisName = Encoding.UTF8.GetString(disk, fnOff, Math.Min((int)nameLen, NrInlineDentry * SlotLen - i * SlotLen));
        if (thisName == name) {
          nid = ino;
          loc = new DentryLocation(IsInline: true,
            BitmapAbsOff: bitmapOff, EntryAbsOff: dentryOff, NameAbsOff: nameOff,
            Slot: i, SlotsConsumed: slots, SlotLenBase: SlotLen);
          return true;
        }
      }
      i += slots;
    }
    return false;
  }

  // Walk every populated i_addr[pgofs] dentry block until the name is found.
  private static bool FindBlockDentry(byte[] disk, int inodeOff, string name,
    out uint nid, out DentryLocation loc) {
    nid = 0;
    loc = default;
    for (var pgofs = 0; pgofs < AddrsPerInode; ++pgofs) {
      var addrOff = inodeOff + InodeIAddrOff + pgofs * 4;
      var dataBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(addrOff));
      if (dataBlock == 0) continue;
      var blockOff = dataBlock * BlockSize;
      var bitmapOff = blockOff;
      var entryOff = blockOff + DentryBlockEntryBase;
      var nameOff = blockOff + DentryBlockNameBase;

      for (var i = 0; i < NrDentryInBlock;) {
        var bit = (disk[bitmapOff + i / 8] >> (i % 8)) & 1;
        if (bit == 0) { ++i; continue; }

        var thisEntryOff = entryOff + i * 11;
        var ino = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(thisEntryOff + 4));
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(thisEntryOff + 8));
        var slots = nameLen <= 0 ? 1 : (nameLen + SlotLen - 1) / SlotLen;

        if (ino != 0 && nameLen > 0 && nameLen <= 255) {
          var fnOff = nameOff + i * SlotLen;
          var maxNameSpan = NrDentryInBlock * SlotLen - i * SlotLen;
          var thisName = Encoding.UTF8.GetString(disk, fnOff, Math.Min((int)nameLen, maxNameSpan));
          if (thisName == name) {
            nid = ino;
            loc = new DentryLocation(IsInline: false,
              BitmapAbsOff: bitmapOff, EntryAbsOff: entryOff, NameAbsOff: nameOff,
              Slot: i, SlotsConsumed: slots, SlotLenBase: SlotLen);
            return true;
          }
        }
        i += slots;
      }
    }
    return false;
  }

  // Inserts a single child dentry into the root directory. Uses inline storage when the
  // root still carries F2FS_INLINE_DENTRY; on inline-region overflow, performs an
  // in-place conversion to a regular dentry block (allocates one HOT_DATA block, migrates
  // every existing inline dentry into it, clears the inline flag and points i_addr[0] at
  // the new block), and then writes the new dentry into that block.
  private static void InsertRootDentry(byte[] disk, Superblock sb, CheckpointState cp,
    uint childNid, string name, byte fileType) {
    var rootInodeBlock = LookupNat(disk, sb, RootIno);
    if (rootInodeBlock <= 0)
      throw new InvalidDataException("F2fs: root NAT entry missing.");

    var rootInodeOff = rootInodeBlock * BlockSize;
    var inlineFlag = disk[rootInodeOff + InodeInlineFlagOff];

    if ((inlineFlag & F2fsInlineDentry) != 0) {
      if (TryInsertInline(disk, rootInodeOff, childNid, name, fileType))
        return;
      // Inline region full → convert to regular dentry block and fall through.
      ConvertRootToRegularDentry(disk, sb, cp, rootInodeBlock);
    }

    // Non-inline: write into a regular dentry block (allocating one if needed).
    InsertIntoBlockDentry(disk, sb, cp, rootInodeBlock, childNid, name, fileType);
  }

  private static bool TryInsertInline(byte[] disk, int inodeOff,
    uint childNid, string name, byte fileType) {
    var bitmapOff = inodeOff + InlineDentryStart;
    var dentryOff = inodeOff + InlineDentryBase;
    var nameOff = inodeOff + InlineNameBase;

    var nameBytes = Encoding.UTF8.GetBytes(name);
    var nameLen = Math.Min(nameBytes.Length, 255);
    var slotsNeeded = Math.Max(1, (nameLen + SlotLen - 1) / SlotLen);

    var free = FindFreeRun(disk, bitmapOff, NrInlineDentry, slotsNeeded);
    if (free < 0) return false;

    WriteDentryAtSlot(disk, bitmapOff, dentryOff, nameOff,
      free, slotsNeeded, childNid, nameBytes, nameLen, fileType);
    return true;
  }

  // Convert the root inode from inline-dentry to a regular block-based dentry directory.
  // Allocates one fresh HOT_DATA block, copies every inline dentry (including "." and "..")
  // into it using the regular dentry-block layout, clears the inline-dentry flag on the
  // inode, writes the new block address into i_addr[0] (pgofs 0), bumps i_size to one
  // block and i_blocks to 2, and refreshes SIT/SSA/NAT-journal for the new block.
  private static void ConvertRootToRegularDentry(byte[] disk, Superblock sb, CheckpointState cp,
    int rootInodeBlock) {
    var hotDataIdx = F2fsWriter.CursegHotData; // 0
    EnsureCursegHasRoom(disk, sb, cp, hotDataIdx, F2fsWriter.CursegHotData);

    var newBlock = sb.MainBlkAddr + cp.CurSegnos[hotDataIdx] * BlocksPerSeg + cp.CurBlkoffs[hotDataIdx];
    var newBlockOff = newBlock * BlockSize;
    Array.Clear(disk, newBlockOff, BlockSize);

    var rootInodeOff = rootInodeBlock * BlockSize;
    var bitmapOff = rootInodeOff + InlineDentryStart;
    var dentryOff = rootInodeOff + InlineDentryBase;
    var nameOff = rootInodeOff + InlineNameBase;

    var dstBitmapOff = newBlockOff;
    var dstEntryOff = newBlockOff + DentryBlockEntryBase;
    var dstNameOff = newBlockOff + DentryBlockNameBase;

    // Migrate every populated inline dentry — preserving the original logical order so
    // "." / ".." (which the writer placed at slots 0/1) stay at slots 0/1 in the new block.
    var dstSlot = 0;
    for (var i = 0; i < NrInlineDentry;) {
      var bit = (disk[bitmapOff + i / 8] >> (i % 8)) & 1;
      if (bit == 0) { ++i; continue; }

      var entryOff = dentryOff + i * 11;
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(entryOff + 8));
      var slots = nameLen <= 0 ? 1 : (nameLen + SlotLen - 1) / SlotLen;

      if (nameLen <= 0 || nameLen > 255) { i += slots; continue; }

      if (dstSlot + slots > NrDentryInBlock)
        throw new InvalidOperationException(
          "F2fs: regular dentry block cannot hold the migrated inline dentries (capacity 214 slots).");

      // Copy the 11-byte dir entry.
      Buffer.BlockCopy(disk, entryOff, disk, dstEntryOff + dstSlot * 11, 11);
      // Copy the filename slot range.
      Buffer.BlockCopy(disk, nameOff + i * SlotLen,
        disk, dstNameOff + dstSlot * SlotLen, slots * SlotLen);
      // Mark bitmap bits.
      for (var k = 0; k < slots; ++k) {
        var b = dstSlot + k;
        disk[dstBitmapOff + b / 8] |= (byte)(1 << (b % 8));
      }
      dstSlot += slots;
      i += slots;
    }

    // Clear inline-dentry storage in the root inode (bitmap + entries + names + reserved).
    Array.Clear(disk, bitmapOff, InlineBitmapSize + InlineDentryReserved
      + NrInlineDentry * 11 + NrInlineDentry * SlotLen);

    // Flip the inline flag bits: drop INLINE_DENTRY, keep DATA_EXIST (kernel invariant).
    var newFlag = (byte)((disk[rootInodeOff + InodeInlineFlagOff] & ~F2fsInlineDentry) | F2fsDataExist);
    disk[rootInodeOff + InodeInlineFlagOff] = newFlag;

    // i_addr[0] (pgofs 0) → new dentry block address. Earlier writer convention stored
    // the inline-reserved sentinel in i_addr[0] as zero; reuse that slot.
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(rootInodeOff + InodeIAddrOff),
      (uint)newBlock);

    // i_size = one block; i_blocks = 2 (inode + one data block).
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(rootInodeOff + InodeSizeOff), (ulong)BlockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(rootInodeOff + InodeBlocksOff), 2UL);

    // Update SIT for the new block + SSA so fsck cross-checks pass.
    SitMarkBlockValid(disk, sb, cp.CurSegnos[hotDataIdx], cp.CurBlkoffs[hotDataIdx]);
    WriteSsaEntry(disk, sb, newBlock, RootIno, ofsInNode: 0, isNode: false);
    cp.CurBlkoffs[hotDataIdx] += 1;
    cp.ValidBlockCount += 1; // the new dentry block.
    UpdateSitJournalForCurseg(disk, sb, cp, hotDataIdx);
  }

  // Inserts a dentry into a non-inline directory by finding a free slot in any populated
  // dentry block (i_addr[]) or allocating a new HOT_DATA block when none has room.
  private static void InsertIntoBlockDentry(byte[] disk, Superblock sb, CheckpointState cp,
    int rootInodeBlock, uint childNid, string name, byte fileType) {
    var rootInodeOff = rootInodeBlock * BlockSize;
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var nameLen = Math.Min(nameBytes.Length, 255);
    var slotsNeeded = Math.Max(1, (nameLen + SlotLen - 1) / SlotLen);

    // 1) Try existing dentry blocks first.
    for (var pgofs = 0; pgofs < AddrsPerInode; ++pgofs) {
      var addrOff = rootInodeOff + InodeIAddrOff + pgofs * 4;
      var dataBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(addrOff));
      if (dataBlock == 0) continue;
      var blockOff = dataBlock * BlockSize;
      var bitmapOff = blockOff;
      var entryOff = blockOff + DentryBlockEntryBase;
      var nameOff = blockOff + DentryBlockNameBase;
      var free = FindFreeRun(disk, bitmapOff, NrDentryInBlock, slotsNeeded);
      if (free >= 0) {
        WriteDentryAtSlot(disk, bitmapOff, entryOff, nameOff, free, slotsNeeded,
          childNid, nameBytes, nameLen, fileType);
        return;
      }
    }

    // 2) Allocate a fresh HOT_DATA block and point i_addr[pgofs] at it.
    var hotDataIdx = F2fsWriter.CursegHotData;
    EnsureCursegHasRoom(disk, sb, cp, hotDataIdx, F2fsWriter.CursegHotData);

    var newBlock = sb.MainBlkAddr + cp.CurSegnos[hotDataIdx] * BlocksPerSeg + cp.CurBlkoffs[hotDataIdx];
    var newBlockOff = newBlock * BlockSize;
    Array.Clear(disk, newBlockOff, BlockSize);

    // Find an empty i_addr[] slot to point at the new block.
    var pgofsFree = -1;
    for (var pgofs = 0; pgofs < AddrsPerInode; ++pgofs) {
      var addrOff = rootInodeOff + InodeIAddrOff + pgofs * 4;
      if (BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(addrOff)) == 0) {
        pgofsFree = pgofs; break;
      }
    }
    if (pgofsFree < 0)
      throw new NotSupportedException(
        "F2fs: root directory i_addr[] is full (923 direct pointers exhausted) — indirect node "
        + "blocks would be needed; multi-level indirect inode trees are genuinely out of scope.");

    BinaryPrimitives.WriteUInt32LittleEndian(
      disk.AsSpan(rootInodeOff + InodeIAddrOff + pgofsFree * 4), (uint)newBlock);

    // Write the dentry into slot 0 of the new block.
    var bitmap = newBlockOff;
    var entry = newBlockOff + DentryBlockEntryBase;
    var nm = newBlockOff + DentryBlockNameBase;
    WriteDentryAtSlot(disk, bitmap, entry, nm, slot: 0, slotsConsumed: slotsNeeded,
      childNid, nameBytes, nameLen, fileType);

    // SIT + SSA + counts.
    SitMarkBlockValid(disk, sb, cp.CurSegnos[hotDataIdx], cp.CurBlkoffs[hotDataIdx]);
    WriteSsaEntry(disk, sb, newBlock, RootIno, ofsInNode: (ushort)pgofsFree, isNode: false);
    cp.CurBlkoffs[hotDataIdx] += 1;
    cp.ValidBlockCount += 1;

    // Bump i_size + i_blocks.
    var sizeOff = rootInodeOff + InodeSizeOff;
    var newSize = (ulong)((long)(pgofsFree + 1) * BlockSize);
    var curSize = BinaryPrimitives.ReadUInt64LittleEndian(disk.AsSpan(sizeOff));
    if (newSize > curSize)
      BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(sizeOff), newSize);

    var blocksOff = rootInodeOff + InodeBlocksOff;
    var curBlocks = BinaryPrimitives.ReadUInt64LittleEndian(disk.AsSpan(blocksOff));
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(blocksOff), curBlocks + 1);

    UpdateSitJournalForCurseg(disk, sb, cp, hotDataIdx);
  }

  // Finds a run of `slotsNeeded` consecutive cleared bits in the dentry bitmap.
  private static int FindFreeRun(byte[] disk, int bitmapAbsOff, int nrSlots, int slotsNeeded) {
    for (var i = 0; i <= nrSlots - slotsNeeded; ++i) {
      var ok = true;
      for (var k = 0; k < slotsNeeded; ++k) {
        var b = i + k;
        if (((disk[bitmapAbsOff + b / 8] >> (b % 8)) & 1) != 0) { ok = false; break; }
      }
      if (ok) return i;
    }
    return -1;
  }

  private static void WriteDentryAtSlot(byte[] disk, int bitmapAbsOff, int entryAbsOff,
    int nameAbsOff, int slot, int slotsConsumed, uint childNid, byte[] nameBytes,
    int nameLen, byte fileType) {
    // Mark bitmap bits.
    for (var k = 0; k < slotsConsumed; ++k) {
      var b = slot + k;
      disk[bitmapAbsOff + b / 8] |= (byte)(1 << (b % 8));
    }

    // Write the f2fs_dir_entry.
    var entryOff = entryAbsOff + slot * 11;
    var hash = F2fsWriter.F2fsNameHash(nameBytes.AsSpan(0, nameLen));
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(entryOff), hash);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(entryOff + 4), childNid);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(entryOff + 8), (ushort)nameLen);
    disk[entryOff + 10] = fileType;

    // Write the filename across consecutive slots.
    nameBytes.AsSpan(0, nameLen).CopyTo(disk.AsSpan(nameAbsOff + slot * SlotLen));
  }

  // ────────────────────────────────────────────────────────────────────────
  // NAT helpers
  // ────────────────────────────────────────────────────────────────────────

  private static int LookupNat(byte[] disk, Superblock sb, uint nid) {
    var natBlock = (int)(nid / NatEntriesPerBlock);
    var natIdx = (int)(nid % NatEntriesPerBlock);
    var off = (sb.NatBlkAddr + natBlock) * BlockSize + natIdx * 9;
    if (off + 9 > disk.Length) return -1;
    return (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(off + 5));
  }

  private static void WriteNatEntry(byte[] disk, int natBlkAddr, uint nid, uint ino, uint blockAddr) {
    var natBlock = (int)(nid / NatEntriesPerBlock);
    var natIdx = (int)(nid % NatEntriesPerBlock);
    var off = (natBlkAddr + natBlock) * BlockSize + natIdx * 9;
    disk[off] = 1; // version
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(off + 1), ino);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(off + 5), blockAddr);
  }

  // Best-effort journal mirror: when the compact summary block's NAT journal is full we
  // silently skip the mirror. The on-disk NAT entry (already written above) is then the
  // sole source. f2fs-tools' build_nat_area_bitmap treats journal entries as overrides
  // over on-disk, so a missing journal entry simply falls through to disk — no
  // inconsistency. This is the kernel's natural NAT-flush behaviour expressed in
  // checkpoint terms: a full journal is flushed by writing the on-disk NAT (which we
  // already did unconditionally) and dropping the in-memory entries.
  private static void TryAddOrUpdateNatJournalEntry(byte[] disk, Superblock sb, CheckpointState cp,
    uint nid, uint ino, uint blockAddr) {

    var packBase = sb.CpBlkAddr + cp.SlotIndex * BlocksPerSeg;
    var summaryBlockOff = (packBase + CompactSummaryBlockOffset) * BlockSize;

    var nNats = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(summaryBlockOff));
    // Search for existing entry for this nid → update in place.
    for (var i = 0; i < nNats; ++i) {
      var entOff = summaryBlockOff + 2 + i * NatJournalEntryBytes;
      var existingNid = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(entOff));
      if (existingNid == nid) {
        disk[entOff + 4] = 1; // version
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(entOff + 5), ino);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(entOff + 9), blockAddr);
        return;
      }
    }

    if (nNats >= NatJournalCapacity)
      return; // journal full → fall through to on-disk NAT (already written).

    var newOff = summaryBlockOff + 2 + nNats * NatJournalEntryBytes;
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(newOff), nid);
    disk[newOff + 4] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(newOff + 5), ino);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(newOff + 9), blockAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(summaryBlockOff), (ushort)(nNats + 1));
  }

  // ────────────────────────────────────────────────────────────────────────
  // SIT helpers
  // ────────────────────────────────────────────────────────────────────────

  private static (int Off, ushort Vblocks, int Type) ReadSitOnDisk(byte[] disk, Superblock sb, int segno) {
    var sitBlock = segno / SitEntriesPerBlock;
    var sitIdx = segno % SitEntriesPerBlock;
    var off = (sb.SitBlkAddr + sitBlock) * BlockSize + sitIdx * SitEntryBytes;
    var vblocksField = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(off));
    var vblocks = (ushort)(vblocksField & 0x3FF);
    var type = vblocksField >> 10;
    return (off, vblocks, type);
  }

  private static void WriteSitOnDiskVblocks(byte[] disk, int sitOff, int type, ushort vblocks, ulong mtime) {
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(sitOff), (ushort)((type << 10) | (vblocks & 0x3FF)));
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(sitOff + 66), mtime);
  }

  private static void SitMarkBlockValid(byte[] disk, Superblock sb, int segno, int blkInSeg) {
    var (off, vblocks, type) = ReadSitOnDisk(disk, sb, segno);
    var mapByte = off + 2 + blkInSeg / 8;
    var mask = (byte)(1 << (7 - blkInSeg % 8)); // MSB-first per F2FS f2fs_set_bit.
    if ((disk[mapByte] & mask) == 0) {
      disk[mapByte] |= mask;
      vblocks = (ushort)(vblocks + 1);
    }
    WriteSitOnDiskVblocks(disk, off, type, vblocks, mtime: (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
  }

  private static void ClearOneBlockInSit(byte[] disk, Superblock sb, int absoluteBlock) {
    var rel = absoluteBlock - sb.MainBlkAddr;
    if (rel < 0) return;
    var segno = rel / BlocksPerSeg;
    var blkInSeg = rel % BlocksPerSeg;
    if (segno >= sb.SegmentCountMain) return;

    var (off, vblocks, type) = ReadSitOnDisk(disk, sb, segno);
    var mapByte = off + 2 + blkInSeg / 8;
    var mask = (byte)(1 << (7 - blkInSeg % 8));
    if ((disk[mapByte] & mask) != 0) {
      disk[mapByte] &= (byte)~mask;
      vblocks = vblocks > 0 ? (ushort)(vblocks - 1) : (ushort)0;
    }
    WriteSitOnDiskVblocks(disk, off, type, vblocks, mtime: (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
  }

  // Mirrors the on-disk SIT entry for cp.CurSegnos[cursegIdx] into the SIT journal
  // snapshot in the target CP pack so fsck's journal-takes-precedence merge agrees.
  // When the journal is full and the segno isn't already present we silently drop the
  // mirror — the on-disk SIT (already written above) is then authoritative.
  private static void UpdateSitJournalForCurseg(byte[] disk, Superblock sb, CheckpointState cp, int cursegIdx) {
    var segno = cp.CurSegnos[cursegIdx];
    var (sitOff, vblocks, type) = ReadSitOnDisk(disk, sb, segno);
    var validMap = disk.AsSpan(sitOff + 2, 64);
    var mtime = BinaryPrimitives.ReadUInt64LittleEndian(disk.AsSpan(sitOff + 66));

    var packBase = sb.CpBlkAddr + cp.SlotIndex * BlocksPerSeg;
    var summaryBlockOff = (packBase + CompactSummaryBlockOffset) * BlockSize;
    var sitJournalOff = summaryBlockOff + SumJournalSize;
    var nSits = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(sitJournalOff));

    // Find existing snapshot for this segno (the writer seeded one per curseg).
    for (var i = 0; i < nSits; ++i) {
      var entOff = sitJournalOff + 2 + i * SitJournalEntryBytes;
      var existing = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(entOff));
      if (existing == segno) {
        var seOff = entOff + 4;
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(seOff), (ushort)((type << 10) | (vblocks & 0x3FF)));
        validMap.CopyTo(disk.AsSpan(seOff + 2, 64));
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(seOff + 66), mtime);
        return;
      }
    }

    if (nSits >= SitJournalCapacity)
      return; // journal full → fall through to on-disk SIT (already written).

    var newOff = sitJournalOff + 2 + nSits * SitJournalEntryBytes;
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(newOff), (uint)segno);
    var seNewOff = newOff + 4;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(seNewOff), (ushort)((type << 10) | (vblocks & 0x3FF)));
    validMap.CopyTo(disk.AsSpan(seNewOff + 2, 64));
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(seNewOff + 66), mtime);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(sitJournalOff), (ushort)(nSits + 1));
  }

  // ────────────────────────────────────────────────────────────────────────
  // SSA helpers
  // ────────────────────────────────────────────────────────────────────────

  private static void WriteSsaEntry(byte[] disk, Superblock sb, int absoluteBlock, uint nid, ushort ofsInNode, bool isNode) {
    var rel = absoluteBlock - sb.MainBlkAddr;
    if (rel < 0) return;
    var segno = rel / BlocksPerSeg;
    var blkInSeg = rel % BlocksPerSeg;
    if (segno >= sb.SegmentCountMain) return;

    var ssaBlockOff = (sb.SsaBlkAddr + segno) * BlockSize;
    var entryOff = ssaBlockOff + blkInSeg * 7;
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(entryOff), nid);
    disk[entryOff + 4] = 1; // version
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(entryOff + 5), ofsInNode);

    // Footer at end of SSA block: entry_type(1) at offset SUM_ENTRIES_SIZE + SUM_JOURNAL_SIZE.
    const int footerTypeOff = 7 * 512 + 507; // 4091
    disk[ssaBlockOff + footerTypeOff] = (byte)(isNode ? 1 : 0);
  }

  // ────────────────────────────────────────────────────────────────────────
  // Inode block writer (regular file). Mirrors F2fsWriter.WriteRegularFileInode.
  // ────────────────────────────────────────────────────────────────────────

  private static void WriteRegularFileInode(byte[] disk, int off, uint ino, string name,
    int size, IReadOnlyList<int> dataBlocks, uint parentNid) {

    Array.Clear(disk, off, BlockSize);
    var s = disk.AsSpan(off, BlockSize);

    BinaryPrimitives.WriteUInt16LittleEndian(s[0..], 0x81A4); // i_mode: S_IFREG | 0644
    BinaryPrimitives.WriteUInt32LittleEndian(s[12..], 1);     // i_links
    BinaryPrimitives.WriteUInt64LittleEndian(s[16..], (ulong)size);
    BinaryPrimitives.WriteUInt64LittleEndian(s[24..], (ulong)(1 + dataBlocks.Count));
    var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt64LittleEndian(s[32..], now);
    BinaryPrimitives.WriteUInt64LittleEndian(s[40..], now);
    BinaryPrimitives.WriteUInt64LittleEndian(s[48..], now);
    BinaryPrimitives.WriteUInt32LittleEndian(s[84..], parentNid);

    var nameBytes = Encoding.UTF8.GetBytes(name);
    var namelen = Math.Min(nameBytes.Length, 255);
    BinaryPrimitives.WriteUInt32LittleEndian(s[88..], (uint)namelen);
    nameBytes.AsSpan(0, namelen).CopyTo(s[92..]);

    const int iAddrOff = 360;
    for (var i = 0; i < dataBlocks.Count; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(s[(iAddrOff + i * 4)..], (uint)dataBlocks[i]);

    // Node footer at block end (24 bytes).
    var footerOff = BlockSize - 24;
    BinaryPrimitives.WriteUInt32LittleEndian(s[footerOff..], ino);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(footerOff + 4)..], ino);
    BinaryPrimitives.WriteUInt64LittleEndian(s[(footerOff + 12)..], 1UL);
  }

  // ────────────────────────────────────────────────────────────────────────
  // Curseg summary writers — populate the data summaries in the compact
  // summary block (CP block 1) and the per-type node summary blocks (CP
  // blocks 2/3/4). f2fs-tools' restore_curseg_summaries reads these to seed
  // curseg->sum_blk and then is_valid_ssa_* checks every reachable block
  // against that buffer (not the on-disk SSA) for current segments.
  // ────────────────────────────────────────────────────────────────────────

  // Writes the HOT/WARM/COLD_DATA curseg summary entries into the compact
  // summary block (block 1 of the target CP pack) at offset 2*SUM_JOURNAL_SIZE
  // onwards. Each entry is {nid, version, ofs_in_node} (7 bytes) — sourced
  // directly from the on-disk SSA we already wrote for each block in each
  // curseg. The kernel layout: HOT_DATA first up to CurBlkoffs[0], then
  // WARM_DATA up to CurBlkoffs[1], then COLD_DATA up to CurBlkoffs[2].
  private static void WriteCompactDataSummaries(byte[] disk, Superblock sb, CheckpointState cp) {
    var packBase = sb.CpBlkAddr + cp.SlotIndex * BlocksPerSeg;
    var compactBlockOff = (packBase + CompactSummaryBlockOffset) * BlockSize;
    var writeOff = compactBlockOff + 2 * SumJournalSize;
    const int summaryBytes = 7;
    var blockEnd = compactBlockOff + BlockSize - 5; // - SUM_FOOTER_SIZE

    for (var dataIdx = 0; dataIdx < 3; ++dataIdx) {
      var segno = cp.CurSegnos[dataIdx];
      var blkOff = cp.CurBlkoffs[dataIdx];
      // Source: on-disk SSA for that segment, blocks 0..blkOff-1.
      var ssaBlockOff = (sb.SsaBlkAddr + segno) * BlockSize;
      for (var b = 0; b < blkOff; ++b) {
        if (writeOff + summaryBytes > blockEnd)
          // No more room in compact block — the kernel would spill into a
          // following block but our 6-block CP pack has no spare. We stop
          // mirroring here; the on-disk SSA still describes the block, and
          // fsck's worst case is treating excess as "current segment empty"
          // (which round-trips as a non-current segment described by SSA).
          return;

        var srcEntryOff = ssaBlockOff + b * 7;
        Buffer.BlockCopy(disk, srcEntryOff, disk, writeOff, summaryBytes);
        writeOff += summaryBytes;
      }
    }
  }

  // Writes the HOT/WARM/COLD_NODE curseg summary blocks into blocks 2/3/4 of
  // the target CP pack. Each block holds up to 512 entries (one per logical
  // block position in the curseg). Entry source is the on-disk SSA: each
  // 7-byte entry mirrors what fsck would compute from the node block's
  // footer.
  private static void WriteNodeSummaries(byte[] disk, Superblock sb, CheckpointState cp) {
    var packBase = sb.CpBlkAddr + cp.SlotIndex * BlocksPerSeg;
    for (var nodeIdx = 0; nodeIdx < 3; ++nodeIdx) {
      var segno = cp.CurSegnos[3 + nodeIdx];
      var blkOff = cp.CurBlkoffs[3 + nodeIdx];
      var nodeSumBlockOff = (packBase + 2 + nodeIdx) * BlockSize;

      // Clear the entries region (3584 bytes for ENTRIES_IN_SUM=512 × 7).
      Array.Clear(disk, nodeSumBlockOff, BlocksPerSeg > 0 ? 3584 : 0);

      var ssaBlockOff = (sb.SsaBlkAddr + segno) * BlockSize;
      for (var b = 0; b < blkOff && b < 512; ++b) {
        var srcEntryOff = ssaBlockOff + b * 7;
        var dstEntryOff = nodeSumBlockOff + b * 7;
        Buffer.BlockCopy(disk, srcEntryOff, disk, dstEntryOff, 7);
      }
      // Footer entry_type = SUM_TYPE_NODE = 1 at offset 4091.
      disk[nodeSumBlockOff + 4091] = 1;
    }
  }

  // ────────────────────────────────────────────────────────────────────────
  // Checkpoint writer: stamp header fields + recompute CRC.
  // ────────────────────────────────────────────────────────────────────────

  private static void WriteCheckpoint(byte[] disk, Superblock sb, CheckpointState cp) {
    var packBase = sb.CpBlkAddr + cp.SlotIndex * BlocksPerSeg;
    var cpBlk0Off = packBase * BlockSize;
    var cpBlk5Off = (packBase + 5) * BlockSize;

    // Update header fields in cp_page_1.
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(cpBlk0Off), cp.CheckpointVersion);
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(cpBlk0Off + 16), cp.ValidBlockCount);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(cpBlk0Off + 32), cp.FreeSegments);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(cpBlk0Off + 144), cp.ValidNodeCount);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(cpBlk0Off + 148), cp.ValidInodeCount);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(cpBlk0Off + 152), cp.NextFreeNid);

    // Curseg segnos + blkoffs: data 0..2 at 84/116, node 0..2 at 36/68 (writer convention).
    for (var i = 0; i < 3; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(cpBlk0Off + 84 + i * 4), (uint)cp.CurSegnos[i]);
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(cpBlk0Off + 116 + i * 2), (ushort)cp.CurBlkoffs[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(cpBlk0Off + 36 + i * 4), (uint)cp.CurSegnos[3 + i]);
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(cpBlk0Off + 68 + i * 2), (ushort)cp.CurBlkoffs[3 + i]);
    }

    // Mirror the per-block summary entries into the CP pack's data + node
    // summary blocks so fsck's curseg->sum_blk seeding agrees with the
    // on-disk SSA we wrote per-add.
    WriteCompactDataSummaries(disk, sb, cp);
    WriteNodeSummaries(disk, sb, cp);

    // Recompute CRC over [0, 4092) with F2FS_SUPER_MAGIC seed (matches writer).
    const int checksumOffset = 4092;
    var crc = F2fsWriter.F2fsCrc32(F2fsMagic, new ReadOnlySpan<byte>(disk, cpBlk0Off, checksumOffset));
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(cpBlk0Off + checksumOffset), crc);

    // Mirror cp_page_1 → cp_page_2 (block 5 of the pack). Same content per writer convention.
    Buffer.BlockCopy(disk, cpBlk0Off, disk, cpBlk5Off, BlockSize);
  }
}
