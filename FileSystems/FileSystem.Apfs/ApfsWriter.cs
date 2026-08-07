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
/// The FS B-tree grows automatically: when the inode / directory-record /
/// file-extent records overflow a single node, they spill into several leaf
/// nodes beneath an internal index node (a 2-level tree), so directories with
/// many entries round-trip correctly. The tree depth is capped at two levels —
/// the internal root holds one separator per leaf, which bounds the volume at a
/// few hundred thousand small files (ample for image creation); a deeper tree is
/// not emitted.
/// </para>
/// <para>
/// <b>Scope cuts</b>:
/// single container / single volume / single checkpoint / FS B-tree limited to
/// two levels (root + leaves) / no snapshots / no encryption / no clones / no inline
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

  private readonly List<FileEntry> _files = [];
  private long _minImageSize = MIN_APFS_IMAGE_SIZE;
  // What macOS names a volume it is given no name for.
  private string _volumeName = "untitled";

  /// <summary>A file's payload: held inline, or opened on demand when it is too large to hold.</summary>
  private readonly record struct FileEntry(string Name, long Size, byte[]? Data, Func<Stream>? Opener);

  /// <summary>Adds a file to be included in the volume image.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add(new FileEntry(name, data.LongLength, data, null));
  }

  /// <summary>
  /// Adds a file whose bytes are produced on demand. <paramref name="size" /> must
  /// match what <paramref name="openStream" /> yields; the layout is settled from
  /// it before a single byte is read, so a file larger than a byte[] can carry is
  /// placed like any other.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    ArgumentOutOfRangeException.ThrowIfNegative(size);
    this._files.Add(new FileEntry(name, size, null, openStream));
  }

  /// <summary>
  /// Sets the APFS volume name written to the APSB <c>apfs_volname</c> field
  /// (offset 968, 256-byte NUL-terminated UTF-8). Defaults to <c>CWB_Volume</c>.
  /// </summary>
  public void SetVolumeName(string name) {
    if (!string.IsNullOrEmpty(name)) this._volumeName = name;
  }

  /// <summary>
  /// Overrides the minimum image size (default 512 MB = <see cref="MIN_APFS_IMAGE_SIZE"/>).
  /// Useful for tests that need smaller round-trip images.
  /// </summary>
  public void SetMinImageSize(long bytes) => this._minImageSize = bytes;

  /// <summary>Builds and returns the complete APFS image.</summary>
  /// <exception cref="NotSupportedException">
  /// The volume is larger than one array can hold. Use <see cref="BuildTo" />,
  /// which keeps free space sparse and streams file data into place.
  /// </exception>
  public byte[] Build() {
    var prefix = BuildCore(out var dataWrites, out var totalBytes);
    if (totalBytes > Array.MaxLength)
      throw new NotSupportedException(
        $"An APFS volume of {totalBytes:N0} bytes cannot be materialised in memory; use BuildTo(Stream).");

    var full = new byte[totalBytes];
    prefix.CopyTo(full, 0);
    foreach (var (offset, entry) in dataWrites) {
      if (entry.Data is { Length: > 0 } inline) { inline.CopyTo(full, offset); continue; }
      using var src = entry.Opener!();
      var written = 0L;
      while (written < entry.Size) {
        var n = src.Read(full, (int)(offset + written), (int)Math.Min(64 * 1024, entry.Size - written));
        if (n <= 0) break;
        written += n;
      }
    }
    return full;
  }

  /// <summary>
  /// Writes the volume to <paramref name="output" />: the metadata prefix, then the
  /// declared length, then each file's bytes at its allocated offset. Free space costs
  /// nothing, so a volume past the in-memory limit is producible.
  /// </summary>
  public void BuildTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildTo requires a writable, seekable stream.", nameof(output));

    var prefix = BuildCore(out var dataWrites, out var totalBytes);
    output.Position = 0;
    output.Write(prefix);
    output.SetLength(totalBytes);

    var buffer = new byte[64 * 1024];
    foreach (var (offset, entry) in dataWrites) {
      output.Position = offset;
      if (entry.Data is { Length: > 0 } inline) { output.Write(inline, 0, inline.Length); continue; }

      using var src = entry.Opener!();
      var remaining = entry.Size;
      while (remaining > 0) {
        var n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
        if (n <= 0) break;
        output.Write(buffer, 0, n);
        remaining -= n;
      }
    }
    output.Flush();
  }

  /// <summary>
  /// Builds the metadata prefix and reports where each file's bytes belong.
  /// Everything this writer emits other than file data lives below the first
  /// data block, so only that prefix has to be materialised.
  /// </summary>
  private byte[] BuildCore(out List<(long Offset, FileEntry Entry)> dataWrites, out long totalBytes) {
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
    // Block 11 onward is dynamically partitioned: first any extra FS-tree leaf
    // nodes needed when the directory records overflow a single node, then the
    // file data blocks.
    const int dynamicStartBlock = 11;

    // OIDs. A physical object's identifier is not a name it can be given: it is the
    // block the object occupies, and a reference to one is followed straight to that
    // block with no map in between. Numbering these from 0x400 while writing them at
    // the front of the volume sent every such reference a thousand blocks past where
    // the object was — to zeroes, which read as a tree with no root.
    //
    // Only two objects here are virtual, and those are the two the object maps exist
    // to resolve: the volume superblock, through the container's map, and the
    // filesystem tree, through the volume's.
    const ulong ctrOmapOid = ctrOmapBlock;
    const ulong apsbVirtOid = 0x402;
    const ulong volOmapOid = volOmapBlock;
    // The filesystem tree is virtual: its nodes are reached through the volume's
    // object map, and any identifier would do. Each node is given the number of the
    // block it sits on, and the map says so — which keeps every reference to a node
    // the same whether it goes through the map or not, and makes a collision between
    // two nodes' identifiers impossible.
    const ulong fsTreeVirtOid = fsTreeBlock;
    const ulong extrefTreeOid = extrefTreeBlock;
    const ulong snapMetaTreeOid = snapMetaTreeBlock;
    const ulong xid = 4;

    // ── Build the directory tree (real directory inodes for path components) ──
    var tree = BuildTree(this._files);

    // ── Build the FS-tree records and decide on the node layout ───────────
    // The records are first sorted into APFS key order, then packed into one or
    // more leaf nodes. When everything fits in one node the FS-tree is a single
    // root-leaf (as before). When it overflows, the records spill into several
    // dedicated leaf nodes beneath an internal root node (a 2-level B-tree).
    var fileRecords = BuildFsTreeLeaf(tree);
    var leafPartitions = PartitionIntoLeaves(fileRecords);
    var isMultiLevel = leafPartitions.Count > 1;

    // Extra leaf node blocks (only when multi-level). They occupy the dynamic
    // region starting at block 11; file data follows after them.
    var extraLeafBlocks = isMultiLevel ? leafPartitions.Count : 0;
    var firstLeafBlock = (ulong)dynamicStartBlock;

    // Every leaf of the filesystem tree is named in the volume's object map, so a
    // tree that spilled into leaves spills the map along with it. Each map record is
    // the same size, so how many nodes the map needs follows from the count alone.
    var volOmapRecordCount = 1 + extraLeafBlocks;
    const int omapRecordCost = TocEntrySize + 16 + 16;
    var omapFitsInRoot = volOmapRecordCount * omapRecordCost <= NodePayloadCapacity(isRoot: true);
    var omapLeafCount = omapFitsInRoot
      ? 0
      : (volOmapRecordCount * omapRecordCost + NodePayloadCapacity(isRoot: false) - 1)
        / NodePayloadCapacity(isRoot: false);
    var firstOmapLeafBlock = firstLeafBlock + (ulong)extraLeafBlocks;
    var fileDataStartBlock = dynamicStartBlock + extraLeafBlocks + omapLeafCount;

    // Compute image size (extra leaf nodes + file data blocks).
    var fileDataBlocks = 0L;
    foreach (var entry in this._files)
      fileDataBlocks += (entry.Size + BlockSize - 1) / BlockSize;
    var usedBlocks = fileDataStartBlock + fileDataBlocks;
    var totalBlocks = Math.Max(usedBlocks + 1, this._minImageSize / BlockSize);
    totalBytes = (long)totalBlocks * BlockSize;

    // Only the metadata prefix is materialised; file data is handed back to the
    // caller to place, so the buffer scales with the metadata rather than the
    // volume and is not bounded by what an array can hold.
    var disk = new byte[(long)fileDataStartBlock * BlockSize];

    // Allocate file data blocks and record extents (one per regular file node).
    dataWrites = [];
    var nextDataBlock = (ulong)fileDataStartBlock;
    foreach (var node in tree.Nodes) {
      if (node.IsDir || node.Payload is not { Size: > 0 } payload)
        continue;
      var blocks = (ulong)((payload.Size + BlockSize - 1) / BlockSize);
      dataWrites.Add(((long)nextDataBlock * BlockSize, payload));
      node.PhysBlock = nextDataBlock;
      nextDataBlock += blocks;
    }

    // The file-extent records were built with placeholder physical block numbers
    // (the node objects are mutated above), so rebuild them now that the data
    // blocks are pinned.
    fileRecords = BuildFsTreeLeaf(tree);
    leafPartitions = PartitionIntoLeaves(fileRecords);

    // ── FS tree (block 8 is the root) ─────────────────────────────────────
    // Every node of the filesystem tree is virtual, root and leaves alike, so each
    // leaf needs an entry in the volume's object map as well as the root.
    var fsTreeLeafOids = new List<ulong>(leafPartitions.Count);
    if (!isMultiLevel) {
      // Single root-leaf node holding inodes + drec + file_extent.
      WriteBtreeRootLeaf(BlockOf(disk, fsTreeBlock),
        fileRecords, fsTreeVirtOid, OBJECT_TYPE_FSTREE | OBJ_VIRTUAL, xid);
    } else {
      // 2-level tree: an internal root at block 8 indexing dedicated leaf nodes.
      var childAddrs = new List<ulong>(leafPartitions.Count);
      var childFirstKeys = new List<byte[]>(leafPartitions.Count);
      for (var i = 0; i < leafPartitions.Count; i++) {
        var leafBlock = firstLeafBlock + (ulong)i;
        WriteBtreeLeafNode(BlockOf(disk, (long)leafBlock), leafPartitions[i],
          leafBlock, OBJECT_TYPE_FSTREE | OBJ_VIRTUAL, xid);
        childAddrs.Add(leafBlock);
        childFirstKeys.Add(leafPartitions[i][0].Key);
        fsTreeLeafOids.Add(leafBlock);
      }
      WriteBtreeRootInternal(BlockOf(disk, fsTreeBlock), childFirstKeys, childAddrs,
        fileRecords, fsTreeVirtOid, OBJECT_TYPE_FSTREE | OBJ_VIRTUAL, xid);
    }

    // ── Extent-ref tree (block 9): empty root ─────────────────────────────
    WriteBtreeRootLeaf(BlockOf(disk, extrefTreeBlock),
      [], extrefTreeOid, OBJECT_TYPE_BLOCKREFTREE | OBJ_PHYSICAL, xid);

    // ── Snap-meta tree (block 10): empty root ─────────────────────────────
    WriteBtreeRootLeaf(BlockOf(disk, snapMetaTreeBlock),
      [], snapMetaTreeOid, OBJECT_TYPE_SNAPMETATREE | OBJ_PHYSICAL, xid);

    // ── Volume OMAP B-tree root (block 7): maps FS-tree virtual OID → phys ─
    var volOmapRecs = new List<BtreeRecord> {
      BuildOmapRecord(fsTreeVirtOid, xid, (ulong)fsTreeBlock),
    };
    for (var i = 0; i < fsTreeLeafOids.Count; i++)
      volOmapRecs.Add(BuildOmapRecord(fsTreeLeafOids[i], xid, firstLeafBlock + (ulong)i));

    if (omapLeafCount == 0) {
      WriteBtreeRootLeaf(BlockOf(disk, volOmapTreeBlock),
        volOmapRecs, (ulong)volOmapTreeBlock, OBJECT_TYPE_BTREE | OBJ_PHYSICAL, xid);
    } else {
      // The map's own nodes are physical, so its root indexes them by block.
      var omapPartitions = PartitionIntoLeaves(volOmapRecs);
      if (omapPartitions.Count > omapLeafCount)
        throw new InvalidOperationException(
          "APFS writer: the volume object map needs more nodes than were reserved for it.");

      var omapChildBlocks = new List<ulong>(omapPartitions.Count);
      var omapChildKeys = new List<byte[]>(omapPartitions.Count);
      for (var i = 0; i < omapPartitions.Count; i++) {
        var block = firstOmapLeafBlock + (ulong)i;
        WriteBtreeLeafNode(BlockOf(disk, (long)block), omapPartitions[i],
          block, OBJECT_TYPE_BTREE | OBJ_PHYSICAL, xid);
        omapChildBlocks.Add(block);
        omapChildKeys.Add(omapPartitions[i][0].Key);
      }

      WriteBtreeRootInternal(BlockOf(disk, volOmapTreeBlock), omapChildKeys, omapChildBlocks,
        volOmapRecs, (ulong)volOmapTreeBlock, OBJECT_TYPE_BTREE | OBJ_PHYSICAL, xid);
    }

    // ── Volume OMAP phys (block 6) ────────────────────────────────────────
    WriteOmapPhys(BlockOf(disk, volOmapBlock),
      volOmapOid, xid, (ulong)volOmapTreeBlock);

    // ── APSB volume superblock (block 5) ──────────────────────────────────
    // NOTE: per spec, `apfs_omap_oid` is a PHYSICAL OID pointing to the volume
    // OMAP phys object (not a virtual OID resolved via the container OMAP).
    WriteVolumeSuperblock(BlockOf(disk, apsbBlock),
      apsbVirtOid, xid, volOmapPhysOid: (ulong)volOmapBlock,
      fsTreeVirtOid, extrefTreeOid, snapMetaTreeOid,
      fileCount: tree.FileCount, dirCount: tree.DirectoryCount,
      nextObjId: tree.NextObjId, volumeName: this._volumeName);

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
      ulong fileCount, ulong dirCount, ulong nextObjId, string volumeName) {
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
    // apfs_meta_crypto (20 bytes) at 0x60 — zeros for an unencrypted volume.
    BinaryPrimitives.WriteUInt32LittleEndian(block[APSB_ROOT_TREE_TYPE..], OBJECT_TYPE_BTREE | OBJ_VIRTUAL);
    BinaryPrimitives.WriteUInt32LittleEndian(block[APSB_EXTENTREF_TREE_TYPE..], OBJECT_TYPE_BTREE | OBJ_PHYSICAL);
    BinaryPrimitives.WriteUInt32LittleEndian(block[APSB_SNAP_META_TREE_TYPE..], OBJECT_TYPE_BTREE | OBJ_PHYSICAL);
    // The volume's object map, by block; then its three trees, the first by
    // virtual identifier and the other two by block.
    BinaryPrimitives.WriteUInt64LittleEndian(block[APSB_OMAP_OID..], volOmapPhysOid);
    BinaryPrimitives.WriteUInt64LittleEndian(block[APSB_ROOT_TREE_OID..], rootTreeOid);
    BinaryPrimitives.WriteUInt64LittleEndian(block[APSB_EXTENTREF_TREE_OID..], extrefTreeOid);
    BinaryPrimitives.WriteUInt64LittleEndian(block[APSB_SNAP_META_TREE_OID..], snapMetaTreeOid);
    BinaryPrimitives.WriteUInt64LittleEndian(block[APSB_REVERT_TO_XID..], 0);
    BinaryPrimitives.WriteUInt64LittleEndian(block[APSB_REVERT_TO_SBLOCK_OID..], 0);
    BinaryPrimitives.WriteUInt64LittleEndian(block[APSB_NEXT_OBJ_ID..], nextObjId);
    BinaryPrimitives.WriteUInt64LittleEndian(block[APSB_NUM_FILES..], fileCount);
    BinaryPrimitives.WriteUInt64LittleEndian(block[APSB_NUM_DIRECTORIES..], dirCount);
    // Symlink, other-object and snapshot counts, and the allocated/freed totals,
    // all follow and are all zero here.
    Guid.NewGuid().ToByteArray().CopyTo(block[APSB_VOL_UUID..]);
    BinaryPrimitives.WriteUInt64LittleEndian(block[APSB_LAST_MOD_TIME..],
      (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
    // Saying nothing here reads as "encrypted", which is not what this volume is.
    BinaryPrimitives.WriteUInt64LittleEndian(block[APSB_FS_FLAGS..], APFS_FS_UNENCRYPTED);
    // apfs_formatted_by: id[32] + timestamp(u64) + last_xid(u64), then eight more
    // of the same for the modification history, all left zero. The identifier names
    // the thing that laid the volume down, and every APFS volume in existence
    // carries the same shape of string here; putting this project's name in it
    // would be the one field that tells our volumes from anyone else's at a glance.
    "newfs_apfs (1934.141.2)"u8.CopyTo(block[APSB_FORMATTED_BY..]);
    var volnameBytes = Encoding.UTF8.GetBytes(volumeName);
    var volnameLen = Math.Min(volnameBytes.Length, APSB_VOLNAME_LEN - 1);
    block.Slice(APSB_VOLNAME, APSB_VOLNAME_LEN).Clear();
    volnameBytes.AsSpan(0, volnameLen).CopyTo(block[APSB_VOLNAME..]);

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

  // btn_phys header occupies bytes [32..56); record data begins at offset 56.
  private const int BtnHeaderEnd = 56;
  // Each TOC entry (kvloc_t) is key_off/key_len/val_off/val_len, u16 each = 8 bytes.
  private const int TocEntrySize = 8;

  /// <summary>
  /// Bytes a node can devote to TOC + keys + values, after the object header,
  /// the btn_phys header and (for a root node) the trailing btree_info footer.
  /// </summary>
  private static int NodePayloadCapacity(bool isRoot) =>
    (int)BlockSize - BtnHeaderEnd - (isRoot ? BtreeInfoSize : 0);

  /// <summary>
  /// Splits the key-sorted FS-tree records into the minimum number of leaf nodes
  /// so that each leaf's TOC + key area + value area fits one block. When all the
  /// records fit in a single root-leaf node, a single partition is returned and
  /// the caller keeps the original single-level layout. Records are never
  /// reordered, so the per-leaf order remains the global APFS key ordering and a
  /// leaf's first key is a valid separator for the internal index.
  /// </summary>
  private static List<List<BtreeRecord>> PartitionIntoLeaves(IReadOnlyList<BtreeRecord> records) {
    var partitions = new List<List<BtreeRecord>>();
    if (records.Count == 0) {
      partitions.Add([]);
      return partitions;
    }

    // First try: does everything fit in one root-leaf node?
    var rootLeafCap = NodePayloadCapacity(isRoot: true);
    var totalUsed = 0;
    foreach (var r in records)
      totalUsed += TocEntrySize + r.Key.Length + r.Value.Length;
    if (totalUsed <= rootLeafCap) {
      partitions.Add([.. records]);
      return partitions;
    }

    // Overflow: pack into non-root leaf nodes (no btree_info footer).
    var leafCap = NodePayloadCapacity(isRoot: false);
    var current = new List<BtreeRecord>();
    var used = 0;
    foreach (var r in records) {
      var cost = TocEntrySize + r.Key.Length + r.Value.Length;
      if (current.Count > 0 && used + cost > leafCap) {
        partitions.Add(current);
        current = [];
        used = 0;
      }
      current.Add(r);
      used += cost;
    }
    if (current.Count > 0)
      partitions.Add(current);
    return partitions;
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
      ulong oid, uint type, ulong xid)
    => WriteBtreeNode(block, records, oid, type, xid,
         flags: (ushort)(BTNODE_ROOT | BTNODE_LEAF), level: 0,
         isRoot: true, nodeCount: 1);

  /// <summary>
  /// Writes a non-root leaf node (level 0): a child of an internal index node.
  /// Holds variable-length key/value records and carries no btree_info footer.
  /// </summary>
  private static void WriteBtreeLeafNode(Span<byte> block, IReadOnlyList<BtreeRecord> records,
      ulong oid, uint type, ulong xid)
    => WriteBtreeNode(block, records, oid, type, xid,
         flags: BTNODE_LEAF, level: 0, isRoot: false, nodeCount: 0);

  /// <summary>
  /// Writes the internal root node (level 1) of a 2-level FS-tree. Its records map
  /// each child leaf's first key to that leaf's physical block address (an 8-byte
  /// oid_t value). The btree_info footer reports the whole tree's key/node counts.
  /// </summary>
  private static void WriteBtreeRootInternal(Span<byte> block,
      IReadOnlyList<byte[]> childFirstKeys, IReadOnlyList<ulong> childAddrs,
      IReadOnlyList<BtreeRecord> allRecords, ulong oid, uint type, ulong xid) {
    var indexRecords = new List<BtreeRecord>(childAddrs.Count);
    for (var i = 0; i < childAddrs.Count; i++) {
      var val = new byte[8];
      BinaryPrimitives.WriteUInt64LittleEndian(val, childAddrs[i]);
      indexRecords.Add(new BtreeRecord(childFirstKeys[i], val));
    }
    // Whole-tree key count = all leaf records; node count = internal + leaves.
    var totalKeys = (ulong)allRecords.Count;
    var totalNodes = (ulong)(1 + childAddrs.Count);
    WriteBtreeNode(block, indexRecords, oid, type, xid,
      flags: BTNODE_ROOT, level: 1, isRoot: true,
      nodeCount: totalNodes, keyCountOverride: totalKeys);
  }

  /// <summary>
  /// Shared B-tree node serializer. Writes the object header, btn_phys header,
  /// table of contents, the key area (growing forward) and the value area
  /// (growing backward), and — for root nodes — the trailing btree_info footer.
  /// Finishes by stamping the Fletcher-64 checksum.
  /// </summary>
  private static void WriteBtreeNode(Span<byte> block, IReadOnlyList<BtreeRecord> records,
      ulong oid, uint type, ulong xid, ushort flags, ushort level, bool isRoot,
      ulong nodeCount, ulong? keyCountOverride = null) {
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
    BinaryPrimitives.WriteUInt16LittleEndian(block[32..], flags);
    BinaryPrimitives.WriteUInt16LittleEndian(block[34..], level);
    BinaryPrimitives.WriteUInt32LittleEndian(block[36..], (uint)records.Count);

    var tocOff = BtnHeaderEnd;
    var tocLen = records.Count * TocEntrySize;
    BinaryPrimitives.WriteUInt16LittleEndian(block[40..], 0);              // btn_table_space.off
    BinaryPrimitives.WriteUInt16LittleEndian(block[42..], (ushort)tocLen); // btn_table_space.len

    var keyAreaStart = tocOff + tocLen;
    // Root nodes reserve the trailing btree_info; non-root nodes use the whole block.
    var valAreaEnd = isRoot ? block.Length - BtreeInfoSize : block.Length;

    var keyCursor = keyAreaStart;
    var valCursor = valAreaEnd;

    for (var i = 0; i < records.Count; i++) {
      var rec = records[i];
      var keyRelOff = (ushort)(keyCursor - keyAreaStart);
      rec.Key.CopyTo(block[keyCursor..]);
      keyCursor += rec.Key.Length;

      valCursor -= rec.Value.Length;
      rec.Value.CopyTo(block[valCursor..]);
      var valRelOff = (ushort)(valAreaEnd - valCursor);

      var entryOff = tocOff + i * TocEntrySize;
      BinaryPrimitives.WriteUInt16LittleEndian(block[entryOff..], keyRelOff);
      BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 2)..], (ushort)rec.Key.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 4)..], valRelOff);
      BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 6)..], (ushort)rec.Value.Length);
    }

    if (keyCursor > valCursor)
      throw new InvalidOperationException(
        $"APFS B-tree node overflow: {records.Count} records exceed one block.");

    // btn_free_space: free area between keyCursor and valCursor.
    BinaryPrimitives.WriteUInt16LittleEndian(block[44..], (ushort)(keyCursor - keyAreaStart));
    BinaryPrimitives.WriteUInt16LittleEndian(block[46..], (ushort)(valCursor - keyCursor));
    // btn_key_free_list / btn_val_free_list: empty.
    BinaryPrimitives.WriteUInt16LittleEndian(block[48..], BTOFF_INVALID);
    BinaryPrimitives.WriteUInt16LittleEndian(block[50..], 0);
    BinaryPrimitives.WriteUInt16LittleEndian(block[52..], BTOFF_INVALID);
    BinaryPrimitives.WriteUInt16LittleEndian(block[54..], 0);

    if (isRoot) {
      // btree_info (40 bytes) at end of root node.
      var infoOff = block.Length - BtreeInfoSize;
      BinaryPrimitives.WriteUInt32LittleEndian(block[infoOff..], 0);                       // bt_flags
      BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 4)..], (uint)block.Length); // bt_node_size
      BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 8)..], 0);                 // bt_key_size (variable)
      BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 12)..], 0);                // bt_val_size (variable)
      var longestKey = 0;
      var longestVal = 0;
      foreach (var r in records) {
        if (r.Key.Length > longestKey) longestKey = r.Key.Length;
        if (r.Value.Length > longestVal) longestVal = r.Value.Length;
      }
      BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 16)..], (uint)longestKey);
      BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 20)..], (uint)longestVal);
      BinaryPrimitives.WriteUInt64LittleEndian(block[(infoOff + 24)..],
        keyCountOverride ?? (ulong)records.Count);
      BinaryPrimitives.WriteUInt64LittleEndian(block[(infoOff + 32)..], nodeCount);
    }

    ApfsFletcher64.Stamp(block);
  }

  // ── FS-tree record builders ─────────────────────────────────────────────

  // ── Directory tree model ─────────────────────────────────────────────────

  /// <summary>A single node (file or directory inode) in the volume's directory tree.</summary>
  private sealed class FsNode {
    public required ulong Ino { get; init; }
    public required ulong ParentIno { get; init; }
    public required string Name { get; init; }
    public required bool IsDir { get; init; }
    public FileEntry? Payload { get; init; }
    /// <summary>Direct children (for directory inodes) used to compute nchildren.</summary>
    public int ChildCount { get; set; }
    /// <summary>First physical data block; assigned for non-empty regular files.</summary>
    public ulong PhysBlock { get; set; }
  }

  /// <summary>The fully expanded directory tree, including synthesised directory inodes.</summary>
  private sealed class FsTree {
    public required List<FsNode> Nodes { get; init; }
    public required ulong NextObjId { get; init; }
    public required ulong FileCount { get; init; }
    public required ulong DirectoryCount { get; init; }
  }

  /// <summary>
  /// Expands the flat <c>AddFile(name, data)</c> list — where <c>name</c> may carry
  /// '/' or '\\' path separators — into a tree of real inodes: one regular-file
  /// inode per file and one directory inode per intermediate path component, each
  /// with its own object id and a DIR_REC entry under its parent. The root
  /// directory (oid 2) is the implicit common parent.
  /// </summary>
  private static FsTree BuildTree(IReadOnlyList<FileEntry> files) {
    var nodes = new List<FsNode>();
    // Map of directory path (forward-slash, no leading/trailing slash) → inode number.
    // The empty path is the root directory.
    var dirIno = new Dictionary<string, ulong>(StringComparer.Ordinal) {
      [string.Empty] = APFS_ROOT_DIR_INO_NUM,
    };
    var childCounts = new Dictionary<ulong, int> { [APFS_ROOT_DIR_INO_NUM] = 0 };

    var nextIno = APFS_MIN_USER_INO_NUM;
    var dirCount = 0UL; // does not include the root (counted separately in the superblock)

    foreach (var entry in files) {
      var parts = entry.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0)
        continue;

      // Ensure every intermediate directory exists, allocating inodes on first sight.
      var parentIno = APFS_ROOT_DIR_INO_NUM;
      var accumulated = string.Empty;
      for (var d = 0; d < parts.Length - 1; d++) {
        var component = parts[d];
        accumulated = accumulated.Length == 0 ? component : accumulated + "/" + component;
        if (!dirIno.TryGetValue(accumulated, out var ino)) {
          ino = nextIno++;
          dirIno[accumulated] = ino;
          childCounts[ino] = 0;
          ++childCounts[parentIno];
          ++dirCount;
          nodes.Add(new FsNode {
            Ino = ino, ParentIno = parentIno, Name = component, IsDir = true,
          });
        }
        parentIno = ino;
      }

      // The file inode itself.
      var fileName = parts[^1];
      var fileIno = nextIno++;
      ++childCounts[parentIno];
      nodes.Add(new FsNode {
        Ino = fileIno, ParentIno = parentIno, Name = fileName, IsDir = false, Payload = entry,
      });
    }

    // Propagate computed child counts back onto the directory nodes.
    foreach (var node in nodes)
      if (node.IsDir && childCounts.TryGetValue(node.Ino, out var count))
        node.ChildCount = count;

    var fileCount = (ulong)nodes.Count(n => !n.IsDir);
    return new FsTree {
      Nodes = nodes,
      NextObjId = nextIno,
      FileCount = fileCount,
      // +1 for the root directory.
      DirectoryCount = dirCount + 1,
    };
  }

  /// <summary>
  /// Builds the complete leaf contents for the filesystem B-tree from the expanded
  /// directory tree: one INODE record for the root dir, one INODE plus a DIR_REC in
  /// the parent for every node, and one FILE_EXTENT per non-empty regular file.
  /// Records are emitted in APFS key-sorted (oid asc, type asc, name asc) order.
  /// </summary>
  private static List<BtreeRecord> BuildFsTreeLeaf(FsTree tree) {
    var list = new List<(ulong KeyOid, int KeyType, string Name, byte[] Key, byte[] Value)>();

    // Root directory inode — nchildren = number of entries directly under it.
    var rootChildren = tree.Nodes.Count(n => n.ParentIno == APFS_ROOT_DIR_INO_NUM);
    list.Add((APFS_ROOT_DIR_INO_NUM, APFS_TYPE_INODE, string.Empty,
      BuildInodeKey(APFS_ROOT_DIR_INO_NUM),
      BuildInodeValue(APFS_ROOT_DIR_INO_NUM, parentId: APFS_ROOT_DIR_PARENT,
        size: 0, isDir: true, nchildren: (uint)rootChildren)));

    // The private directory. A mount reads this inode before it reads the root and
    // refuses the volume when it is not there — it is where a file goes that is
    // still open after its last name is gone. Nothing in the root links to it, so
    // it is an inode record and nothing else.
    list.Add((APFS_PRIV_DIR_INO_NUM, APFS_TYPE_INODE, string.Empty,
      BuildInodeKey(APFS_PRIV_DIR_INO_NUM),
      BuildInodeValue(APFS_PRIV_DIR_INO_NUM, parentId: APFS_ROOT_DIR_PARENT,
        size: 0, isDir: true, nchildren: 0, internalFlags: APFS_INODE_IS_APFS_PRIVATE)));

    foreach (var node in tree.Nodes) {
      // DIR_REC under the parent directory pointing at this node.
      list.Add((node.ParentIno, APFS_TYPE_DIR_REC, node.Name,
        BuildDrecKey(node.ParentIno, node.Name),
        BuildDrecValue(node.Ino, isDir: node.IsDir)));

      // The node's own inode.
      var size = node.IsDir ? 0L : (node.Payload?.Size ?? 0L);
      list.Add((node.Ino, APFS_TYPE_INODE, string.Empty,
        BuildInodeKey(node.Ino),
        BuildInodeValue(node.Ino, parentId: node.ParentIno, size: size,
          isDir: node.IsDir, nchildren: node.IsDir ? (uint)node.ChildCount : 1u)));

      // Every file that has a data stream needs a record counting who shares it.
      // A driver looks this up before it will open the file, and treats its absence
      // as corruption rather than as "no one else has it".
      if (!node.IsDir) {
        list.Add((node.Ino, APFS_TYPE_DSTREAM_ID, string.Empty,
          ApfsInodeRecord.BuildDstreamIdKey(node.Ino), ApfsInodeRecord.BuildDstreamIdValue(refCount: 1)));
      }

      // FILE_EXTENT for non-empty regular files.
      if (!node.IsDir && node.Payload is { Size: > 0 } payload) {
        list.Add((node.Ino, APFS_TYPE_FILE_EXTENT, string.Empty,
          BuildFileExtentKey(node.Ino, logicalOffset: 0),
          BuildFileExtentValue(lengthBytes: (ulong)payload.Size,
            physBlockNum: node.PhysBlock)));
      }
    }

    // Sort by (oid asc, type asc, name asc) — APFS B-tree leaf ordering rule.
    list.Sort((a, b) => {
      var cmp = a.KeyOid.CompareTo(b.KeyOid);
      if (cmp != 0) return cmp;
      cmp = a.KeyType.CompareTo(b.KeyType);
      if (cmp != 0) return cmp;
      return string.CompareOrdinal(a.Name, b.Name);
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

  private static byte[] BuildDrecKey(ulong parentOid, string name)
    => ApfsDrecKey.Build(parentOid, name);

  private static byte[] BuildFileExtentKey(ulong ino, ulong logicalOffset) {
    // j_file_extent_key_t: u64 oid_and_type + u64 logical_addr.
    var k = new byte[16];
    var oidAndType = ino | ((ulong)APFS_TYPE_FILE_EXTENT << 60);
    BinaryPrimitives.WriteUInt64LittleEndian(k, oidAndType);
    BinaryPrimitives.WriteUInt64LittleEndian(k.AsSpan(8), logicalOffset);
    return k;
  }

  // Value helpers.

  private static byte[] BuildInodeValue(ulong ino, ulong parentId, long size, bool isDir, uint nchildren,
      ulong internalFlags = 0)
    => ApfsInodeRecord.BuildValue(ino, parentId, size, isDir, nchildren, internalFlags);


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

  /// <summary>
  /// Writes one file extent: how much it covers, where, and under whose key.
  /// </summary>
  /// <remarks>
  /// The length is what the extent covers on disk, not what the file holds — it names
  /// whole blocks, and a driver rejects an extent whose length is not a multiple of
  /// one. The file's own length is recorded on its inode instead.
  /// </remarks>
  private static byte[] BuildFileExtentValue(ulong lengthBytes, ulong physBlockNum) {
    // j_file_extent_val_t: u64 len_and_flags; u64 phys_block_num; u64 crypto_id.
    var v = new byte[24];
    var covered = (lengthBytes + BlockSize - 1) / BlockSize * BlockSize;
    // Low 56 bits = length in bytes; high 8 bits = flags (0 = no compression).
    var lenAndFlags = covered & 0x00FFFFFFFFFFFFFFUL;
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
