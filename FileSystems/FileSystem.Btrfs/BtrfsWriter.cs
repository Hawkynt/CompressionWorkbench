#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Btrfs;

/// <summary>
/// Writes spec-compliant Btrfs filesystem images. Every image contains
/// a populated <c>sys_chunk_array</c> inside the superblock, a real chunk
/// tree with three <c>CHUNK_ITEM</c> entries (<c>SYSTEM</c>, <c>METADATA</c>,
/// <c>DATA</c>) that map every logical range used by the image to its
/// physical offset, a dev tree with one <c>DEV_ITEM</c> for the single
/// device, a root tree pointing at the FS tree, and an FS tree leaf
/// holding inode / directory-index / inline extent-data items for every
/// added file. All metadata blocks carry the 4-byte little-endian CRC-32C
/// (Castagnoli) at byte offset 0 per the on-disk spec.
/// </summary>
public sealed class BtrfsWriter {
  private const int SectorSize = 4096;
  private const int NodeSize = 16384;
  // BTRFS_STRIPE_LEN (fs/btrfs/volumes.h) — fixed 64 KiB stripe width used for
  // all chunk types. btrfs check rejects any other value.
  private const int StripeLen = 64 * 1024;

  // Physical layout (superblock at canonical 0x10000, trees at node-aligned
  // offsets). We deliberately keep logical == physical so the chunk tree's
  // mapping is trivially verifiable but still real — every read goes through
  // sys_chunk_array → chunk tree → LogicalToPhysical.
  //
  // Chunk placement: btrfs check's check_extent_type() uses the first tree
  // backref of each metadata extent to decide the expected block group type.
  // Only CHUNK_TREE-owned blocks may live in a SYSTEM chunk; every other
  // metadata tree (DEV, ROOT, FS, EXTENT, CSUM) MUST live in a METADATA
  // chunk. Our previous layout put DEV_TREE inside SYSTEM which triggered
  // "type mismatch with chunk" — hence the split below.
  private const int SbOffset = 0x10000;
  private const int ChunkTreeOff = 0x20000;   // SYSTEM chunk: chunk tree only
  private const int DevTreeOff = 0x30000;     // METADATA chunk starts here
  private const int RootTreeOff = 0x40000;
  private const int FsTreeOff = 0x50000;      // first FS-tree block (root node)

  // The metadata chunk holds dev/root/fs/extent/csum trees. The FS tree may
  // span an arbitrary number of node-sized blocks (one internal node + N leaf
  // nodes when a directory overflows a single leaf), so the extent/csum trees
  // and the data region are placed *after* the FS-tree blocks at offsets
  // computed per image in <see cref="ComputeLayout"/>. The fixed offsets above
  // cover the blocks whose position never depends on file count.

  // Chunk ranges (logical == physical in this image). Lengths are multiples
  // of BTRFS_STRIPE_LEN (64 KiB).
  //   SYSTEM:   [0x10000, 0x30000) 128 KiB — superblock gap + chunk tree
  //   METADATA: [0x30000, …)       — dev/root/fs(+grown leaves)/extent/csum
  //   DATA:     [end of metadata, …) — file data region
  private const long SystemChunkStart = 0x10000;
  private const long SystemChunkLength = 0x20000;  // 128 KiB (sb gap + chunk tree)
  private const long MetadataChunkStart = 0x30000;
  private const long DataChunkLength = 0x40000;    // 256 KiB data region

  // The dev/root trees plus the extent and csum trees and the data region are
  // sized at write time once the FS-tree block count is known. These fields are
  // populated by ComputeLayout before any tree is serialised.
  private int _fsTreeBlockCount = 1;            // FS-tree blocks (root + leaves)
  private long _extentTreeOff;
  private long _csumTreeOff;
  private long _metadataChunkLength;
  private long _dataChunkStart;
  private long _totalSize;

  // Logical/physical offsets of every FS-tree block, in write order. Index 0 is
  // the root node (an internal node when _fsTreeBlockCount > 2, otherwise the
  // sole leaf). Leaves follow when the tree has grown.
  private readonly List<long> _fsTreeBlockOffsets = [];

  // Key types — shared with the reader.
  private const byte InodeItem = 1;
  private const byte InodeRef = 12;
  private const byte DirItem = 84;
  private const byte DirIndex = 96;
  private const byte ExtentData = 108;
  private const byte RootItem = 132;
  private const byte ExtentItem = 168;
  private const byte MetadataItem = 169;
  private const byte TreeBlockRef = 176;
  private const byte ExtentDataRef = 178;
  private const byte BlockGroupItem = 192;
  private const byte DevExtent = 204;
  private const byte DevItem = 216;
  private const byte ChunkItem = 228;

  // Extent-item flags (fs/btrfs/ctree.h).
  private const ulong ExtentFlagData = 0x01;
  private const ulong ExtentFlagTreeBlock = 0x02;

  // Chunk type bitmask (fs/btrfs/ctree.h).
  private const ulong BlockGroupData = 0x01;
  private const ulong BlockGroupSystem = 0x02;
  private const ulong BlockGroupMetadata = 0x04;

  // Well-known object IDs (fs/btrfs/ctree.h BTRFS_*_TREE_OBJECTID).
  private const long RootTreeObjectId = 1;
  private const long ExtentTreeObjectId = 2;
  private const long ChunkTreeObjectId = 3;
  private const long DevTreeObjectId = 4;
  private const long FsTreeObjectId = 5;
  private const long CsumTreeObjectId = 7;
  private const long DevItemsObjectId = 1;
  private const long FirstChunkTreeObjectId = 256;
  private const long FirstFreeObjectId = 256;

  private static readonly byte[] Magic = "_BHRfS_M"u8.ToArray();
  private static readonly byte[] FsUuid = {
    0xb7, 0xe5, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x72, 0x74, 0x72, 0x66,
  };
  private static readonly byte[] DevUuid = {
    0xb7, 0xe5, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x64, 0x65, 0x76, 0x31,
  };

  private readonly List<(string name, byte[] data)> _files = [];

  /// <summary>Adds a file to the image. The <paramref name="name"/> may
  /// contain '/' (or '\\') separators; each path component becomes a real
  /// directory inode in the FS tree. File data becomes an inline
  /// <c>EXTENT_DATA</c> item in the FS tree leaf.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    // Normalise separators and drop empty components (leading "/", "a//b").
    var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
      throw new ArgumentException("File name must contain at least one path component.", nameof(name));
    for (var i = 0; i < parts.Length; i++)
      if (parts[i].Length > 255) parts[i] = parts[i][..255];
    this._files.Add((string.Join('/', parts), data));
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    // ── Phase 1: build and pack the FS tree ─────────────────────────────
    // The FS-tree item set is assembled and split into leaf-sized batches
    // first, because the resulting block count drives the physical layout
    // (where the extent/csum trees and the data region land, and how big the
    // image and the metadata chunk must be).
    var fsLeaves = BuildFsTreeLeaves();
    // FS-tree block count: one block when a single leaf is the root; otherwise
    // one internal root node plus one block per leaf.
    var fsTreeBlocks = fsLeaves.Count == 1 ? 1 : fsLeaves.Count + 1;
    ComputeLayout(fsTreeBlocks);

    var image = new byte[this._totalSize];

    WriteSuperblock(image);
    WriteChunkTree(image);
    WriteDevTree(image);
    WriteRootTree(image);
    WriteFsTree(image, fsLeaves);
    WriteExtentTree(image);
    WriteEmptyTree(image, (int)this._csumTreeOff, CsumTreeObjectId);

    // Every metadata block starts with a 32-byte csum field whose first
    // 4 bytes hold CRC-32C over bytes [32..blockSize).
    WriteBlockChecksum(image, SbOffset, SectorSize);
    WriteBlockChecksum(image, ChunkTreeOff, NodeSize);
    WriteBlockChecksum(image, DevTreeOff, NodeSize);
    WriteBlockChecksum(image, RootTreeOff, NodeSize);
    foreach (var off in this._fsTreeBlockOffsets)
      WriteBlockChecksum(image, (int)off, NodeSize);
    WriteBlockChecksum(image, (int)this._extentTreeOff, NodeSize);
    WriteBlockChecksum(image, (int)this._csumTreeOff, NodeSize);

    output.Write(image);
  }

  // Decides the physical placement of every block whose offset depends on the
  // FS-tree block count, then records the chunk/image sizes those placements
  // imply. Logical == physical throughout, so these offsets double as logical
  // addresses in the chunk map.
  private void ComputeLayout(int fsTreeBlockCount) {
    this._fsTreeBlockCount = fsTreeBlockCount;

    this._fsTreeBlockOffsets.Clear();
    for (var i = 0; i < fsTreeBlockCount; i++)
      this._fsTreeBlockOffsets.Add(FsTreeOff + (long)i * NodeSize);

    var afterFsTree = FsTreeOff + (long)fsTreeBlockCount * NodeSize;
    this._extentTreeOff = afterFsTree;
    this._csumTreeOff = afterFsTree + NodeSize;

    // Metadata chunk spans dev/root trees through the csum tree. Round its
    // length up to a whole BTRFS_STRIPE_LEN (64 KiB) so the chunk mapping
    // stays stripe-aligned like the original fixed layout.
    var metadataEnd = this._csumTreeOff + NodeSize;
    var metadataRawLength = metadataEnd - MetadataChunkStart;
    this._metadataChunkLength = RoundUpToStripe(metadataRawLength);

    this._dataChunkStart = MetadataChunkStart + this._metadataChunkLength;
    this._totalSize = this._dataChunkStart + DataChunkLength;
  }

  private static long RoundUpToStripe(long length) {
    var rem = length % StripeLen;
    return rem == 0 ? length : length + (StripeLen - rem);
  }

  // Number of metadata tree blocks the extent tree must account for:
  // chunk + dev + root + every FS-tree block + extent + csum.
  private int MetadataBlockCount => 4 + this._fsTreeBlockCount;

  // Builds an empty leaf block (no items). Used for CSUM_TREE where the
  // only requirement is a valid node header — no CSUM_ITEM entries needed
  // since we have no data blocks that require csum coverage in this
  // minimal image.
  private static void WriteEmptyTree(byte[] image, int nodeOff, long ownerObjectId) {
    WriteLeafNode(image, nodeOff, ownerObjectId,
      new List<(long, byte, long, byte[])>());
  }

  // ── Extent tree ──────────────────────────────────────────────────────
  //
  // The extent tree accounts for every allocated block in the FS. btrfs
  // check walks it and cross-references ownership against each tree block.
  // For this minimal image we emit:
  //   - BLOCK_GROUP_ITEM per chunk (SYSTEM, METADATA, DATA)
  //   - EXTENT_ITEM with inline TREE_BLOCK_REF per metadata tree block
  // Per-extent backrefs point at the owning root (e.g. root_tree=1).
  private void WriteExtentTree(byte[] image) {
    var items = new List<(long objId, byte type, long offset, byte[] data)>();

    // Tree block extent items — one per metadata node.
    AddTreeBlockExtent(items, ChunkTreeOff, ChunkTreeObjectId);
    AddTreeBlockExtent(items, DevTreeOff,   DevTreeObjectId);
    AddTreeBlockExtent(items, RootTreeOff,  RootTreeObjectId);
    // Every FS-tree block — the root node (internal when the tree has grown)
    // and each leaf — is an owned metadata extent of FS_TREE.
    foreach (var off in this._fsTreeBlockOffsets)
      AddTreeBlockExtent(items, off, FsTreeObjectId);
    AddTreeBlockExtent(items, this._extentTreeOff, ExtentTreeObjectId);
    AddTreeBlockExtent(items, this._csumTreeOff,  CsumTreeObjectId);

    // Block groups — one per chunk. Used bytes must match what
    // update_block_group_used() accumulates from the extent items that
    // actually live inside each chunk. chunk_objectid is the FIRST
    // chunk-tree objectid (256 = FIRST_CHUNK_TREE), NOT the chunk-tree
    // root objectid (3). mkfs.btrfs always stores 256 here.
    items.Add((SystemChunkStart, BlockGroupItem, SystemChunkLength,
      BuildBlockGroupItem(used: CountMetadataUseInRange(SystemChunkStart, SystemChunkLength), FirstChunkTreeObjectId, BlockGroupSystem)));
    items.Add((MetadataChunkStart, BlockGroupItem, this._metadataChunkLength,
      BuildBlockGroupItem(used: CountMetadataUseInRange(MetadataChunkStart, this._metadataChunkLength), FirstChunkTreeObjectId, BlockGroupMetadata)));
    items.Add((this._dataChunkStart, BlockGroupItem, DataChunkLength,
      BuildBlockGroupItem(used: 0, FirstChunkTreeObjectId, BlockGroupData)));

    SortLeafItems(items);
    WriteLeafNode(image, (int)this._extentTreeOff, ExtentTreeObjectId, items);
  }

  private static void AddTreeBlockExtent(List<(long, byte, long, byte[])> items,
      long bytenr, long ownerRoot) {
    // EXTENT_ITEM value layout for a tree block:
    //   btrfs_extent_item (24): refs(8) + generation(8) + flags(8)
    //   btrfs_tree_block_info (18): disk_key(17) + level(1)
    //   inline TREE_BLOCK_REF (9): type(1) + offset(8)
    // Total: 51 bytes.
    var v = new byte[24 + 18 + 9];
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0), 1);                                // refs
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(8), 1);                                // generation
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(16), ExtentFlagTreeBlock);            // flags
    // tree_block_info.key (17 bytes at offset 24) = lowest key in the block.
    // Zeroing is tolerated by btrfs check for structural validation.
    v[24 + 17] = 0;  // level — leaf
    // Inline TREE_BLOCK_REF (type=176, offset=owning root objectid).
    v[24 + 18] = TreeBlockRef;
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(24 + 18 + 1), ownerRoot);
    items.Add((bytenr, ExtentItem, NodeSize, v));
  }

  private static byte[] BuildBlockGroupItem(long used, long chunkObjectId, ulong flags) {
    var v = new byte[24];
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0), used);
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(8), chunkObjectId);
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(16), flags);
    return v;
  }

  // Sum the sizes of every metadata tree block whose bytenr falls within the
  // given chunk range. btrfs check's update_block_group_used() pre-seeds
  // rec->actual_used from the extents it walks; when the tool later compares
  // that against rec->used (what we stamp here), they must match.
  private long CountMetadataUseInRange(long chunkStart, long chunkLength) {
    var chunkEnd = chunkStart + chunkLength;
    long used = 0;
    var nodes = new List<long> { ChunkTreeOff, DevTreeOff, RootTreeOff, this._extentTreeOff, this._csumTreeOff };
    nodes.AddRange(this._fsTreeBlockOffsets);
    foreach (var n in nodes)
      if (n >= chunkStart && n < chunkEnd)
        used += NodeSize;
    return used;
  }

  // ── Superblock ────────────────────────────────────────────────────────
  //
  // Canonical Btrfs superblock layout per fs/btrfs/ctree.h btrfs_super_block.
  // Earlier revisions of this writer also duplicated sys_chunk_array_size
  // into the sbOffset+196 / sbOffset+299 "pragmatic" offsets matching an
  // earlier reader; that path collided with the spec csum_type field (u16 @
  // 0xC4) and produced csum_type=97 (= array length 17+48+32) which
  // btrfs check rejects as "unsupported checksum algorithm 97". The reader
  // now reads the spec offsets (0xA0 / 0x32B) instead, and the pragmatic
  // duplicates have been removed.
  private void WriteSuperblock(byte[] image) {
    var sb = image.AsSpan(SbOffset);

    // Canonical spec layout ────────────────────────────────────────────
    // 0x00  csum[32]                  — stamped in WriteBlockChecksum
    // 0x20  fsid[16]
    // 0x30  bytenr (self)
    // 0x38  flags
    // 0x40  magic[8]
    // 0x48  generation
    // 0x50  root (fs tree-of-roots)
    // 0x58  chunk_root
    // 0x60  log_root
    // 0x68  log_root_transid
    // 0x70  total_bytes
    // 0x78  bytes_used
    // 0x80  root_dir_objectid
    // 0x88  num_devices
    // 0x90  sectorsize
    // 0x94  nodesize
    // 0x98  leafsize (== nodesize)
    // 0x9C  stripesize
    // 0xA0  sys_chunk_array_size
    // 0xA4  chunk_root_generation
    // 0xAC  compat_flags
    // 0xB4  compat_ro_flags
    // 0xBC  incompat_flags
    // 0xC4  csum_type (u16)           — 0 = CRC-32C
    // 0xC6  root_level
    // 0xC7  chunk_root_level
    // 0xC8  log_root_level
    // 0xC9  dev_item (98 bytes)
    // 0x12B label[256]
    // 0x22B cache_generation (u64)
    // 0x233 uuid_tree_generation (u64)
    // 0x23B metadata_uuid[16]
    // ...
    // 0x32B sys_chunk_array[2048]
    FsUuid.CopyTo(sb.Slice(0x20));
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x30), SbOffset);
    Magic.CopyTo(sb.Slice(0x40));
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x48), 1); // generation
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x50), RootTreeOff);
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x58), ChunkTreeOff);
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x70), this._totalSize);
    // bytes_used: actual bytes consumed by metadata + data. btrfs check
    // compares this against the sum of every allocated extent it walks;
    // we account every metadata tree block (chunk/dev/root, the FS-tree
    // root+leaves, extent, csum) and no file data extents (files live inline
    // in FS_TREE EXTENT_DATA items which do not contribute to bytes_used).
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x78), (long)this.MetadataBlockCount * NodeSize);
    // root_dir_objectid at 0x80 is the default directory inode inside the
    // default subvolume (fs tree root). Per fs/btrfs/ctree.h this is
    // BTRFS_FIRST_FREE_OBJECTID (256), NOT the fs-tree objectid.
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x80), FirstFreeObjectId);
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x88), 1); // num_devices
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x90), SectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x94), NodeSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x98), NodeSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x9C), SectorSize);
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0xA4), 1); // chunk_root_generation

    // csum_type at 0xC4 (u16 LE). 0 = CRC-32C (what our WriteBlockChecksum
    // emits). Must be explicitly zero because the bytes here were previously
    // overwritten with 97 by an obsolete duplicate array-size write.
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(0xC4), 0);

    // Embedded DEV_ITEM (98 bytes) for bootstrap.
    var devItem = BuildSuperblockDevItem();
    devItem.CopyTo(image, SbOffset + 0xC9);

    // Label at 0x12B (256 bytes, NUL-padded).
    var label = "CWB-BTRFS"u8;
    label.CopyTo(image.AsSpan(SbOffset + 0x12B));

    // sys_chunk_array at the canonical 0x32B offset. Size is declared at
    // 0xA0 per fs/btrfs/ctree.h.
    var sysArray = BuildSysChunkArray();
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0xA0), (uint)sysArray.Length);
    sysArray.CopyTo(image.AsSpan(SbOffset + 0x32B));
  }

  // Builds the 98-byte DEV_ITEM embedded in both the superblock and the
  // dev tree. Matches fs/btrfs/ctree.h btrfs_dev_item.
  private byte[] BuildSuperblockDevItem() {
    var d = new byte[98];
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(0), 1);              // devid
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(8), this._totalSize); // total_bytes
    // bytes_used must equal the sum of every DEV_EXTENT length that
    // references this device. btrfs check complains with
    // "Dev extent's total-byte(X) is not equal to byte-used(Y)" otherwise.
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(16),
      SystemChunkLength + this._metadataChunkLength + DataChunkLength);
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(24), SectorSize);   // io_align
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(28), SectorSize);   // io_width
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(32), SectorSize);   // sector_size
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(36), 0);             // type
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(44), 1);             // generation
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(52), 0);             // start_offset
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(60), 0);            // dev_group
    d[64] = 0; // seek_speed
    d[65] = 0; // bandwidth
    DevUuid.CopyTo(d.AsSpan(66));
    FsUuid.CopyTo(d.AsSpan(82));
    return d;
  }

  // ── sys_chunk_array ───────────────────────────────────────────────────
  //
  // Serialised (key, chunk_item) tuple for the SYSTEM chunk. The reader
  // parses this array to bootstrap logical→physical translation before
  // descending into the chunk tree. Layout must match BtrfsReader
  // ParseSysChunkArray exactly.
  //   key:         objectid(8) + type(1) + offset(8)                  = 17
  //   chunk_item:  length(8) + owner(8) + stripe_len(8) + type(8)
  //              + io_align(4) + io_width(4) + sector_size(4)
  //              + num_stripes(2) + sub_stripes(2)                    = 48
  //   stripe:      devid(8) + offset(8) + dev_uuid(16)                = 32
  private static byte[] BuildSysChunkArray() {
    var a = new byte[17 + 48 + 32];
    BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0), FirstChunkTreeObjectId);
    a[8] = ChunkItem;
    BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(9), SystemChunkStart);
    var c = 17;
    BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(c + 0), SystemChunkLength);
    BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(c + 8), ChunkTreeObjectId);
    BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(c + 16), StripeLen);        // stripe_len (BTRFS_STRIPE_LEN = 64 KiB)
    BinaryPrimitives.WriteUInt64LittleEndian(a.AsSpan(c + 24), BlockGroupSystem);
    BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(c + 32), SectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(c + 36), SectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(c + 40), SectorSize);
    BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(c + 44), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(c + 46), 0);
    var s = c + 48;
    BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(s + 0), 1);                 // devid
    BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(s + 8), SystemChunkStart);  // physical
    DevUuid.CopyTo(a.AsSpan(s + 16));
    return a;
  }

  // ── Chunk tree (three CHUNK_ITEMs) ───────────────────────────────────

  private void WriteChunkTree(byte[] image) {
    // Per mkfs.btrfs the CHUNK_TREE holds both DEV_ITEM entries (one per
    // device) and CHUNK_ITEM entries (one per chunk). The key order enforced
    // by SortLeafItems places DEV_ITEM (objId=DEV_ITEMS=1, type=216) before
    // CHUNK_ITEM (objId=FIRST_CHUNK_TREE=256, type=228).
    var items = new List<(long objId, byte type, long offset, byte[] data)> {
      (DevItemsObjectId, DevItem, 1, BuildSuperblockDevItem()),
      (FirstChunkTreeObjectId, ChunkItem, SystemChunkStart,
        BuildChunkItem(SystemChunkLength, BlockGroupSystem, SystemChunkStart)),
      (FirstChunkTreeObjectId, ChunkItem, MetadataChunkStart,
        BuildChunkItem(this._metadataChunkLength, BlockGroupMetadata, MetadataChunkStart)),
      (FirstChunkTreeObjectId, ChunkItem, this._dataChunkStart,
        BuildChunkItem(DataChunkLength, BlockGroupData, this._dataChunkStart)),
    };
    SortLeafItems(items);
    WriteLeafNode(image, ChunkTreeOff, ChunkTreeObjectId, items);
  }

  // 80-byte chunk value = 48-byte chunk_item header + 32-byte stripe.
  private static byte[] BuildChunkItem(long length, ulong type, long physicalStart) {
    var data = new byte[48 + 32];
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(0), length);
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), RootTreeObjectId);
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), StripeLen);         // stripe_len (BTRFS_STRIPE_LEN = 64 KiB)
    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(24), type);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), SectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), SectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), SectorSize);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(44), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(46), 0);
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(48), 1);
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(56), physicalStart);
    DevUuid.CopyTo(data.AsSpan(64));
    return data;
  }

  // ── Dev tree ──────────────────────────────────────────────────────────
  //
  // Holds the DEV_ITEM for each device plus DEV_EXTENT entries that mirror
  // the chunk allocations (btrfs check cross-references dev extents with
  // chunk stripes). Key layout for DEV_EXTENT: (devid, DEV_EXTENT,
  // physical_offset).

  private void WriteDevTree(byte[] image) {
    // DEV_TREE holds DEV_EXTENT entries only; DEV_ITEM lives in CHUNK_TREE.
    // btrfs check rejects DEV_ITEM found in DEV_TREE with "Invalid key
    // type(DEV_ITEM) found in root(DEV_TREE)".
    var items = new List<(long objId, byte type, long offset, byte[] data)> {
      (1, DevExtent, SystemChunkStart,
        BuildDevExtent(ChunkTreeObjectId, FirstChunkTreeObjectId, SystemChunkStart, SystemChunkLength)),
      (1, DevExtent, MetadataChunkStart,
        BuildDevExtent(ChunkTreeObjectId, FirstChunkTreeObjectId, MetadataChunkStart, this._metadataChunkLength)),
      (1, DevExtent, this._dataChunkStart,
        BuildDevExtent(ChunkTreeObjectId, FirstChunkTreeObjectId, this._dataChunkStart, DataChunkLength)),
    };
    SortLeafItems(items);
    WriteLeafNode(image, DevTreeOff, DevTreeObjectId, items);
  }

  // btrfs_dev_extent (fs/btrfs/ctree.h). 48 bytes.
  private static byte[] BuildDevExtent(long chunkTreeObjectId, long chunkObjectId, long chunkOffset, long length) {
    var d = new byte[48];
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(0), chunkTreeObjectId);
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(8), chunkObjectId);
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(16), chunkOffset);
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(24), length);
    // chunk_tree_uuid at offset 32 (16 bytes) — leave zero (check-level tolerant).
    return d;
  }

  // ── Root tree ────────────────────────────────────────────────────────
  //
  // The root tree contains a ROOT_ITEM for every other tree in the FS.
  // btrfs_setup_all_roots walks this list to find EXTENT_TREE (2),
  // DEV_TREE (4), FS_TREE (5), and CSUM_TREE (7); missing any of these
  // causes "could not setup extent tree" / similar open-time failures.

  private void WriteRootTree(byte[] image) {
    // The FS-tree ROOT_ITEM points at the FS-tree root block, which is always
    // _fsTreeBlockOffsets[0] (== FsTreeOff): the sole leaf when the tree fits
    // one node, otherwise the internal index node atop the leaves. Its level is
    // recorded in the block header, not here.
    var items = new List<(long objId, byte type, long offset, byte[] data)> {
      (ExtentTreeObjectId, RootItem, 0, BuildRootItem(this._extentTreeOff, rootDirId: 0)),
      (DevTreeObjectId,    RootItem, 0, BuildRootItem(DevTreeOff,          rootDirId: 0)),
      (FsTreeObjectId,     RootItem, 0, BuildRootItem(FsTreeOff,           rootDirId: FirstFreeObjectId)),
      (CsumTreeObjectId,   RootItem, 0, BuildRootItem(this._csumTreeOff,   rootDirId: 0)),
    };
    SortLeafItems(items);
    WriteLeafNode(image, RootTreeOff, RootTreeObjectId, items);
  }

  // 439-byte ROOT_ITEM (fs/btrfs/ctree.h btrfs_root_item). Only a few
  // fields need sensible values for btrfs check to accept the image:
  // bytenr points at the tree root, generation matches, refs>=1.
  private static byte[] BuildRootItem(long bytenr, long rootDirId) {
    var d = new byte[439];
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(160), 1);           // generation
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(168), rootDirId);   // root_dirid
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(176), bytenr);      // bytenr
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(184), NodeSize);    // byte_limit
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(192), NodeSize);    // bytes_used
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(216), 1);          // refs
    return d;
  }

  // ── FS tree ─────────────────────────────────────────────────────────

  // A node in the directory tree assembled from the added file paths. Each
  // directory and file is assigned its own sequential objectid; directories
  // accumulate their children so the FS-tree builder can emit one INODE_ITEM,
  // the parent DIR_ITEM/DIR_INDEX pair, and a child→parent INODE_REF per link.
  private sealed class DirNode {
    public long ObjectId;
    public long ParentObjectId;
    public string Name = "";
    // child name → directory node (deterministic insertion order).
    public readonly Dictionary<string, DirNode> SubDirs = new(StringComparer.Ordinal);
    // file name → payload, in insertion order.
    public readonly List<(string name, byte[] data)> Files = [];
    // file name → its allocated inode objectid.
    public readonly Dictionary<string, long> FileObjectIds = new(StringComparer.Ordinal);
  }

  // A single leaf's worth of FS-tree items together with the lowest key in
  // the leaf — the internal-node key pointer that references it.
  private sealed class FsTreeLeaf {
    public required List<(long objId, byte type, long offset, byte[] data)> Items;
    public long FirstObjId;
    public byte FirstType;
    public long FirstOffset;
  }

  // Builds the complete sorted FS-tree item set and packs it into leaf-sized
  // batches. Returns one batch per leaf node; the caller emits an internal
  // index node above them when more than one leaf results. Index 0 of the
  // returned list is always the first (lowest-key) leaf.
  private List<FsTreeLeaf> BuildFsTreeLeaves() {
    // ── Phase 1: assemble the directory tree ────────────────────────────
    // The FS-tree root directory is BTRFS_FIRST_FREE_OBJECTID (256); every
    // other inode gets the next sequential objectid as it is first seen.
    var nextObjectId = FirstFreeObjectId;
    var root = new DirNode {
      ObjectId = nextObjectId++,
      ParentObjectId = FirstFreeObjectId, // root's parent is itself in btrfs
      Name = "",
    };

    foreach (var (path, data) in this._files) {
      var parts = path.Split('/');
      var dir = root;
      for (var p = 0; p < parts.Length - 1; p++) {
        var component = parts[p];
        if (!dir.SubDirs.TryGetValue(component, out var child)) {
          child = new DirNode {
            ObjectId = nextObjectId++,
            ParentObjectId = dir.ObjectId,
            Name = component,
          };
          dir.SubDirs[component] = child;
        }
        dir = child;
      }
      var fileName = parts[^1];
      dir.Files.Add((fileName, data));
      dir.FileObjectIds[fileName] = nextObjectId++;
    }

    // ── Phase 2: emit FS-tree items inode-by-inode ──────────────────────
    var items = new List<(long objId, byte type, long offset, byte[] data)>();
    EmitDirectory(items, root, isRoot: true);
    SortLeafItems(items);

    // ── Phase 3: pack the sorted items into leaf nodes ──────────────────
    return PackIntoLeaves(items);
  }

  // Usable bytes for items inside a node (after the 101-byte btrfs_header).
  private const int LeafItemSpace = NodeSize - 101;
  // Per-item overhead inside a leaf: the 25-byte btrfs_item header. The item's
  // data is stored separately at the tail of the leaf.
  private const int LeafItemHeader = 25;
  // Per-child overhead inside an internal node: key(17)+blockptr(8)+gen(8).
  private const int KeyPtrSize = 33;
  // Maximum children an internal node can index.
  private const int MaxKeyPtrs = LeafItemSpace / KeyPtrSize;

  // Greedily slices the sorted item set into leaves, each bounded by the node's
  // usable space. A single oversized item (one whose 25-byte header plus data
  // exceeds a whole leaf) is impossible here because inline file payloads are
  // bounded well below the node size, but we still place such an item alone so
  // the writer never silently drops data.
  private static List<FsTreeLeaf> PackIntoLeaves(
      List<(long objId, byte type, long offset, byte[] data)> items) {
    var leaves = new List<FsTreeLeaf>();
    var current = new List<(long, byte, long, byte[])>();
    var used = 0;

    foreach (var item in items) {
      var cost = LeafItemHeader + item.data.Length;
      if (current.Count > 0 && used + cost > LeafItemSpace) {
        leaves.Add(MakeLeaf(current));
        current = [];
        used = 0;
      }
      current.Add(item);
      used += cost;
    }
    if (current.Count > 0 || leaves.Count == 0)
      leaves.Add(MakeLeaf(current));
    return leaves;
  }

  private static FsTreeLeaf MakeLeaf(List<(long objId, byte type, long offset, byte[] data)> items) {
    var leaf = new FsTreeLeaf { Items = items };
    if (items.Count > 0) {
      leaf.FirstObjId = items[0].objId;
      leaf.FirstType = items[0].type;
      leaf.FirstOffset = items[0].offset;
    }
    return leaf;
  }

  // Serialises the FS-tree blocks. When the item set fits one leaf the FS-tree
  // root *is* that leaf (level 0). Otherwise the root becomes a level-1
  // internal node whose key pointers reference each leaf in key order; the
  // leaves occupy the blocks following the root.
  private void WriteFsTree(byte[] image, List<FsTreeLeaf> leaves) {
    if (leaves.Count == 1) {
      WriteLeafNode(image, (int)this._fsTreeBlockOffsets[0], FsTreeObjectId, leaves[0].Items);
      return;
    }

    if (leaves.Count > MaxKeyPtrs)
      throw new InvalidOperationException(
        $"BtrfsWriter: directory tree needs {leaves.Count} leaves, exceeding the "
        + $"{MaxKeyPtrs}-child single internal node (deeper trees are not emitted).");

    // Block 0 is the internal root; blocks 1..N are the leaves.
    var keyPtrs = new List<(long objId, byte type, long offset, long blockPtr)>();
    for (var i = 0; i < leaves.Count; i++) {
      var leafOff = this._fsTreeBlockOffsets[i + 1];
      WriteLeafNode(image, (int)leafOff, FsTreeObjectId, leaves[i].Items);
      keyPtrs.Add((leaves[i].FirstObjId, leaves[i].FirstType, leaves[i].FirstOffset, leafOff));
    }
    WriteInternalNode(image, (int)this._fsTreeBlockOffsets[0], FsTreeObjectId, level: 1, keyPtrs);
  }

  // Emits all FS-tree items for a directory inode and recurses into its
  // children. Order of allocation matches phase-1 discovery so objectids are
  // monotonic across the produced item set.
  private static void EmitDirectory(
      List<(long objId, byte type, long offset, byte[] data)> items,
      DirNode dir, bool isRoot) {
    // Directory inode size = sum of (name_len * 2) across its entries: per
    // fs/btrfs/inode.c btrfs_i_size_write each link adds name_len once for the
    // DIR_ITEM and once for the DIR_INDEX. btrfs check validates this exactly.
    long dirSize = 0;
    foreach (var sub in dir.SubDirs.Values)
      dirSize += Encoding.UTF8.GetBytes(sub.Name).Length * 2;
    foreach (var (name, _) in dir.Files)
      dirSize += Encoding.UTF8.GetBytes(name).Length * 2;

    // Btrfs counts only "." against a directory's nlink — not ".." and not
    // children — so every directory has nlink=1 regardless of child count.
    var dirInode = BuildInodeItem(mode: 0x41ED /* S_IFDIR | 0755 */, size: dirSize, nlink: 1);
    items.Add((dir.ObjectId, InodeItem, 0L, dirInode));

    if (isRoot) {
      // INODE_REF for the root directory. mkfs stores (256, INODE_REF, 256)
      // with name "..". btrfs check accepts this self-reference.
      var rootDotDot = ".."u8.ToArray();
      var rootRef = new byte[10 + rootDotDot.Length];
      BinaryPrimitives.WriteInt64LittleEndian(rootRef.AsSpan(0), 0); // index
      BinaryPrimitives.WriteUInt16LittleEndian(rootRef.AsSpan(8), (ushort)rootDotDot.Length);
      rootDotDot.CopyTo(rootRef, 10);
      items.Add((dir.ObjectId, InodeRef, dir.ObjectId, rootRef));
    }

    // DIR_INDEX key offsets start at 2 to reserve 0/1 for "." / "..".
    long index = 2;

    // Subdirectory links first, then file links — deterministic per discovery.
    foreach (var sub in dir.SubDirs.Values) {
      EmitChildLink(items, dir.ObjectId, sub.ObjectId, sub.Name, isDir: true, ref index);
      EmitChildBackRef(items, sub.ObjectId, dir.ObjectId, sub.Name, index - 1);
    }

    foreach (var (name, data) in dir.Files) {
      var fileObjectId = AllocateFileObjectId(dir, name);
      var fileInode = BuildInodeItem(mode: 0x81A4 /* S_IFREG | 0644 */, size: data.Length, nlink: 1);
      items.Add((fileObjectId, InodeItem, 0L, fileInode));

      EmitChildLink(items, dir.ObjectId, fileObjectId, name, isDir: false, ref index);
      EmitChildBackRef(items, fileObjectId, dir.ObjectId, name, index - 1);

      // Inline EXTENT_DATA (btrfs_file_extent_item). Layout:
      //   0..7  generation, 8..15 ram_bytes, 16 compression (0=none),
      //   17 encryption (0), 18..19 other_encoding, 20 type (0=inline),
      //   21.. inline payload.
      var extent = new byte[21 + data.Length];
      BinaryPrimitives.WriteInt64LittleEndian(extent.AsSpan(0), 1);           // generation
      BinaryPrimitives.WriteInt64LittleEndian(extent.AsSpan(8), data.Length); // ram_bytes
      extent[16] = 0; // compression = none
      extent[20] = 0; // type = inline
      data.CopyTo(extent, 21);
      items.Add((fileObjectId, ExtentData, 0L, extent));
    }

    // Recurse after emitting this directory's own links so the produced item
    // set still sorts cleanly by (objectid, type, offset).
    foreach (var sub in dir.SubDirs.Values)
      EmitDirectory(items, sub, isRoot: false);
  }

  // Files are allocated objectids lazily here so that, within a directory,
  // subdirectory inodes (allocated during phase 1) keep their lower ids and
  // files take the ids following the entire subtree. The mapping is stored on
  // the node the first time it is needed and reused for the link/inode/extent.
  private static long AllocateFileObjectId(DirNode dir, string name) {
    return dir.FileObjectIds[name];
  }

  // Adds a DIR_INDEX (enumeration) and DIR_ITEM (name-hash lookup) entry for a
  // child in its parent directory. Both carry the same btrfs_dir_item value.
  private static void EmitChildLink(
      List<(long objId, byte type, long offset, byte[] data)> items,
      long parentObjectId, long childObjectId, string name, bool isDir, ref long index) {
    var entry = BuildDirItemValue(childObjectId, name, isDir);

    // DIR_INDEX entry — the reader uses this for enumeration.
    items.Add((parentObjectId, DirIndex, index, entry));

    // DIR_ITEM with btrfs_name_hash-keyed offset for name lookup. btrfs uses
    // CRC-32C seeded with ~1 (0xFFFFFFFE) without the final XOR inversion —
    // see fs/btrfs/crc32c.h btrfs_name_hash.
    var nameBytes = Encoding.UTF8.GetBytes(name);
    items.Add((parentObjectId, DirItem, BtrfsNameHash(nameBytes), entry));

    index++;
  }

  // Adds the child→parent INODE_REF (key = (childObjectId, INODE_REF,
  // parentObjectId)) naming the link and recording the DIR_INDEX index.
  private static void EmitChildBackRef(
      List<(long objId, byte type, long offset, byte[] data)> items,
      long childObjectId, long parentObjectId, string name, long index) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var inodeRef = new byte[10 + nameBytes.Length];
    BinaryPrimitives.WriteInt64LittleEndian(inodeRef.AsSpan(0), index);
    BinaryPrimitives.WriteUInt16LittleEndian(inodeRef.AsSpan(8), (ushort)nameBytes.Length);
    nameBytes.CopyTo(inodeRef, 10);
    items.Add((childObjectId, InodeRef, parentObjectId, inodeRef));
  }

  // Builds a 160-byte INODE_ITEM. Size at offset 16 is what our reader
  // inspects; the rest is canonical shape for btrfs-progs.
  private static byte[] BuildInodeItem(uint mode, long size, uint nlink) {
    var d = new byte[160];
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(0), 1);   // generation
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(8), 1);   // transid
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(16), size);
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(24), size); // bytes
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(40), nlink);
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(52), mode);
    return d;
  }

  private static byte[] BuildDirItemValue(long childInode, string name, bool isDir) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var v = new byte[30 + nameBytes.Length];
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0), childInode);
    v[8] = InodeItem;
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(9), 0);
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(17), 1); // transid
    BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(25), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(27), (ushort)nameBytes.Length);
    v[29] = (byte)(isDir ? 2 : 1);
    nameBytes.CopyTo(v, 30);
    return v;
  }

  // ── Leaf node serialisation ──────────────────────────────────────────
  //
  // Canonical btrfs_header layout per fs/btrfs/ctree.h:
  //   0   csum[32]
  //   32  fsid[16]
  //   48  bytenr (u64)
  //   56  flags (u64)
  //   64  chunk_tree_uuid[16]
  //   80  generation (u64)
  //   88  owner (u64)
  //   96  nritems (u32)
  //   100 level (u8)           — total header = 101 bytes
  private static void WriteLeafNode(byte[] image, int nodeOff, long ownerObjectId, List<(long objId, byte type, long offset, byte[] data)> items) {
    FsUuid.CopyTo(image.AsSpan(nodeOff + 32));
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 48), nodeOff); // bytenr
    // flags: bit0 = WRITTEN, top 8 bits (56..63) = backref_rev.
    // backref_rev MUST be 1 (MIXED_BACKREF_REV). If it is 0 (OLD_BACKREF_REV)
    // btrfs check treats every tree block as shared_block_ref with
    // parent=bytenr, which then doesn't match our TREE_BLOCK_REF (root) inline
    // backrefs and produces the "no backref item in extent tree" cascade.
    const long WrittenFlag = 1L;
    const long MixedBackrefRev = 1L << 56;
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 56), WrittenFlag | MixedBackrefRev);
    FsUuid.CopyTo(image.AsSpan(nodeOff + 64));                                    // chunk_tree_uuid
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 80), 1);       // generation
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 88), ownerObjectId); // owner
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(nodeOff + 96), (uint)items.Count);
    image[nodeOff + 100] = 0; // level = 0 (leaf)

    // Item headers grow forward from offset 101; data grows backward from node_end.
    var dataEnd = NodeSize;
    for (var i = 0; i < items.Count; i++) {
      var (objId, type, offset, data) = items[i];
      dataEnd -= data.Length;
      var dataOffsetInItems = dataEnd - 101;

      var itemOff = nodeOff + 101 + i * 25;
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(itemOff), objId);
      image[itemOff + 8] = type;
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(itemOff + 9), offset);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(itemOff + 17), (uint)dataOffsetInItems);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(itemOff + 21), (uint)data.Length);

      data.CopyTo(image, nodeOff + 101 + dataOffsetInItems);
    }
  }

  // ── Internal (index) node serialisation ──────────────────────────────
  //
  // Same 101-byte btrfs_header as a leaf, but level >= 1 and the body is a
  // packed array of btrfs_key_ptr entries (fs/btrfs/ctree.h):
  //   key:        objectid(8) + type(1) + offset(8)   = 17
  //   blockptr:   logical address of the child node    = 8
  //   generation: child generation                     = 8   → 33 bytes total
  // Children must appear in ascending key order; each key is the lowest key
  // present in the referenced child node.
  private static void WriteInternalNode(byte[] image, int nodeOff, long ownerObjectId,
      byte level, List<(long objId, byte type, long offset, long blockPtr)> keyPtrs) {
    FsUuid.CopyTo(image.AsSpan(nodeOff + 32));
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 48), nodeOff); // bytenr
    const long WrittenFlag = 1L;
    const long MixedBackrefRev = 1L << 56;
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 56), WrittenFlag | MixedBackrefRev);
    FsUuid.CopyTo(image.AsSpan(nodeOff + 64));                                    // chunk_tree_uuid
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 80), 1);       // generation
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 88), ownerObjectId);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(nodeOff + 96), (uint)keyPtrs.Count);
    image[nodeOff + 100] = level;

    for (var i = 0; i < keyPtrs.Count; i++) {
      var (objId, type, offset, blockPtr) = keyPtrs[i];
      var p = nodeOff + 101 + i * 33;
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(p), objId);
      image[p + 8] = type;
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(p + 9), offset);
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(p + 17), blockPtr);
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(p + 25), 1); // generation
    }
  }

  private static void SortLeafItems(List<(long objId, byte type, long offset, byte[] data)> items) {
    items.Sort((a, b) => {
      var c = a.objId.CompareTo(b.objId);
      if (c != 0) return c;
      c = a.type.CompareTo(b.type);
      if (c != 0) return c;
      return a.offset.CompareTo(b.offset);
    });
  }

  // ── btrfs_name_hash ─────────────────────────────────────────────────
  //
  // CRC-32C (Castagnoli) seeded with 0xFFFFFFFE and NO final inversion.
  // Matches fs/btrfs/crc32c.h btrfs_name_hash(name, len).
  private static long BtrfsNameHash(byte[] data) {
    const uint poly = 0x82F63B78u; // Castagnoli reflected
    var crc = 0xFFFFFFFEu; // ~1
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; i++)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : (crc >> 1);
    }
    return crc;
  }

  // ── CRC-32C stamping ─────────────────────────────────────────────────

  /// <summary>
  /// Writes CRC-32C over bytes [blockOff+32 .. blockOff+blockSize) as
  /// little-endian u32 at <paramref name="blockOff"/>+0. Bytes
  /// [blockOff+4 .. blockOff+32) are zeroed (unused for CRC32 csum type).
  /// </summary>
  private static void WriteBlockChecksum(byte[] image, int blockOff, int blockSize) {
    var payload = image.AsSpan(blockOff + 32, blockSize - 32);
    var crc = Crc32.Compute(payload, Crc32.Castagnoli);
    image.AsSpan(blockOff, 32).Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(blockOff, 4), crc);
  }
}
