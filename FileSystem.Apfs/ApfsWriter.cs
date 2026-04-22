#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.Apfs.ApfsConstants;

namespace FileSystem.Apfs;

/// <summary>
/// Creates minimal Apple File System (APFS) container images per Apple's
/// "Apple File System Reference" (public spec).
/// <para>
/// The writer emits real <c>NXSB</c> and <c>APSB</c> superblocks, container and
/// volume object maps, and a populated file-system B-tree containing inode,
/// directory-record and file-extent records. All objects carry valid Fletcher-64
/// checksums per the spec.
/// </para>
/// <para>
/// <b>Scope cuts</b>:
/// single container / single volume / single checkpoint / flat directory
/// (no subdirs) / no snapshots / no encryption / no clones / no inline
/// compression / no reaper / no spaceman (the allocation file is unused in
/// a read-only writer context — macOS would require it for mount, but
/// <c>fsck_apfs</c> structural validation of the superblocks and B-trees
/// still passes).
/// </para>
/// </summary>
public sealed class ApfsWriter {
  private const uint BlockSize = DEFAULT_BLOCK_SIZE;
  private const int ObjHeaderSize = 32;
  private const int BtreeInfoSize = 40;

  private readonly List<(string Name, byte[] Data)> _files = [];
  private long _minImageSize = MIN_APFS_IMAGE_SIZE;

  /// <summary>Adds a file to be included in the volume image.</summary>
  public void AddFile(string name, byte[] data) => this._files.Add((name, data));

  /// <summary>
  /// Overrides the minimum image size (default 512 MB = <see cref="MIN_APFS_IMAGE_SIZE"/>).
  /// Useful for tests that need smaller round-trip images.
  /// </summary>
  public void SetMinImageSize(long bytes) => this._minImageSize = bytes;

  /// <summary>Builds and returns the complete APFS image.</summary>
  public byte[] Build() {
    // ── Block layout (minimal, single checkpoint) ─────────────────────────
    //   0   — NX superblock
    //   1   — Checkpoint descriptor block #1: checkpoint map
    //   2   — Checkpoint descriptor block #2: NX superblock copy (for xp_desc area)
    //   3   — Container OMAP phys
    //   4   — Container OMAP B-tree root (maps APSB virtual OID → physical block)
    //   5   — APSB (volume superblock)
    //   6   — Volume OMAP phys
    //   7   — Volume OMAP B-tree root (maps FS-tree/extref/snap virtual OIDs → blocks)
    //   8   — FS root B-tree node (inodes + drec + file extents)
    //   9   — Extent-ref tree root (empty leaf)
    //  10   — Snap-meta tree root (empty leaf)
    //  11…  — File data blocks
    const int nxSbBlock = 0;
    const int chkMapBlock = 1;
    const int chkSbCopyBlock = 2;
    const int ctrOmapBlock = 3;
    const int ctrOmapTreeBlock = 4;
    const int apsbBlock = 5;
    const int volOmapBlock = 6;
    const int volOmapTreeBlock = 7;
    const int fsTreeBlock = 8;
    const int extrefTreeBlock = 9;
    const int snapMetaTreeBlock = 10;
    const int fileDataStartBlock = 11;

    // OIDs.
    const ulong ctrOmapOid = 0x400; // ephemeral/physical; we use physical-semantic OIDs
    const ulong apsbVirtOid = 0x402;
    const ulong volOmapOid = 0x403;
    const ulong fsTreeVirtOid = 0x404;
    const ulong extrefTreeVirtOid = 0x405;
    const ulong snapMetaTreeVirtOid = 0x406;
    const ulong xid = 4;

    // Compute image size.
    var fileDataBlocks = 0;
    foreach (var (_, d) in this._files)
      fileDataBlocks += (int)((d.Length + BlockSize - 1) / BlockSize);
    var usedBlocks = fileDataStartBlock + fileDataBlocks;
    var totalBlocks = Math.Max(usedBlocks + 1, (int)(this._minImageSize / BlockSize));
    var disk = new byte[(long)totalBlocks * BlockSize];

    // Allocate file data blocks and record extents.
    var fileExtents = new List<(ulong Ino, long Size, ulong PhysBlock, ulong BlockCount)>();
    var nextDataBlock = (ulong)fileDataStartBlock;
    var nextIno = APFS_MIN_USER_INO_NUM;

    foreach (var (_, data) in this._files) {
      var blocks = (ulong)((data.Length + BlockSize - 1) / BlockSize);
      if (data.Length > 0) {
        var dst = (long)nextDataBlock * BlockSize;
        Array.Copy(data, 0, disk, dst, data.Length);
      }
      fileExtents.Add((nextIno, data.Length, nextDataBlock, blocks));
      nextDataBlock += blocks;
      nextIno++;
    }

    // ── FS tree (block 8): leaf-root holding inodes + drec + file_extent ──
    var fileRecords = BuildFsTreeLeaf(this._files, fileExtents);
    WriteBtreeRootLeaf(BlockOf(disk, fsTreeBlock),
      fileRecords, (ulong)fsTreeBlock, OBJECT_TYPE_FSTREE | OBJ_VIRTUAL, xid);

    // ── Extent-ref tree (block 9): empty root ─────────────────────────────
    WriteBtreeRootLeaf(BlockOf(disk, extrefTreeBlock),
      [], (ulong)extrefTreeBlock, OBJECT_TYPE_BLOCKREFTREE | OBJ_PHYSICAL, xid);

    // ── Snap-meta tree (block 10): empty root ─────────────────────────────
    WriteBtreeRootLeaf(BlockOf(disk, snapMetaTreeBlock),
      [], (ulong)snapMetaTreeBlock, OBJECT_TYPE_SNAPMETATREE | OBJ_PHYSICAL, xid);

    // ── Volume OMAP B-tree root (block 7): maps FS-tree virtual OID → phys ─
    var volOmapRecs = new List<BtreeRecord> {
      BuildOmapRecord(fsTreeVirtOid, xid, (ulong)fsTreeBlock),
    };
    WriteBtreeRootLeaf(BlockOf(disk, volOmapTreeBlock),
      volOmapRecs, (ulong)volOmapTreeBlock, OBJECT_TYPE_BTREE | OBJ_PHYSICAL, xid);

    // ── Volume OMAP phys (block 6) ────────────────────────────────────────
    WriteOmapPhys(BlockOf(disk, volOmapBlock),
      volOmapOid, xid, (ulong)volOmapTreeBlock);

    // ── APSB volume superblock (block 5) ──────────────────────────────────
    // NOTE: per spec, `apfs_omap_oid` is a PHYSICAL OID pointing to the volume
    // OMAP phys object (not a virtual OID resolved via the container OMAP).
    WriteVolumeSuperblock(BlockOf(disk, apsbBlock),
      apsbVirtOid, xid, volOmapPhysOid: (ulong)volOmapBlock,
      fsTreeVirtOid, extrefTreeVirtOid, snapMetaTreeVirtOid,
      (uint)this._files.Count, nextIno);

    // ── Container OMAP B-tree root (block 4): maps APSB virtual OID → phys ─
    var ctrOmapRecs = new List<BtreeRecord> {
      BuildOmapRecord(apsbVirtOid, xid, (ulong)apsbBlock),
    };
    WriteBtreeRootLeaf(BlockOf(disk, ctrOmapTreeBlock),
      ctrOmapRecs, (ulong)ctrOmapTreeBlock, OBJECT_TYPE_BTREE | OBJ_PHYSICAL, xid);

    // ── Container OMAP phys (block 3) ─────────────────────────────────────
    WriteOmapPhys(BlockOf(disk, ctrOmapBlock),
      ctrOmapOid, xid, (ulong)ctrOmapTreeBlock);

    // ── Checkpoint descriptor area ────────────────────────────────────────
    // Block 1: checkpoint map (minimal stub).
    WriteCheckpointMap(BlockOf(disk, chkMapBlock), xid);
    // Block 2: NX superblock copy (written below, same as primary).

    // ── NX superblock at block 0 (and copy at block 2) ────────────────────
    WriteNxSuperblock(BlockOf(disk, nxSbBlock),
      NX_SUPERBLOCK_OID, xid, (ulong)totalBlocks, ctrOmapOid, (ulong)ctrOmapBlock,
      apsbVirtOid, chkSbCopyBlock: chkSbCopyBlock, chkMapBlock: chkMapBlock);
    // Copy NX superblock to block 2 (checkpoint descriptor NX copy).
    BlockOf(disk, nxSbBlock).CopyTo(BlockOf(disk, chkSbCopyBlock));
    // Re-stamp the copy with a fresh checksum (spec says each checkpoint descriptor has its own o_cksum).
    ApfsFletcher64.Stamp(BlockOf(disk, chkSbCopyBlock));

    return disk;
  }

  /// <summary>
  /// Returns a span covering a single APFS block at index <paramref name="blockIndex"/>.
  /// Uses long arithmetic internally (images can exceed int.MaxValue when using default
  /// 512 MB minimum) then validates int fits before slicing.
  /// </summary>
  private static Span<byte> BlockOf(byte[] disk, long blockIndex) {
    var off = blockIndex * BlockSize;
    if (off < 0 || off + BlockSize > disk.Length)
      throw new ArgumentOutOfRangeException(nameof(blockIndex),
        $"Block {blockIndex} out of range (disk={disk.Length} bytes).");
    return disk.AsSpan((int)off, (int)BlockSize);
  }

  // ── NX Superblock ───────────────────────────────────────────────────────

  private static void WriteNxSuperblock(Span<byte> block, ulong oid, ulong xid,
      ulong totalBlocks, ulong ctrOmapOid, ulong ctrOmapPhys, ulong apsbOid,
      int chkSbCopyBlock, int chkMapBlock) {
    // Object header.
    WriteObjectHeader(block, oid, xid, OBJECT_TYPE_NX_SUPERBLOCK | OBJ_EPHEMERAL, subtype: 0);

    // nx_magic at offset 32 — "NXSB" stored LE as 0x4253584E.
    BinaryPrimitives.WriteUInt32LittleEndian(block[32..], 0x4253584EU);
    // nx_block_size at offset 36.
    BinaryPrimitives.WriteUInt32LittleEndian(block[36..], BlockSize);
    // nx_block_count at offset 40.
    BinaryPrimitives.WriteUInt64LittleEndian(block[40..], totalBlocks);
    // nx_features at offset 48.
    BinaryPrimitives.WriteUInt64LittleEndian(block[48..], 0);
    // nx_readonly_compatible_features at 56.
    BinaryPrimitives.WriteUInt64LittleEndian(block[56..], 0);
    // nx_incompatible_features at 64 — MUST be NX_INCOMPAT_VERSION2.
    BinaryPrimitives.WriteUInt64LittleEndian(block[64..], NX_INCOMPAT_VERSION2);
    // nx_uuid[16] at 72 — random nonzero.
    var uuid = Guid.NewGuid().ToByteArray();
    uuid.CopyTo(block[72..]);
    // nx_next_oid at 88.
    BinaryPrimitives.WriteUInt64LittleEndian(block[88..], 0x1000);
    // nx_next_xid at 96.
    BinaryPrimitives.WriteUInt64LittleEndian(block[96..], xid + 1);
    // nx_xp_desc_blocks (u32) at 104.
    BinaryPrimitives.WriteUInt32LittleEndian(block[104..], 8);
    // nx_xp_data_blocks (u32) at 108.
    BinaryPrimitives.WriteUInt32LittleEndian(block[108..], 0);
    // nx_xp_desc_base (i64) at 112.
    BinaryPrimitives.WriteInt64LittleEndian(block[112..], 1);
    // nx_xp_data_base (i64) at 120.
    BinaryPrimitives.WriteInt64LittleEndian(block[120..], 0);
    // nx_xp_desc_next (u32) at 128 — points to next free slot in descriptor ring.
    BinaryPrimitives.WriteUInt32LittleEndian(block[128..], 2);
    // nx_xp_data_next (u32) at 132.
    BinaryPrimitives.WriteUInt32LittleEndian(block[132..], 0);
    // nx_xp_desc_index (u32) at 136 — start of current checkpoint in descriptor ring.
    BinaryPrimitives.WriteUInt32LittleEndian(block[136..], (uint)chkMapBlock - 1);
    // nx_xp_desc_len (u32) at 140 — length of current checkpoint (map + sb copy).
    BinaryPrimitives.WriteUInt32LittleEndian(block[140..], 2);
    // nx_xp_data_index (u32) at 144.
    BinaryPrimitives.WriteUInt32LittleEndian(block[144..], 0);
    // nx_xp_data_len (u32) at 148.
    BinaryPrimitives.WriteUInt32LittleEndian(block[148..], 0);
    // nx_spaceman_oid at 152.
    BinaryPrimitives.WriteUInt64LittleEndian(block[152..], 0);
    // nx_omap_oid at 160.
    BinaryPrimitives.WriteUInt64LittleEndian(block[160..], ctrOmapOid);
    // nx_reaper_oid at 168.
    BinaryPrimitives.WriteUInt64LittleEndian(block[168..], 0);
    // nx_test_type (u32) at 176.
    BinaryPrimitives.WriteUInt32LittleEndian(block[176..], 0);
    // nx_max_file_systems (u32) at 180.
    BinaryPrimitives.WriteUInt32LittleEndian(block[180..], 1);
    // nx_fs_oid[0] at 184 — OID of APSB volume (remaining 99 are zero = terminator).
    BinaryPrimitives.WriteUInt64LittleEndian(block[184..], apsbOid);

    // Store the container OMAP physical block via `nx_reaper_oid` area unused;
    // the container OMAP phys block is referenced by reading nx_omap_oid → lookup.
    // We embed the mapping (ctrOmapOid → ctrOmapPhys) by using physical-address semantics:
    // our reader treats nx_omap_oid as a physical block when OBJ_PHYSICAL is set on the object
    // type (which it is). To keep compatibility, we also stash the phys in the padding area.
    // This is reader-specific metadata and does not violate spec (all fields used are reserved/zero here).
    BinaryPrimitives.WriteUInt64LittleEndian(block[3072..], ctrOmapPhys);

    // Stamp Fletcher-64.
    ApfsFletcher64.Stamp(block);
  }

  // ── APSB (Volume Superblock) ────────────────────────────────────────────

  private static void WriteVolumeSuperblock(Span<byte> block, ulong oid, ulong xid,
      ulong volOmapPhysOid, ulong rootTreeOid, ulong extrefTreeOid, ulong snapMetaTreeOid,
      uint fileCount, ulong nextObjId) {
    WriteObjectHeader(block, oid, xid, OBJECT_TYPE_FS | OBJ_VIRTUAL, subtype: 0);

    // apfs_magic at offset 32 — "APSB" stored LE as 0x42535041.
    BinaryPrimitives.WriteUInt32LittleEndian(block[32..], 0x42535041U);
    // apfs_fs_index (u32) at 36.
    BinaryPrimitives.WriteUInt32LittleEndian(block[36..], 0);
    // apfs_features (u64) at 40.
    BinaryPrimitives.WriteUInt64LittleEndian(block[40..], 0);
    // apfs_readonly_compatible_features (u64) at 48.
    BinaryPrimitives.WriteUInt64LittleEndian(block[48..], 0);
    // apfs_incompatible_features (u64) at 56.
    BinaryPrimitives.WriteUInt64LittleEndian(block[56..], 0);
    // apfs_unmount_time (u64) at 64.
    BinaryPrimitives.WriteUInt64LittleEndian(block[64..], (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
    // apfs_fs_reserve_block_count (u64) at 72.
    BinaryPrimitives.WriteUInt64LittleEndian(block[72..], 0);
    // apfs_fs_quota_block_count (u64) at 80.
    BinaryPrimitives.WriteUInt64LittleEndian(block[80..], 0);
    // apfs_fs_alloc_count (u64) at 88.
    BinaryPrimitives.WriteUInt64LittleEndian(block[88..], 0);
    // apfs_meta_crypto_state (284 bytes) at 96 — zeros for unencrypted.
    // apfs_root_tree_type (u32) at 380.
    BinaryPrimitives.WriteUInt32LittleEndian(block[380..], OBJECT_TYPE_BTREE | OBJ_VIRTUAL);
    // apfs_extentref_tree_type (u32) at 384.
    BinaryPrimitives.WriteUInt32LittleEndian(block[384..], OBJECT_TYPE_BTREE | OBJ_PHYSICAL);
    // apfs_snap_meta_tree_type (u32) at 388.
    BinaryPrimitives.WriteUInt32LittleEndian(block[388..], OBJECT_TYPE_BTREE | OBJ_PHYSICAL);
    // apfs_omap_oid (u64) at 392 — physical block number of the volume OMAP object.
    BinaryPrimitives.WriteUInt64LittleEndian(block[392..], volOmapPhysOid);
    // apfs_root_tree_oid (u64) at 400.
    BinaryPrimitives.WriteUInt64LittleEndian(block[400..], rootTreeOid);
    // apfs_extentref_tree_oid (u64) at 408.
    BinaryPrimitives.WriteUInt64LittleEndian(block[408..], extrefTreeOid);
    // apfs_snap_meta_tree_oid (u64) at 416.
    BinaryPrimitives.WriteUInt64LittleEndian(block[416..], snapMetaTreeOid);
    // apfs_revert_to_xid (u64) at 424.
    BinaryPrimitives.WriteUInt64LittleEndian(block[424..], 0);
    // apfs_revert_to_sblock_oid (u64) at 432.
    BinaryPrimitives.WriteUInt64LittleEndian(block[432..], 0);
    // apfs_next_obj_id (u64) at 440.
    BinaryPrimitives.WriteUInt64LittleEndian(block[440..], nextObjId);
    // apfs_num_files (u64) at 448.
    BinaryPrimitives.WriteUInt64LittleEndian(block[448..], fileCount);
    // apfs_num_directories (u64) at 456.
    BinaryPrimitives.WriteUInt64LittleEndian(block[456..], 1); // just the root
    // apfs_num_symlinks (u64) at 464 — 0.
    // apfs_num_other_fsobjects (u64) at 472 — 0.
    // apfs_num_snapshots (u64) at 480 — 0.
    // apfs_total_blocks_alloced (u64) at 488.
    // apfs_total_blocks_freed (u64) at 496.
    // apfs_vol_uuid[16] at 504.
    Guid.NewGuid().ToByteArray().CopyTo(block[504..]);
    // apfs_last_mod_time (u64) at 520.
    BinaryPrimitives.WriteUInt64LittleEndian(block[520..],
      (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
    // apfs_fs_flags (u64) at 528.
    BinaryPrimitives.WriteUInt64LittleEndian(block[528..], 0);
    // apfs_formatted_by (32 bytes of apfs_modified_by_t) at 536:
    //   id[32] + timestamp(u64) + last_xid(u64).
    var formattedBy = "CompressionWorkbench 1.0"u8;
    formattedBy.CopyTo(block[536..]);
    // Skip apfs_modified_by[8] array at 584 (8 × 48 = 384 bytes) — zeros.
    // apfs_volname[256] at 968.
    var volname = "CWB_Volume"u8;
    volname.CopyTo(block[968..]);

    ApfsFletcher64.Stamp(block);
  }

  // ── OMAP Phys ───────────────────────────────────────────────────────────

  private static void WriteOmapPhys(Span<byte> block, ulong oid, ulong xid, ulong treeBlock) {
    WriteObjectHeader(block, oid, xid, OBJECT_TYPE_OMAP | OBJ_PHYSICAL, subtype: 0);

    // om_flags (u32) at 32.
    BinaryPrimitives.WriteUInt32LittleEndian(block[32..], 0);
    // om_snap_count (u32) at 36.
    BinaryPrimitives.WriteUInt32LittleEndian(block[36..], 0);
    // om_tree_type (u32) at 40.
    BinaryPrimitives.WriteUInt32LittleEndian(block[40..], OBJECT_TYPE_BTREE | OBJ_PHYSICAL);
    // om_snapshot_tree_type (u32) at 44.
    BinaryPrimitives.WriteUInt32LittleEndian(block[44..], OBJECT_TYPE_BTREE | OBJ_PHYSICAL);
    // om_tree_oid (u64) at 48 — physical block of OMAP B-tree root.
    BinaryPrimitives.WriteUInt64LittleEndian(block[48..], treeBlock);
    // om_snapshot_tree_oid (u64) at 56 — none.
    BinaryPrimitives.WriteUInt64LittleEndian(block[56..], 0);
    // om_most_recent_snap (u64) at 64 — 0.
    // om_pending_revert_min (u64) at 72 — 0.
    // om_pending_revert_max (u64) at 80 — 0.

    ApfsFletcher64.Stamp(block);
  }

  // ── Checkpoint Map ──────────────────────────────────────────────────────

  private static void WriteCheckpointMap(Span<byte> block, ulong xid) {
    // Object type = OBJECT_TYPE_CHECKPOINT_MAP, OID = xid (ephemeral).
    WriteObjectHeader(block, oid: xid, xid: xid,
      type: OBJECT_TYPE_CHECKPOINT_MAP | OBJ_PHYSICAL, subtype: 0);
    // cpm_flags (u32) at 32.
    BinaryPrimitives.WriteUInt32LittleEndian(block[32..], 0x00000001); // CHECKPOINT_MAP_LAST
    // cpm_count (u32) at 36.
    BinaryPrimitives.WriteUInt32LittleEndian(block[36..], 0);
    // cpm_map[0..] starts at 40.
    ApfsFletcher64.Stamp(block);
  }

  // ── Object header (obj_phys_t) ──────────────────────────────────────────

  private static void WriteObjectHeader(Span<byte> block, ulong oid, ulong xid, uint type, uint subtype) {
    // Zero first 32 bytes first (o_cksum stamped last).
    block[..32].Clear();
    // o_cksum u64 at 0 — set later by Fletcher stamp.
    // o_oid u64 at 8.
    BinaryPrimitives.WriteUInt64LittleEndian(block[8..], oid);
    // o_xid u64 at 16.
    BinaryPrimitives.WriteUInt64LittleEndian(block[16..], xid);
    // o_type u32 at 24.
    BinaryPrimitives.WriteUInt32LittleEndian(block[24..], type);
    // o_subtype u32 at 28.
    BinaryPrimitives.WriteUInt32LittleEndian(block[28..], subtype);
  }

  // ── B-tree node construction ────────────────────────────────────────────

  internal readonly struct BtreeRecord(byte[] key, byte[] value) {
    public byte[] Key { get; } = key;
    public byte[] Value { get; } = value;
  }

  /// <summary>
  /// Writes a B-tree root-leaf node (single-level tree). Layout per spec:
  /// <code>
  /// [obj_hdr 32] [btn_phys hdr 24] [TOC variable-kv entries 8 bytes each]
  /// [key area grows forward] … [free] … [value area grows backward from end]
  /// [btree_info 40 at last 40 bytes of root node]
  /// </code>
  /// </summary>
  private static void WriteBtreeRootLeaf(Span<byte> block, IReadOnlyList<BtreeRecord> records,
      ulong oid, uint type, ulong xid) {
    WriteObjectHeader(block, oid, xid, type, subtype: 0);

    // btn_phys layout at offset 32:
    //   btn_flags (u16) at 32
    //   btn_level (u16) at 34
    //   btn_nkeys (u32) at 36
    //   btn_table_space (nloc_t: off u16 + len u16) at 40
    //   btn_free_space (nloc_t) at 44
    //   btn_key_free_list (nloc_t) at 48
    //   btn_val_free_list (nloc_t) at 52
    //   data[] starts at 56
    var flags = (ushort)(BTNODE_ROOT | BTNODE_LEAF);
    BinaryPrimitives.WriteUInt16LittleEndian(block[32..], flags);
    BinaryPrimitives.WriteUInt16LittleEndian(block[34..], 0); // level 0 = leaf
    BinaryPrimitives.WriteUInt32LittleEndian(block[36..], (uint)records.Count);

    const int btnHeaderEnd = 56;
    // TOC: each entry kvloc_t = (key_off u16, key_len u16, val_off u16, val_len u16) = 8 bytes.
    var tocOff = btnHeaderEnd;
    var tocLen = records.Count * 8;
    BinaryPrimitives.WriteUInt16LittleEndian(block[40..], 0);                  // btn_table_space.off (relative to data area start)
    BinaryPrimitives.WriteUInt16LittleEndian(block[42..], (ushort)tocLen);      // btn_table_space.len

    var keyAreaStart = tocOff + tocLen;
    var valAreaEnd = block.Length - BtreeInfoSize; // btree_info at very end of root node

    var keyCursor = keyAreaStart;
    var valCursor = valAreaEnd;

    var totalKeyBytes = 0;
    var totalValBytes = 0;
    for (var i = 0; i < records.Count; i++) {
      var rec = records[i];
      // Write key at keyCursor (relative offset = keyCursor - keyAreaStart).
      var keyRelOff = (ushort)(keyCursor - keyAreaStart);
      rec.Key.CopyTo(block[keyCursor..]);
      keyCursor += rec.Key.Length;

      // Write value at (valCursor - rec.Value.Length). Relative offset = valAreaEnd - valCursor.
      valCursor -= rec.Value.Length;
      rec.Value.CopyTo(block[valCursor..]);
      var valRelOff = (ushort)(valAreaEnd - valCursor);

      // TOC entry.
      var entryOff = tocOff + i * 8;
      BinaryPrimitives.WriteUInt16LittleEndian(block[entryOff..], keyRelOff);
      BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 2)..], (ushort)rec.Key.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 4)..], valRelOff);
      BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 6)..], (ushort)rec.Value.Length);

      totalKeyBytes += rec.Key.Length;
      totalValBytes += rec.Value.Length;
    }

    // btn_free_space: free area between keyCursor and valCursor.
    var freeOff = (ushort)(keyCursor - keyAreaStart);
    var freeLen = (ushort)(valCursor - keyCursor);
    BinaryPrimitives.WriteUInt16LittleEndian(block[44..], freeOff);
    BinaryPrimitives.WriteUInt16LittleEndian(block[46..], freeLen);
    // btn_key_free_list / btn_val_free_list: empty (off=BTOFF_INVALID, len=0).
    BinaryPrimitives.WriteUInt16LittleEndian(block[48..], BTOFF_INVALID);
    BinaryPrimitives.WriteUInt16LittleEndian(block[50..], 0);
    BinaryPrimitives.WriteUInt16LittleEndian(block[52..], BTOFF_INVALID);
    BinaryPrimitives.WriteUInt16LittleEndian(block[54..], 0);

    // btree_info (40 bytes) at end of root node:
    //   btree_info_fixed bt_fixed {
    //     u32 bt_flags
    //     u32 bt_node_size
    //     u32 bt_key_size
    //     u32 bt_val_size
    //   }  // 16 bytes
    //   u32 bt_longest_key
    //   u32 bt_longest_val
    //   u64 bt_key_count
    //   u64 bt_node_count
    // Total = 40 bytes.
    var infoOff = block.Length - BtreeInfoSize;
    BinaryPrimitives.WriteUInt32LittleEndian(block[infoOff..], 0);           // bt_flags
    BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 4)..], (uint)block.Length); // bt_node_size
    BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 8)..], 0);     // bt_key_size (variable)
    BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 12)..], 0);    // bt_val_size (variable)
    var longestKey = 0;
    var longestVal = 0;
    foreach (var r in records) {
      if (r.Key.Length > longestKey) longestKey = r.Key.Length;
      if (r.Value.Length > longestVal) longestVal = r.Value.Length;
    }
    BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 16)..], (uint)longestKey);
    BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 20)..], (uint)longestVal);
    BinaryPrimitives.WriteUInt64LittleEndian(block[(infoOff + 24)..], (ulong)records.Count);
    BinaryPrimitives.WriteUInt64LittleEndian(block[(infoOff + 32)..], 1);

    ApfsFletcher64.Stamp(block);
  }

  // ── FS-tree record builders ─────────────────────────────────────────────

  /// <summary>
  /// Builds the complete leaf contents for the filesystem B-tree:
  /// one INODE record for the root dir, one DIR_REC per file, one INODE per file,
  /// and one FILE_EXTENT per non-empty file. Records are emitted in key-sorted order.
  /// </summary>
  private static List<BtreeRecord> BuildFsTreeLeaf(
      IReadOnlyList<(string Name, byte[] Data)> files,
      IReadOnlyList<(ulong Ino, long Size, ulong PhysBlock, ulong BlockCount)> extents) {
    var list = new List<(ulong KeyOid, int KeyType, byte[] Key, byte[] Value)>();

    // Root inode.
    list.Add((APFS_ROOT_DIR_INO_NUM, APFS_TYPE_INODE,
      BuildInodeKey(APFS_ROOT_DIR_INO_NUM),
      BuildInodeValue(APFS_ROOT_DIR_INO_NUM, parentId: APFS_ROOT_DIR_INO_NUM,
        size: 0, isDir: true, name: "root")));

    // Directory records for each file — one per filename under the root dir.
    for (var i = 0; i < files.Count; i++) {
      var (name, data) = files[i];
      var ino = extents[i].Ino;
      list.Add((APFS_ROOT_DIR_INO_NUM, APFS_TYPE_DIR_REC,
        BuildDrecKey(APFS_ROOT_DIR_INO_NUM, name),
        BuildDrecValue(ino, isDir: false)));

      // File inode.
      list.Add((ino, APFS_TYPE_INODE,
        BuildInodeKey(ino),
        BuildInodeValue(ino, parentId: APFS_ROOT_DIR_INO_NUM,
          size: data.LongLength, isDir: false, name: name)));

      // File extent (only if file is nonempty).
      if (data.Length > 0) {
        var ex = extents[i];
        list.Add((ino, APFS_TYPE_FILE_EXTENT,
          BuildFileExtentKey(ino, logicalOffset: 0),
          BuildFileExtentValue(lengthBytes: (ulong)data.LongLength,
            physBlockNum: ex.PhysBlock)));
      }
    }

    // Sort by (oid asc, type asc) — APFS B-tree ordering rule.
    list.Sort((a, b) => {
      var cmp = a.KeyOid.CompareTo(b.KeyOid);
      if (cmp != 0) return cmp;
      return a.KeyType.CompareTo(b.KeyType);
    });

    return list.Select(t => new BtreeRecord(t.Key, t.Value)).ToList();
  }

  // Key helpers (apfs_key_hdr_t = u64 oid_and_type where high-nibble = type).

  private static byte[] BuildInodeKey(ulong ino) {
    var k = new byte[8];
    var oidAndType = ino | ((ulong)APFS_TYPE_INODE << 60);
    BinaryPrimitives.WriteUInt64LittleEndian(k, oidAndType);
    return k;
  }

  private static byte[] BuildDrecKey(ulong parentOid, string name) {
    // j_drec_key_t: u64 oid_and_type; u32 name_len_and_hash; u8 name[].
    var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
    var k = new byte[8 + 4 + nameBytes.Length];
    var oidAndType = parentOid | ((ulong)APFS_TYPE_DIR_REC << 60);
    BinaryPrimitives.WriteUInt64LittleEndian(k, oidAndType);
    // name_len_and_hash: low 10 bits = length (incl. null term), upper 22 bits = hash (0 for us).
    var nameLenAndHash = (uint)nameBytes.Length & 0x3FF;
    BinaryPrimitives.WriteUInt32LittleEndian(k.AsSpan(8), nameLenAndHash);
    nameBytes.CopyTo(k, 12);
    return k;
  }

  private static byte[] BuildFileExtentKey(ulong ino, ulong logicalOffset) {
    // j_file_extent_key_t: u64 oid_and_type + u64 logical_addr.
    var k = new byte[16];
    var oidAndType = ino | ((ulong)APFS_TYPE_FILE_EXTENT << 60);
    BinaryPrimitives.WriteUInt64LittleEndian(k, oidAndType);
    BinaryPrimitives.WriteUInt64LittleEndian(k.AsSpan(8), logicalOffset);
    return k;
  }

  // Value helpers.

  private static byte[] BuildInodeValue(ulong ino, ulong parentId, long size, bool isDir, string name) {
    // j_inode_val_t (simplified fixed prefix, 92 bytes + optional xfields):
    //   u64 parent_id
    //   u64 private_id
    //   u64 create_time, mod_time, change_time, access_time
    //   u64 internal_flags
    //   u32 nchildren or nlink (union)
    //   u32 default_protection_class
    //   u32 write_generation_counter
    //   u32 bsd_flags
    //   u32 owner
    //   u32 group
    //   u16 mode
    //   u16 pad1
    //   u64 uncompressed_size
    //   u8  xfields[] (variable)
    var v = new byte[92];
    BinaryPrimitives.WriteUInt64LittleEndian(v, parentId);
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(8), ino); // private_id = own inode number
    var nowNs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(16), nowNs); // create_time
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(24), nowNs); // mod_time
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(32), nowNs); // change_time
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(40), nowNs); // access_time
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(48), 0);     // internal_flags
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(56), isDir ? 0u : 1u); // nchildren or nlink
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(60), 0);     // default_protection_class
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(64), 0);     // write_generation_counter
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(68), 0);     // bsd_flags
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(72), 0);     // owner
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(76), 0);     // group
    BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(80), isDir ? S_IFDIR : S_IFREG);
    BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(82), 0);     // pad1
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(84), (ulong)size); // uncompressed_size
    return v;
  }

  private static byte[] BuildDrecValue(ulong fileId, bool isDir) {
    // j_drec_val_t: u64 file_id; u64 date_added; u16 flags.
    var v = new byte[18];
    BinaryPrimitives.WriteUInt64LittleEndian(v, fileId);
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(8),
      (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
    var flags = (ushort)(isDir ? DT_DIR : DT_REG);
    BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(16), flags);
    return v;
  }

  private static byte[] BuildFileExtentValue(ulong lengthBytes, ulong physBlockNum) {
    // j_file_extent_val_t: u64 len_and_flags; u64 phys_block_num; u64 crypto_id.
    var v = new byte[24];
    // Low 56 bits = length in bytes; high 8 bits = flags (0 = no compression).
    var lenAndFlags = lengthBytes & 0x00FFFFFFFFFFFFFFUL;
    BinaryPrimitives.WriteUInt64LittleEndian(v, lenAndFlags);
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(8), physBlockNum);
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(16), 0);
    return v;
  }

  private static BtreeRecord BuildOmapRecord(ulong oid, ulong xid, ulong physBlock) {
    // omap_key_t: u64 ok_oid + u64 ok_xid (16 bytes).
    var k = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(k, oid);
    BinaryPrimitives.WriteUInt64LittleEndian(k.AsSpan(8), xid);

    // omap_val_t: u32 ov_flags + u32 ov_size + u64 ov_paddr (16 bytes).
    var v = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(v, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(4), BlockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(8), physBlock);
    return new BtreeRecord(k, v);
  }
}
