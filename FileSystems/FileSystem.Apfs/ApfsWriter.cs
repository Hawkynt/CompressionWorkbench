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
    //   0    — NX superblock
    //   1    — Checkpoint descriptor #1: checkpoint map
    //   2    — Checkpoint descriptor #2: NX superblock copy
    //   3–8  — the rest of the checkpoint descriptor area, spare and zeroed
    //   9–16 — the checkpoint data area
    //  17    — Container OMAP phys
    //  18    — Container OMAP B-tree root (maps APSB virtual OID → physical block)
    //  19    — APSB (volume superblock)
    //  20    — Volume OMAP phys
    //  21    — Volume OMAP B-tree root (maps FS-tree/extref/snap virtual OIDs → blocks)
    //  22    — FS root B-tree node (inodes + drec + file extents)
    //  23    — Extent-ref tree root (empty leaf)
    //  24    — Snap-meta tree root (empty leaf)
    //  25…   — File data blocks
    //
    // The superblock declares a descriptor area of eight blocks from block 1, and
    // the volume metadata used to start at block 3 — inside it. It also declared
    // no checkpoint data area at all, which is what fsck.apfs meant by "out of
    // range checkpoint data": a window of index 0 in an area of nothing. Both
    // areas are now real and neither overlaps anything else.
    const int nxSbBlock = 0;
    const int chkMapBlock = 1;
    const int chkSbCopyBlock = 2;
    const int chkDescBase = 1;
    const int chkDescBlocks = 8;
    const int chkDataBase = chkDescBase + chkDescBlocks;   // 9
    const int chkDataBlocks = 8;                            // 9..16
    const int ctrOmapBlock = chkDataBase + chkDataBlocks;   // 17
    const int ctrOmapTreeBlock = ctrOmapBlock + 1;
    const int apsbBlock = ctrOmapTreeBlock + 1;
    const int volOmapBlock = apsbBlock + 1;
    const int volOmapTreeBlock = volOmapBlock + 1;
    const int fsTreeBlock = volOmapTreeBlock + 1;
    const int extrefTreeBlock = fsTreeBlock + 1;
    const int snapMetaTreeBlock = extrefTreeBlock + 1;
    // The block after that is dynamically partitioned: first any extra FS-tree
    // leaf nodes needed when the directory records overflow a single node, then
    // the file data blocks.
    const int dynamicStartBlock = snapMetaTreeBlock + 1;

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
    // A virtual object's identifier is free to be anything, but not anything at
    // all: everything below OID_RESERVED_COUNT belongs to the format. Numbering
    // these by their block put them squarely inside that range — "reserved object
    // id in block 0x16" — so the block number is lifted clear of it. The map
    // still names each node by the block it sits on, which is what keeps two
    // nodes from ever sharing an identifier.
    const ulong fsTreeVirtOid = OID_RESERVED_COUNT + fsTreeBlock;
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

    var fileDataBlocks = 0L;
    foreach (var entry in this._files)
      fileDataBlocks += (entry.Size + BlockSize - 1) / BlockSize;

    // The space manager keeps the container's allocation bitmap in an internal
    // pool: one bitmap block per chunk, a chunk-info block naming them, and a
    // ring of blocks for the pool's own bitmap. How many chunks there are
    // depends on how big the container is, and that depends on the pool — so the
    // two are settled against each other rather than guessed at. The divisor is
    // 32 768 blocks, so this agrees on the first or second pass.
    // The extent-reference tree names every run of blocks a file occupies, so it
    // grows with the file count and spills out of one node just as the
    // filesystem tree does. It used to be written as a single root whatever it
    // held, which overflowed the moment a volume had a few hundred files in it.
    var extentBearingFiles = this._files.Count(f => f.Size > 0);
    const int extrefRecordCost = TocEntrySize + 8 + 20;
    var extrefFitsInRoot = extentBearingFiles * extrefRecordCost <= NodePayloadCapacity(isRoot: true);
    var extrefLeafCount = extrefFitsInRoot
      ? 0
      : (extentBearingFiles * extrefRecordCost + NodePayloadCapacity(isRoot: false) - 1)
        / NodePayloadCapacity(isRoot: false);
    var firstExtrefLeafBlock = (ulong)(dynamicStartBlock + extraLeafBlocks + omapLeafCount);

    var afterTrees = dynamicStartBlock + extraLeafBlocks + omapLeafCount + extrefLeafCount;
    var chunkCount = 1;
    long ipBmBase = afterTrees, ipBase = 0, fileDataStartBlock = 0;
    long usedBlocks = 0, totalBlocks = 0;
    var ipBlockCount = 0;
    for (var pass = 0; pass < 4; ++pass) {
      ipBlockCount = InternalPoolBlocks(chunkCount);
      ipBase = ipBmBase + IpBitmapRingBlocks;
      fileDataStartBlock = ipBase + ipBlockCount;
      usedBlocks = fileDataStartBlock + fileDataBlocks;
      totalBlocks = Math.Max(usedBlocks + 1, this._minImageSize / BlockSize);

      var settled = (int)((totalBlocks + BlocksPerChunk - 1) / BlocksPerChunk);
      if (settled == chunkCount) break;
      chunkCount = settled;
    }
    totalBytes = totalBlocks * BlockSize;

    // Only the metadata prefix is materialised; file data is handed back to the
    // caller to place, so the buffer scales with the metadata rather than the
    // volume and is not bounded by what an array can hold.
    var disk = new byte[fileDataStartBlock * BlockSize];

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
        fileRecords, fsTreeVirtOid, OBJECT_TYPE_BTREE | OBJ_VIRTUAL, xid, subtype: OBJECT_TYPE_FSTREE);
    } else {
      // 2-level tree: an internal root at block 8 indexing dedicated leaf nodes.
      var childAddrs = new List<ulong>(leafPartitions.Count);
      var childFirstKeys = new List<byte[]>(leafPartitions.Count);
      for (var i = 0; i < leafPartitions.Count; i++) {
        var leafBlock = firstLeafBlock + (ulong)i;
        WriteBtreeLeafNode(BlockOf(disk, (long)leafBlock), leafPartitions[i],
          OID_RESERVED_COUNT + leafBlock, OBJECT_TYPE_BTREE | OBJ_VIRTUAL, xid,
          subtype: OBJECT_TYPE_FSTREE);
        // A virtual tree names its children by identifier, not by block: the
        // object map is what turns one into the other. Putting the block number
        // here made every child an identifier the map had never heard of.
        childAddrs.Add(OID_RESERVED_COUNT + leafBlock);
        childFirstKeys.Add(leafPartitions[i][0].Key);
        fsTreeLeafOids.Add(OID_RESERVED_COUNT + leafBlock);
      }
      WriteBtreeRootInternal(BlockOf(disk, fsTreeBlock), childFirstKeys, childAddrs,
        fileRecords, fsTreeVirtOid, OBJECT_TYPE_BTREE | OBJ_VIRTUAL, xid, subtype: OBJECT_TYPE_FSTREE);
    }

    // ── Extent-ref tree: one record per file's run of blocks ──────────────
    // Its nodes are physical, so a child is named by its block and needs no
    // entry in any object map — which makes spilling it simpler than the
    // filesystem tree.
    var extrefRecords = BuildExtentRefRecords(tree);
    var extrefPartitions = PartitionIntoLeaves(extrefRecords);
    if (extrefPartitions.Count <= 1) {
      WriteBtreeRootLeaf(BlockOf(disk, extrefTreeBlock),
        extrefRecords, extrefTreeOid, OBJECT_TYPE_BTREE | OBJ_PHYSICAL, xid,
        subtype: OBJECT_TYPE_BLOCKREFTREE);
    } else {
      var extrefAddrs = new List<ulong>(extrefPartitions.Count);
      var extrefKeys = new List<byte[]>(extrefPartitions.Count);
      for (var i = 0; i < extrefPartitions.Count; i++) {
        var leafBlock = firstExtrefLeafBlock + (ulong)i;
        WriteBtreeLeafNode(BlockOf(disk, (long)leafBlock), extrefPartitions[i],
          leafBlock, OBJECT_TYPE_BTREE | OBJ_PHYSICAL, xid,
          subtype: OBJECT_TYPE_BLOCKREFTREE);
        extrefAddrs.Add(leafBlock);
        extrefKeys.Add(extrefPartitions[i][0].Key);
      }
      WriteBtreeRootInternal(BlockOf(disk, extrefTreeBlock), extrefKeys, extrefAddrs,
        extrefRecords, extrefTreeOid, OBJECT_TYPE_BTREE | OBJ_PHYSICAL, xid,
        subtype: OBJECT_TYPE_BLOCKREFTREE);
    }

    // ── Snap-meta tree (block 10): empty root ─────────────────────────────
    WriteBtreeRootLeaf(BlockOf(disk, snapMetaTreeBlock),
      [], snapMetaTreeOid, OBJECT_TYPE_BTREE | OBJ_PHYSICAL, xid,
      subtype: OBJECT_TYPE_SNAPMETATREE);

    // ── Volume OMAP B-tree root (block 7): maps FS-tree virtual OID → phys ─
    var volOmapRecs = new List<BtreeRecord> {
      BuildOmapRecord(fsTreeVirtOid, xid, (ulong)fsTreeBlock),
    };
    for (var i = 0; i < fsTreeLeafOids.Count; i++)
      volOmapRecs.Add(BuildOmapRecord(fsTreeLeafOids[i], xid, firstLeafBlock + (ulong)i));

    if (omapLeafCount == 0) {
      WriteBtreeRootLeaf(BlockOf(disk, volOmapTreeBlock),
        volOmapRecs, (ulong)volOmapTreeBlock, OBJECT_TYPE_BTREE | OBJ_PHYSICAL, xid,
        subtype: OBJECT_TYPE_OMAP, fixedKv: true);
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
          block, OBJECT_TYPE_BTREE | OBJ_PHYSICAL, xid,
          subtype: OBJECT_TYPE_OMAP, fixedKv: true);
        omapChildBlocks.Add(block);
        omapChildKeys.Add(omapPartitions[i][0].Key);
      }

      WriteBtreeRootInternal(BlockOf(disk, volOmapTreeBlock), omapChildKeys, omapChildBlocks,
        volOmapRecs, (ulong)volOmapTreeBlock, OBJECT_TYPE_BTREE | OBJ_PHYSICAL, xid,
        subtype: OBJECT_TYPE_OMAP, fixedKv: true);
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
      nextObjId: tree.NextObjId, volumeName: this._volumeName,
      // Its object map and that map's tree, the filesystem tree, the
      // extent-reference tree and the snapshot-metadata tree, plus whatever the
      // trees spilled into and every block of file data.
      allocCount: (ulong)(5 + extraLeafBlocks + omapLeafCount + extrefLeafCount + fileDataBlocks));

    // ── Container OMAP B-tree root (block 4): maps APSB virtual OID → phys ─
    var ctrOmapRecs = new List<BtreeRecord> {
      BuildOmapRecord(apsbVirtOid, xid, (ulong)apsbBlock),
    };
    WriteBtreeRootLeaf(BlockOf(disk, ctrOmapTreeBlock),
      ctrOmapRecs, (ulong)ctrOmapTreeBlock, OBJECT_TYPE_BTREE | OBJ_PHYSICAL, xid,
      subtype: OBJECT_TYPE_OMAP, fixedKv: true);

    // ── Container OMAP phys (block 3) ─────────────────────────────────────
    WriteOmapPhys(BlockOf(disk, ctrOmapBlock),
      ctrOmapOid, xid, (ulong)ctrOmapTreeBlock, manuallyManaged: true);

    // ── The ephemeral objects, in the checkpoint data area ────────────────
    // A container keeps its space manager and its reaper here, and the
    // checkpoint map names them. Without them the container describes no
    // allocation state at all, which is what fsck.apfs means by "Ephemeral
    // object: missing checkpoint mapping".
    var reaperBlock = chkDataBase;
    var spacemanBlock = chkDataBase + 1;
    var freeQueueBlock = chkDataBase + 2;          // two trees, at +2 and +3
    const ulong spacemanOid = OID_RESERVED_COUNT;      // 1024
    const ulong reaperOid = OID_RESERVED_COUNT + 1;    // 1025
    const ulong freeQueueOid = OID_RESERVED_COUNT + 4; // 1028, 1029

    WriteReaper(BlockOf(disk, reaperBlock), reaperOid, xid);
    for (var i = 0; i < 2; ++i)
      WriteBtreeRootLeaf(BlockOf(disk, freeQueueBlock + i), [], freeQueueOid + (ulong)i,
        OBJECT_TYPE_BTREE | OBJ_EPHEMERAL, xid,
        subtype: OBJECT_TYPE_SPACEMAN_FREE_QUEUE, fixedKv: true,
        leafKeySize: 16, leafValueSize: 8, btreeFlags: BTREE_ALLOW_GHOSTS);

    // The pool: one bitmap block per chunk, then the chunk-info block naming
    // them. Every block below usedBlocks is taken — this writer hands them out
    // in order — and everything above is free.
    WriteChunkBitmaps(disk, ipBase, chunkCount, usedBlocks, totalBlocks);
    var cibBlock = ipBase + chunkCount;
    WriteChunkInfoBlock(disk, cibBlock, ipBase, chunkCount, usedBlocks, totalBlocks, xid);
    WriteInternalPoolBitmap(disk, ipBmBase, chunkCount);

    WriteSpaceman(BlockOf(disk, spacemanBlock), spacemanOid, xid, totalBlocks, usedBlocks,
      chunkCount, cibBlock, ipBmBase, ipBase, ipBlockCount, freeQueueOid);

    // ── Checkpoint descriptor area ────────────────────────────────────────
    // Block 1: the checkpoint map, naming every ephemeral object above.
    WriteCheckpointMap(BlockOf(disk, chkMapBlock), xid, (ulong)chkMapBlock, [
      (OBJECT_TYPE_NX_REAPER | OBJ_EPHEMERAL, 0u, reaperOid, (ulong)reaperBlock),
      (OBJECT_TYPE_SPACEMAN | OBJ_EPHEMERAL, 0u, spacemanOid, (ulong)spacemanBlock),
      (OBJECT_TYPE_BTREE | OBJ_EPHEMERAL, OBJECT_TYPE_SPACEMAN_FREE_QUEUE,
        freeQueueOid, (ulong)freeQueueBlock),
      (OBJECT_TYPE_BTREE | OBJ_EPHEMERAL, OBJECT_TYPE_SPACEMAN_FREE_QUEUE,
        freeQueueOid + 1, (ulong)(freeQueueBlock + 1)),
    ]);
    // Block 2: NX superblock copy (written below, same as primary).

    // ── NX superblock at block 0 (and copy at block 2) ────────────────────
    WriteNxSuperblock(BlockOf(disk, nxSbBlock),
      NX_SUPERBLOCK_OID, xid, (ulong)totalBlocks, ctrOmapOid, (ulong)ctrOmapBlock,
      apsbVirtOid, chkSbCopyBlock: chkSbCopyBlock, chkMapBlock: chkMapBlock,
      descBase: chkDescBase, descBlocks: chkDescBlocks,
      dataBase: chkDataBase, dataBlocks: chkDataBlocks,
      spacemanOid: spacemanOid, reaperOid: reaperOid, ephemeralCount: 4);
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
      int chkSbCopyBlock, int chkMapBlock,
      int descBase, int descBlocks, int dataBase, int dataBlocks,
      ulong spacemanOid, ulong reaperOid, int ephemeralCount) {
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
    var uuid = Guid.NewGuid().ToByteArray(bigEndian: true);
    uuid.CopyTo(block[72..]);
    // nx_next_oid at 88.
    BinaryPrimitives.WriteUInt64LittleEndian(block[88..], 0x1000);
    // nx_next_xid at 96.
    BinaryPrimitives.WriteUInt64LittleEndian(block[96..], xid + 1);
    // nx_xp_desc_blocks (u32) at 104.
    BinaryPrimitives.WriteUInt32LittleEndian(block[104..], (uint)descBlocks);
    // nx_xp_data_blocks (u32) at 108. Zero here is not "no ephemeral state to
    // keep"; it is an area a checkpoint cannot name a position inside, and
    // fsck.apfs refuses the container over it.
    BinaryPrimitives.WriteUInt32LittleEndian(block[108..], (uint)dataBlocks);
    // nx_xp_desc_base (i64) at 112.
    BinaryPrimitives.WriteInt64LittleEndian(block[112..], descBase);
    // nx_xp_data_base (i64) at 120.
    BinaryPrimitives.WriteInt64LittleEndian(block[120..], dataBase);
    // nx_xp_desc_next (u32) at 128 — points to next free slot in descriptor ring.
    BinaryPrimitives.WriteUInt32LittleEndian(block[128..], 2);
    // nx_xp_data_next (u32) at 132 — the next free slot of the data area.
    BinaryPrimitives.WriteUInt32LittleEndian(block[132..], (uint)ephemeralCount);
    // nx_xp_desc_index (u32) at 136 — start of current checkpoint in descriptor ring.
    BinaryPrimitives.WriteUInt32LittleEndian(block[136..], (uint)chkMapBlock - 1);
    // nx_xp_desc_len (u32) at 140 — length of current checkpoint (map + sb copy).
    BinaryPrimitives.WriteUInt32LittleEndian(block[140..], 2);
    // nx_xp_data_index (u32) at 144 — where this checkpoint's data begins.
    BinaryPrimitives.WriteUInt32LittleEndian(block[144..], 0);
    // nx_xp_data_len (u32) at 148 — and how many blocks of it there are. Zero
    // here described a checkpoint holding none of the objects it names.
    BinaryPrimitives.WriteUInt32LittleEndian(block[148..], (uint)ephemeralCount);
    // nx_ephemeral_info[0] at 1312. Not a free-form field: it is a fixed
    // description of how much room the container keeps for ephemeral state, and
    // apfsprogs compares it against the constant rather than reading anything
    // out of it — "bad first entry in ephemeral info" when it is left at zero.
    //   (min_block_count << 32) | (NX_MAX_FILE_SYSTEM_EPH_STRUCTS << 16)
    //   | NX_EPH_INFO_VERSION_1
    //
    // The minimum block count is not a fixed 8: it grows with the container and
    // levels off there. The steps below are measured against mkfs.apfs rather
    // than derived — every one of them was read out of a container it built —
    // because the rule behind them is not documented and guessing at it would
    // put a number here that only looked right.
    BinaryPrimitives.WriteUInt64LittleEndian(block[1312..],
      (EphemeralMinBlockCount((long)totalBlocks) << 32) | (4UL << 16) | 1UL);

    // nx_spaceman_oid at 152 — the container's allocator, and below it the
    // reaper. Both were zero, which says a container that keeps no account of
    // its own free space.
    BinaryPrimitives.WriteUInt64LittleEndian(block[152..], spacemanOid);
    BinaryPrimitives.WriteUInt64LittleEndian(block[168..], reaperOid);

    // nx_omap_oid at 160.
    BinaryPrimitives.WriteUInt64LittleEndian(block[160..], ctrOmapOid);
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
      ulong fileCount, ulong dirCount, ulong nextObjId, string volumeName, ulong allocCount) {
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
    // apfs_fs_alloc_count (u64) at 88: the blocks this volume occupies, its own
    // superblock aside. Zero says a volume that takes up nothing, which is what
    // apfsprogs calls a "bad block count".
    BinaryPrimitives.WriteUInt64LittleEndian(block[88..], allocCount);
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
    Guid.NewGuid().ToByteArray(bigEndian: true).CopyTo(block[APSB_VOL_UUID..]);
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

    // apfs_next_doc_id, just past the name. A volume starts at the lowest
    // identifier it is allowed to name; zero says it would hand out one that is
    // reserved, which is what apfsprogs calls "next document id is invalid".
    BinaryPrimitives.WriteUInt32LittleEndian(block[APSB_NEXT_DOC_ID..], APFS_MIN_DOC_ID);

    ApfsFletcher64.Stamp(block);
  }

  // ── OMAP Phys ───────────────────────────────────────────────────────────

  private static void WriteOmapPhys(Span<byte> block, ulong oid, ulong xid, ulong treeBlock,
      bool manuallyManaged = false) {
    WriteObjectHeader(block, oid, xid, OBJECT_TYPE_OMAP | OBJ_PHYSICAL, subtype: 0);

    // om_flags (u32) at 32. A container's object map is manually managed —
    // OMAP_MANUALLY_MANAGED, which mkfs.apfs sets and apfsprogs insists on:
    // "Container object map: isn't manually managed". A volume's is not.
    BinaryPrimitives.WriteUInt32LittleEndian(block[32..], manuallyManaged ? 1u : 0u);
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

  private static void WriteCheckpointMap(Span<byte> block, ulong xid, ulong blkno,
      IReadOnlyList<(uint Type, uint Subtype, ulong Oid, ulong Paddr)> mappings) {
    // A checkpoint map is a physical object, and a physical object's identifier
    // is the block it occupies — the same rule the rest of this writer follows.
    // It used to be given the transaction id instead, which fsck.apfs reports as
    // "Checkpoint map: wrong object id".
    WriteObjectHeader(block, oid: blkno, xid: xid,
      type: OBJECT_TYPE_CHECKPOINT_MAP | OBJ_PHYSICAL, subtype: 0);
    // cpm_flags (u32) at 32.
    BinaryPrimitives.WriteUInt32LittleEndian(block[32..], 0x00000001); // CHECKPOINT_MAP_LAST
    // cpm_count (u32) at 36 — how many ephemeral objects this checkpoint carries.
    BinaryPrimitives.WriteUInt32LittleEndian(block[36..], (uint)mappings.Count);

    // cpm_map[] from 40: forty bytes each.
    //   cpm_type(4) cpm_subtype(4) cpm_size(4) cpm_pad(4) cpm_fs_oid(8)
    //   cpm_oid(8) cpm_paddr(8)
    for (var i = 0; i < mappings.Count; ++i) {
      var at = 40 + i * 40;
      var (type, subtype, oid, paddr) = mappings[i];
      BinaryPrimitives.WriteUInt32LittleEndian(block[at..], type);
      BinaryPrimitives.WriteUInt32LittleEndian(block[(at + 4)..], subtype);
      BinaryPrimitives.WriteUInt32LittleEndian(block[(at + 8)..], BlockSize);
      BinaryPrimitives.WriteUInt64LittleEndian(block[(at + 24)..], oid);
      BinaryPrimitives.WriteUInt64LittleEndian(block[(at + 32)..], paddr);
    }

    ApfsFletcher64.Stamp(block);
  }

  /// <summary>
  /// How many blocks a container of this size reserves for ephemeral state, as
  /// <c>nx_ephemeral_info[0]</c> reports it.
  /// </summary>
  /// <remarks>
  /// Read off containers built by <c>mkfs.apfs</c>, which is the only account of
  /// this there is: 1 024 to 4 096 blocks give 1, up to 13 312 give 3, then 4, 5,
  /// 6 and 7 in turn, and everything from 32 768 blocks upward gives 8. apfsprogs
  /// works the same number out for itself and compares, so a value that is close
  /// is no better than one that is absent.
  /// </remarks>
  private static ulong EphemeralMinBlockCount(long totalBlocks) => totalBlocks switch {
    <= 4_096 => 1,
    <= 13_312 => 3,
    <= 17_408 => 4,
    <= 22_528 => 5,
    <= 26_624 => 6,
    <= 30_720 => 7,
    _ => 8,
  };

  /// <summary>Blocks one chunk of the allocation bitmap covers.</summary>
  private const int BlocksPerChunk = 32768;          // one 4 KB bitmap block's worth of bits

  /// <summary>Chunks a single chunk-info block can name.</summary>
  private const int ChunksPerCib = 126;

  /// <summary>Blocks the internal pool's own bitmap ring occupies.</summary>
  private const int IpBitmapRingBlocks = 16;

  /// <summary>
  /// Blocks the internal pool takes for a container of this many chunks.
  /// </summary>
  /// <remarks>
  /// Three per chunk and three more, which is what mkfs.apfs reserves at every
  /// size measured against it — one chunk gives six, four give fifteen, thirty-two
  /// give ninety-nine. A pool sized to just the bitmaps and the chunk-info block
  /// is what apfsprogs calls a "wrong size of internal pool".
  /// </remarks>
  private static int InternalPoolBlocks(int chunkCount) => 3 * chunkCount + 3;

  /// <summary>
  /// How many nodes the main free queue may grow to.
  /// </summary>
  /// <remarks>
  /// <para>Measured against mkfs.apfs rather than derived: a container of N
  /// blocks gives N/4096 less 3N/131072, which holds at every size from 16 384
  /// blocks up to the 262 144 where its behaviour changes. This writer's
  /// containers sit inside that range — the floor is 131 072 blocks, where the
  /// value is 29 and apfsprogs accepts nothing else.</para>
  ///
  /// <para>Above 262 144 blocks mkfs follows a rule this has not been able to
  /// pin down, and guessing at one would put a number here that only looked
  /// right. What is written there is this same formula, and it is the one thing
  /// about a container of that size that a checker would still object to.</para>
  /// </remarks>
  private static ushort MainFreeQueueNodeLimit(long totalBlocks) {
    var limit = totalBlocks / 4096 - 3 * totalBlocks / 131072;
    return (ushort)Math.Clamp(limit, 1, ushort.MaxValue);
  }

  /// <summary>
  /// The reaper: what a container uses to finish deleting objects too large to
  /// remove in one transaction. A fresh volume has nothing to reap.
  /// </summary>
  private static void WriteReaper(Span<byte> block, ulong oid, ulong xid) {
    WriteObjectHeader(block, oid, xid, OBJECT_TYPE_NX_REAPER | OBJ_EPHEMERAL, subtype: 0);
    BinaryPrimitives.WriteUInt64LittleEndian(block[32..], 1);   // nr_next_reap_id
    BinaryPrimitives.WriteUInt64LittleEndian(block[40..], 0);   // nr_completed_id
    BinaryPrimitives.WriteUInt32LittleEndian(block[64..], 1);   // nr_flags: BHM
    // nr_state_buffer_size at 108: whatever is left of the block after the
    // fixed part, which is where a reap in progress would keep its state.
    BinaryPrimitives.WriteUInt32LittleEndian(block[108..], (uint)(block.Length - 112));
    ApfsFletcher64.Stamp(block);
  }

  /// <summary>
  /// One bitmap block per chunk, saying which blocks of that chunk are taken.
  /// </summary>
  /// <remarks>
  /// This writer hands blocks out in order and never frees one, so everything
  /// below <paramref name="usedBlocks" /> is allocated and everything above it is
  /// free. A checker compares this against what the volume's structures actually
  /// occupy, so it is not decoration.
  /// </remarks>
  private static void WriteChunkBitmaps(byte[] disk, long ipBase, int chunkCount,
      long usedBlocks, long totalBlocks) {
    for (var chunk = 0; chunk < chunkCount; ++chunk) {
      var bitmap = BlockOf(disk, ipBase + chunk);
      var first = (long)chunk * BlocksPerChunk;
      var count = (int)Math.Min(BlocksPerChunk, totalBlocks - first);
      for (var i = 0; i < count; ++i) {
        if (first + i >= usedBlocks) break;
        bitmap[i >> 3] |= (byte)(1 << (i & 7));
      }
    }
  }

  /// <summary>The block naming every chunk's bitmap, and how full each chunk is.</summary>
  private static void WriteChunkInfoBlock(byte[] disk, long cibBlock, long ipBase, int chunkCount,
      long usedBlocks, long totalBlocks, ulong xid) {
    var block = BlockOf(disk, cibBlock);
    WriteObjectHeader(block, (ulong)cibBlock, xid, OBJECT_TYPE_SPACEMAN_CIB | OBJ_PHYSICAL, subtype: 0);
    BinaryPrimitives.WriteUInt32LittleEndian(block[32..], 0);                  // cib_index
    BinaryPrimitives.WriteUInt32LittleEndian(block[36..], (uint)chunkCount);   // cib_chunk_info_count

    for (var chunk = 0; chunk < chunkCount; ++chunk) {
      var at = 40 + chunk * 32;
      var first = (long)chunk * BlocksPerChunk;
      var count = Math.Min(BlocksPerChunk, totalBlocks - first);
      var used = Math.Clamp(usedBlocks - first, 0, count);

      BinaryPrimitives.WriteUInt64LittleEndian(block[at..], xid);                        // ci_xid
      BinaryPrimitives.WriteUInt64LittleEndian(block[(at + 8)..], (ulong)first);          // ci_addr
      BinaryPrimitives.WriteUInt32LittleEndian(block[(at + 16)..], (uint)count);          // ci_block_count
      BinaryPrimitives.WriteUInt32LittleEndian(block[(at + 20)..], (uint)(count - used)); // ci_free_count
      BinaryPrimitives.WriteUInt64LittleEndian(block[(at + 24)..], (ulong)(ipBase + chunk)); // ci_bitmap_addr
    }
    ApfsFletcher64.Stamp(block);
  }

  /// <summary>The internal pool's own bitmap: which of its blocks are in use.</summary>
  /// <remarks>
  /// Only the ones actually occupied — a bitmap per chunk and the chunk-info
  /// block after them. Marking the whole pool says every block of it is spoken
  /// for, which leaves the allocator no room in the place it keeps for itself.
  /// </remarks>
  private static void WriteInternalPoolBitmap(byte[] disk, long ipBmBase, int chunkCount) {
    var bitmap = BlockOf(disk, ipBmBase);
    var inUse = chunkCount + 1;
    for (var i = 0; i < inUse; ++i)
      bitmap[i >> 3] |= (byte)(1 << (i & 7));
  }

  /// <summary>
  /// The space manager: what the container knows about where its free blocks are.
  /// </summary>
  private static void WriteSpaceman(Span<byte> block, ulong oid, ulong xid,
      long totalBlocks, long usedBlocks, int chunkCount, long cibBlock,
      long ipBmBase, long ipBase, int ipBlockCount, ulong freeQueueOid) {
    WriteObjectHeader(block, oid, xid, OBJECT_TYPE_SPACEMAN | OBJ_EPHEMERAL, subtype: 0);

    BinaryPrimitives.WriteUInt32LittleEndian(block[32..], BlockSize);        // sm_block_size
    BinaryPrimitives.WriteUInt32LittleEndian(block[36..], BlocksPerChunk);   // sm_blocks_per_chunk
    BinaryPrimitives.WriteUInt32LittleEndian(block[40..], ChunksPerCib);     // sm_chunks_per_cib
    BinaryPrimitives.WriteUInt32LittleEndian(block[44..], 507);              // sm_cibs_per_cab

    // sm_dev[main] at 48: block_count, chunk_count, cib_count, cab_count,
    // free_count, addr_offset, then two reserved words.
    const int addrOffset = 384;   // where the array of chunk-info block addresses sits
    BinaryPrimitives.WriteUInt64LittleEndian(block[48..], (ulong)totalBlocks);
    BinaryPrimitives.WriteUInt64LittleEndian(block[56..], (ulong)chunkCount);
    BinaryPrimitives.WriteUInt32LittleEndian(block[64..], 1);                        // one CIB
    BinaryPrimitives.WriteUInt32LittleEndian(block[68..], 0);                        // no CABs
    BinaryPrimitives.WriteUInt64LittleEndian(block[72..], (ulong)(totalBlocks - usedBlocks));
    BinaryPrimitives.WriteUInt32LittleEndian(block[80..], addrOffset);
    // sm_dev[tier2] at 96 stays zero: there is no second device.

    BinaryPrimitives.WriteUInt32LittleEndian(block[144..], 0);                       // sm_flags
    BinaryPrimitives.WriteUInt32LittleEndian(block[148..], IpBitmapRingBlocks);      // sm_ip_bm_tx_multiplier
    BinaryPrimitives.WriteUInt64LittleEndian(block[152..], (ulong)ipBlockCount);     // sm_ip_block_count
    BinaryPrimitives.WriteUInt32LittleEndian(block[160..], 1);                       // sm_ip_bm_size_in_blocks
    BinaryPrimitives.WriteUInt32LittleEndian(block[164..], IpBitmapRingBlocks);      // sm_ip_bm_block_count
    BinaryPrimitives.WriteUInt64LittleEndian(block[168..], (ulong)ipBmBase);         // sm_ip_bm_base
    BinaryPrimitives.WriteUInt64LittleEndian(block[176..], (ulong)ipBase);           // sm_ip_base

    // sm_fq[]: two free queues, each an empty tree. Nothing has been freed yet.
    // The node limit is how many nodes a queue may grow to before the allocator
    // must drain it; the internal pool's queue is always allowed one.
    for (var i = 0; i < 2; ++i) {
      var at = 200 + i * 40;
      BinaryPrimitives.WriteUInt64LittleEndian(block[(at + 8)..], freeQueueOid + (ulong)i);
      BinaryPrimitives.WriteUInt16LittleEndian(block[(at + 24)..],
        (ushort)(i == 0 ? 1 : MainFreeQueueNodeLimit(totalBlocks)));
    }

    BinaryPrimitives.WriteUInt16LittleEndian(block[320..], 1);                       // sm_ip_bm_free_head
    BinaryPrimitives.WriteUInt16LittleEndian(block[322..], (ushort)(IpBitmapRingBlocks - 1));
    BinaryPrimitives.WriteUInt32LittleEndian(block[324..], 336);                     // sm_ip_bm_xid_offset
    BinaryPrimitives.WriteUInt32LittleEndian(block[328..], 344);                     // sm_ip_bitmap_offset
    BinaryPrimitives.WriteUInt32LittleEndian(block[332..], 352);                     // sm_ip_bm_free_next_offset
    BinaryPrimitives.WriteUInt32LittleEndian(block[336..], 1);                       // sm_version

    // The pool's bitmap ring is a linked free list, and the links live inside
    // this block: slot 0 is the one in use, 1 through 14 each point at the next,
    // and 15 ends it. Leaving them zero makes every slot point at slot zero,
    // which apfsprogs reports as a list that loops.
    BinaryPrimitives.WriteUInt64LittleEndian(block[336..], xid);          // the ring's xid
    const int freeNextOffset = 352;
    for (var i = 0; i < IpBitmapRingBlocks; ++i) {
      var next = i == 0 || i == IpBitmapRingBlocks - 1
        ? (ushort)0xFFFF                                                 // in use, and the end
        : (ushort)(i + 1);
      BinaryPrimitives.WriteUInt16LittleEndian(block[(freeNextOffset + i * 2)..], next);
    }

    BinaryPrimitives.WriteUInt64LittleEndian(block[addrOffset..], (ulong)cibBlock);
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

  /// <summary>Table slots a variable-length node reserves however few it holds.</summary>
  private const int MinimumTableSlots = 16;
  /// <summary>A toc entry when keys and values are fixed-length: two offsets, no lengths.</summary>
  private const int FixedTocEntrySize = 4;

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
      ulong oid, uint type, ulong xid, uint subtype = 0, bool fixedKv = false,
      int leafKeySize = 0, int leafValueSize = 0, uint btreeFlags = 0)
    => WriteBtreeNode(block, records, oid, type, xid,
         flags: (ushort)(BTNODE_ROOT | BTNODE_LEAF), level: 0,
         isRoot: true, nodeCount: 1, subtype: subtype, fixedKv: fixedKv,
         leafKeySize: leafKeySize, leafValueSize: leafValueSize, btreeFlags: btreeFlags);

  /// <summary>
  /// Writes a non-root leaf node (level 0): a child of an internal index node.
  /// Holds variable-length key/value records and carries no btree_info footer.
  /// </summary>
  private static void WriteBtreeLeafNode(Span<byte> block, IReadOnlyList<BtreeRecord> records,
      ulong oid, uint type, ulong xid, uint subtype = 0, bool fixedKv = false)
    // A node below the root is a btree_node, not a btree: the root's type names
    // the tree and every other node names itself. Giving them all the root's type
    // is what apfsprogs reports as the "wrong object type for nonroot".
    => WriteBtreeNode(block, records, oid,
         (type & ~(uint)0xFFFF) | OBJECT_TYPE_BTREE_NODE, xid,
         flags: BTNODE_LEAF, level: 0, isRoot: false, nodeCount: 0, subtype: subtype,
         fixedKv: fixedKv);

  /// <summary>
  /// Writes the internal root node (level 1) of a 2-level FS-tree. Its records map
  /// each child leaf's first key to that leaf's physical block address (an 8-byte
  /// oid_t value). The btree_info footer reports the whole tree's key/node counts.
  /// </summary>
  private static void WriteBtreeRootInternal(Span<byte> block,
      IReadOnlyList<byte[]> childFirstKeys, IReadOnlyList<ulong> childAddrs,
      IReadOnlyList<BtreeRecord> allRecords, ulong oid, uint type, ulong xid, uint subtype = 0,
      bool fixedKv = false) {
    // What the leaves hold, for the footer — not what this index node holds.
    var leafKeySize = 0;
    var leafValueSize = 0;
    foreach (var r in allRecords) {
      if (r.Key.Length > leafKeySize) leafKeySize = r.Key.Length;
      if (r.Value.Length > leafValueSize) leafValueSize = r.Value.Length;
    }
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
      nodeCount: totalNodes, keyCountOverride: totalKeys, subtype: subtype, fixedKv: fixedKv,
      leafKeySize: leafKeySize, leafValueSize: leafValueSize);
  }

  /// <summary>
  /// Shared B-tree node serializer. Writes the object header, btn_phys header,
  /// table of contents, the key area (growing forward) and the value area
  /// (growing backward), and — for root nodes — the trailing btree_info footer.
  /// Finishes by stamping the Fletcher-64 checksum.
  /// </summary>
  private static void WriteBtreeNode(Span<byte> block, IReadOnlyList<BtreeRecord> records,
      ulong oid, uint type, ulong xid, ushort flags, ushort level, bool isRoot,
      ulong nodeCount, ulong? keyCountOverride = null, uint subtype = 0, bool fixedKv = false,
      int leafKeySize = 0, int leafValueSize = 0, uint btreeFlags = 0) {
    // A node's own type says it is a B-tree; which tree it belongs to is the
    // subtype. Both used to go in the type field, leaving the subtype zero,
    // which apfsprogs reports as "Object map node: wrong object subtype".
    WriteObjectHeader(block, oid, xid, type, subtype);

    // btn_phys layout at offset 32:
    //   btn_flags (u16) at 32
    //   btn_level (u16) at 34
    //   btn_nkeys (u32) at 36
    //   btn_table_space (nloc_t: off u16 + len u16) at 40
    //   btn_free_space (nloc_t) at 44
    //   btn_key_free_list (nloc_t) at 48
    //   btn_val_free_list (nloc_t) at 52
    //   data[] starts at 56
    // An object map's keys and values are both fixed-length, and a node holding
    // them says so: its table of contents is then a pair of offsets per record
    // rather than an offset and a length for each half. Writing the
    // variable-length form for one is what apfsprogs calls "B-tree node: block
    // 0x12 is not sane".
    var tocEntrySize = fixedKv ? FixedTocEntrySize : TocEntrySize;
    if (fixedKv) flags |= BTNODE_FIXED_KV_SIZE;

    BinaryPrimitives.WriteUInt16LittleEndian(block[32..], flags);
    BinaryPrimitives.WriteUInt16LittleEndian(block[34..], level);
    BinaryPrimitives.WriteUInt32LittleEndian(block[36..], (uint)records.Count);

    var tocOff = BtnHeaderEnd;
    // A fixed-size tree reserves table space for as many records as the node
    // could hold, not for the ones it holds today — that is what leaves room to
    // add one without moving the keys. Reserving only what is in use is the last
    // thing that made apfsprogs call the node insane.
    var tocLen = records.Count * tocEntrySize;
    if (fixedKv) {
      // The node's own records decide how many would fit in it — an index node's
      // values are child identifiers and smaller than what the leaves hold, so
      // it has room for more of them. The stated leaf sizes stand in only for a
      // node with nothing in it to measure.
      var keySize = 0;
      var valSize = 0;
      foreach (var r in records) {
        if (r.Key.Length > keySize) keySize = r.Key.Length;
        if (r.Value.Length > valSize) valSize = r.Value.Length;
      }
      if (records.Count == 0) { keySize = leafKeySize; valSize = leafValueSize; }
      var perRecord = tocEntrySize + keySize + valSize;
      if (perRecord > tocEntrySize)
        tocLen = Math.Max(tocLen, (block.Length - BtnHeaderEnd) / perRecord * tocEntrySize);
    } else {
      // A tree whose records vary cannot say how many would fit, so it reserves a
      // fixed minimum instead — sixteen slots, which is what mkfs.apfs leaves on
      // every variable tree it writes, empty ones included. Reserving nothing at
      // all is what apfsprogs calls a node that "is not sane".
      tocLen = Math.Max(tocLen, MinimumTableSlots * tocEntrySize);
    }
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

      var entryOff = tocOff + i * tocEntrySize;
      if (fixedKv) {
        BinaryPrimitives.WriteUInt16LittleEndian(block[entryOff..], keyRelOff);
        BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 2)..], valRelOff);
      } else {
        BinaryPrimitives.WriteUInt16LittleEndian(block[entryOff..], keyRelOff);
        BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 2)..], (ushort)rec.Key.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 4)..], valRelOff);
        BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 6)..], (ushort)rec.Value.Length);
      }
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
      var longestKey = 0;
      var longestVal = 0;
      foreach (var r in records) {
        if (r.Key.Length > longestKey) longestKey = r.Key.Length;
        if (r.Value.Length > longestVal) longestVal = r.Value.Length;
      }

      // The footer has to agree with the node's own encoding: a tree whose
      // records are fixed-length says BTREE_FIXED_KV_SIZE and states both sizes,
      // where a variable one leaves them zero. Saying zero for a fixed tree left
      // the two halves of the same node describing different things.
      //
      // The sizes it states are the *leaves'*. An internal node's own values are
      // child block numbers, eight bytes each, and taking the sizes from those
      // told every reader that the records at the bottom of the tree were eight
      // bytes wide as well.
      var footerKeySize = leafKeySize > 0 ? leafKeySize : longestKey;
      var footerValSize = leafValueSize > 0 ? leafValueSize : longestVal;
      // The longest key and value in the tree, not in this node: an index node's
      // own values are child addresses, and an empty fixed tree has nothing to
      // measure at all. Both cases reported a maximum smaller than what the
      // leaves actually hold.
      longestKey = Math.Max(longestKey, leafKeySize);
      longestVal = Math.Max(longestVal, leafValueSize);
      // Where the nodes live, not how the records are shaped: a physical tree
      // says so here and an ephemeral one says that instead. What makes a tree
      // fixed is the two sizes below being set at all.
      // OBJ_VIRTUAL is zero, so the storage class is read from the two bits that
      // are not: ephemeral, physical, or neither — which is virtual.
      var physical = (type & OBJ_EPHEMERAL) != 0 ? BTREE_EPHEMERAL
        : (type & OBJ_PHYSICAL) != 0 ? BTREE_PHYSICAL : 0u;
      // A variable-length tree also says its records are not padded, which every
      // one in a reference container does.
      var shape = fixedKv ? 0u : BTREE_KV_NONALIGNED;
      BinaryPrimitives.WriteUInt32LittleEndian(block[infoOff..],
        physical | shape | btreeFlags);                                                 // bt_flags
      BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 4)..], (uint)block.Length); // bt_node_size
      BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 8)..],
        fixedKv ? (uint)footerKeySize : 0u);                                              // bt_key_size
      BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 12)..],
        fixedKv ? (uint)footerValSize : 0u);                                              // bt_val_size
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
      // The root and the private directory are the format's own, and a volume's
      // count is of the directories in it rather than the ones it is made of: a
      // container mkfs.apfs builds reports none at all. Counting the root here
      // made the number one larger than anything checking it will arrive at.
      DirectoryCount = dirCount,
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

    // Both the root and the private directory are named by an entry under inode
    // 1, which is the parent the format gives them: "root" and "private-dir". A
    // container mkfs.apfs builds carries exactly those two, and ours carried
    // neither — two directories nothing pointed at.
    list.Add((APFS_ROOT_DIR_PARENT, APFS_TYPE_DIR_REC, "root",
      BuildDrecKey(APFS_ROOT_DIR_PARENT, "root"),
      BuildDrecValue(APFS_ROOT_DIR_INO_NUM, isDir: true)));
    list.Add((APFS_ROOT_DIR_PARENT, APFS_TYPE_DIR_REC, "private-dir",
      BuildDrecKey(APFS_ROOT_DIR_PARENT, "private-dir"),
      BuildDrecValue(APFS_PRIV_DIR_INO_NUM, isDir: true)));

    // Root directory inode — nchildren = number of entries directly under it.
    var rootChildren = tree.Nodes.Count(n => n.ParentIno == APFS_ROOT_DIR_INO_NUM);
    list.Add((APFS_ROOT_DIR_INO_NUM, APFS_TYPE_INODE, string.Empty,
      BuildInodeKey(APFS_ROOT_DIR_INO_NUM),
      BuildInodeValue(APFS_ROOT_DIR_INO_NUM, parentId: APFS_ROOT_DIR_PARENT,
        size: 0, isDir: true, nchildren: (uint)rootChildren, name: "root")));

    // The private directory. A mount reads this inode before it reads the root and
    // refuses the volume when it is not there — it is where a file goes that is
    // still open after its last name is gone. Nothing in the root links to it, so
    // it is an inode record and nothing else.
    list.Add((APFS_PRIV_DIR_INO_NUM, APFS_TYPE_INODE, string.Empty,
      BuildInodeKey(APFS_PRIV_DIR_INO_NUM),
      BuildInodeValue(APFS_PRIV_DIR_INO_NUM, parentId: APFS_ROOT_DIR_PARENT,
        size: 0, isDir: true, nchildren: 0, internalFlags: APFS_INODE_IS_APFS_PRIVATE,
        name: "private-dir")));

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
          isDir: node.IsDir, nchildren: node.IsDir ? (uint)node.ChildCount : 1u,
          name: node.Name)));

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
      ulong internalFlags = 0, string name = "")
    => ApfsInodeRecord.BuildValue(ino, parentId, size, isDir, nchildren, internalFlags, name);


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

  /// <summary>
  /// One record per file's data, saying which blocks it occupies and who owns
  /// them.
  /// </summary>
  /// <remarks>
  /// This is the extent-reference tree, and it is how the volume knows a run of
  /// blocks is spoken for and by how many files. It used to be written empty,
  /// which leaves every file's extents accounted for nowhere — "Logical extent
  /// record: doesn't seem covered by any physical extent".
  /// </remarks>
  private static List<BtreeRecord> BuildExtentRefRecords(FsTree tree) {
    var records = new List<(ulong Paddr, BtreeRecord Record)>();
    foreach (var node in tree.Nodes) {
      if (node.IsDir || node.Payload is not { Size: > 0 } payload) continue;

      var blocks = (ulong)((payload.Size + BlockSize - 1) / BlockSize);

      // j_phys_ext_key_t: the first block, under the extent record type.
      var key = new byte[8];
      BinaryPrimitives.WriteUInt64LittleEndian(key,
        node.PhysBlock | ((ulong)APFS_TYPE_EXTENT << 60));

      // j_phys_ext_val_t: length and kind, the inode that owns it, and how many
      // files share it. The kind lives in the top four bits and a zero there —
      // "any" — is not something a record may say it is; a run written for the
      // first time is new.
      const ulong kindNew = 1UL << 60;
      var value = new byte[20];
      BinaryPrimitives.WriteUInt64LittleEndian(value,
        (blocks & 0x0FFFFFFFFFFFFFFFUL) | kindNew);
      BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), node.Ino);
      BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(16), 1);

      records.Add((node.PhysBlock, new BtreeRecord(key, value)));
    }

    // A B-tree's records are in key order, and a key here is the block number.
    records.Sort((a, b) => a.Paddr.CompareTo(b.Paddr));
    return records.Select(r => r.Record).ToList();
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
