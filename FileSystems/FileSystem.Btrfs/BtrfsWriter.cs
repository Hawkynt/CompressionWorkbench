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
  private const int FsTreeOff = 0x50000;
  private const int ExtentTreeOff = 0x60000;
  private const int CsumTreeOff = 0x70000;
  private const int DataRegionOff = 0x80000;

  // Chunk ranges (logical == physical in this image). Lengths are multiples
  // of BTRFS_STRIPE_LEN (64 KiB).
  //   SYSTEM:   [0x10000, 0x30000) 128 KiB — superblock gap + chunk tree
  //   METADATA: [0x30000, 0x80000) 320 KiB — dev/root/fs/extent/csum trees
  //   DATA:     [0x80000, 0xC0000) 256 KiB — file data region
  private const long SystemChunkStart = 0x10000;
  private const long SystemChunkLength = 0x20000;  // 128 KiB (sb gap + chunk tree)
  private const long MetadataChunkStart = 0x30000;
  private const long MetadataChunkLength = 0x50000; // 320 KiB (dev+root+fs+extent+csum)
  private const long DataChunkStart = 0x80000;
  private const long DataChunkLength = 0x40000;    // 256 KiB data region

  private const long TotalSize = 0xC0000; // 768 KiB image

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

  /// <summary>Adds a file to the image. File data becomes an inline
  /// <c>EXTENT_DATA</c> item in the FS tree leaf.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (this._files.Count >= 64)
      throw new InvalidOperationException("BtrfsWriter supports at most 64 files in a single leaf node.");
    var leaf = Path.GetFileName(name);
    if (leaf.Length > 255) leaf = leaf[..255];
    this._files.Add((leaf, data));
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var image = new byte[TotalSize];

    WriteSuperblock(image);
    WriteChunkTree(image);
    WriteDevTree(image);
    WriteRootTree(image);
    BuildFsTree(image);
    WriteExtentTree(image);
    WriteEmptyTree(image, CsumTreeOff, CsumTreeObjectId);

    // Every metadata block starts with a 32-byte csum field whose first
    // 4 bytes hold CRC-32C over bytes [32..blockSize).
    WriteBlockChecksum(image, SbOffset, SectorSize);
    WriteBlockChecksum(image, ChunkTreeOff, NodeSize);
    WriteBlockChecksum(image, DevTreeOff, NodeSize);
    WriteBlockChecksum(image, RootTreeOff, NodeSize);
    WriteBlockChecksum(image, FsTreeOff, NodeSize);
    WriteBlockChecksum(image, ExtentTreeOff, NodeSize);
    WriteBlockChecksum(image, CsumTreeOff, NodeSize);

    output.Write(image);
  }

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
    AddTreeBlockExtent(items, FsTreeOff,    FsTreeObjectId);
    AddTreeBlockExtent(items, ExtentTreeOff, ExtentTreeObjectId);
    AddTreeBlockExtent(items, CsumTreeOff,  CsumTreeObjectId);

    // Block groups — one per chunk. Used bytes must match what
    // update_block_group_used() accumulates from the extent items that
    // actually live inside each chunk. chunk_objectid is the FIRST
    // chunk-tree objectid (256 = FIRST_CHUNK_TREE), NOT the chunk-tree
    // root objectid (3). mkfs.btrfs always stores 256 here.
    items.Add((SystemChunkStart, BlockGroupItem, SystemChunkLength,
      BuildBlockGroupItem(used: CountMetadataUseInRange(SystemChunkStart, SystemChunkLength), FirstChunkTreeObjectId, BlockGroupSystem)));
    items.Add((MetadataChunkStart, BlockGroupItem, MetadataChunkLength,
      BuildBlockGroupItem(used: CountMetadataUseInRange(MetadataChunkStart, MetadataChunkLength), FirstChunkTreeObjectId, BlockGroupMetadata)));
    items.Add((DataChunkStart, BlockGroupItem, DataChunkLength,
      BuildBlockGroupItem(used: 0, FirstChunkTreeObjectId, BlockGroupData)));

    SortLeafItems(items);
    WriteLeafNode(image, ExtentTreeOff, ExtentTreeObjectId, items);
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
  private static long CountMetadataUseInRange(long chunkStart, long chunkLength) {
    var chunkEnd = chunkStart + chunkLength;
    long used = 0;
    long[] nodes = [ChunkTreeOff, DevTreeOff, RootTreeOff, FsTreeOff, ExtentTreeOff, CsumTreeOff];
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
  private static void WriteSuperblock(byte[] image) {
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
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x70), TotalSize);
    // bytes_used: actual bytes consumed by metadata + data. btrfs check
    // compares this against the sum of every allocated extent it walks;
    // we have 6 metadata tree blocks (chunk/dev/root/fs/extent/csum) and
    // no file data extents (we store files inline in FS_TREE EXTENT_DATA
    // items which do not contribute to bytes_used).
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x78), 6L * NodeSize);
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
  private static byte[] BuildSuperblockDevItem() {
    var d = new byte[98];
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(0), 1);              // devid
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(8), TotalSize);      // total_bytes
    // bytes_used must equal the sum of every DEV_EXTENT length that
    // references this device. btrfs check complains with
    // "Dev extent's total-byte(X) is not equal to byte-used(Y)" otherwise.
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(16),
      SystemChunkLength + MetadataChunkLength + DataChunkLength);
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

  private static void WriteChunkTree(byte[] image) {
    // Per mkfs.btrfs the CHUNK_TREE holds both DEV_ITEM entries (one per
    // device) and CHUNK_ITEM entries (one per chunk). The key order enforced
    // by SortLeafItems places DEV_ITEM (objId=DEV_ITEMS=1, type=216) before
    // CHUNK_ITEM (objId=FIRST_CHUNK_TREE=256, type=228).
    var items = new List<(long objId, byte type, long offset, byte[] data)> {
      (DevItemsObjectId, DevItem, 1, BuildSuperblockDevItem()),
      (FirstChunkTreeObjectId, ChunkItem, SystemChunkStart,
        BuildChunkItem(SystemChunkLength, BlockGroupSystem, SystemChunkStart)),
      (FirstChunkTreeObjectId, ChunkItem, MetadataChunkStart,
        BuildChunkItem(MetadataChunkLength, BlockGroupMetadata, MetadataChunkStart)),
      (FirstChunkTreeObjectId, ChunkItem, DataChunkStart,
        BuildChunkItem(DataChunkLength, BlockGroupData, DataChunkStart)),
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

  private static void WriteDevTree(byte[] image) {
    // DEV_TREE holds DEV_EXTENT entries only; DEV_ITEM lives in CHUNK_TREE.
    // btrfs check rejects DEV_ITEM found in DEV_TREE with "Invalid key
    // type(DEV_ITEM) found in root(DEV_TREE)".
    var items = new List<(long objId, byte type, long offset, byte[] data)> {
      (1, DevExtent, SystemChunkStart,
        BuildDevExtent(ChunkTreeObjectId, FirstChunkTreeObjectId, SystemChunkStart, SystemChunkLength)),
      (1, DevExtent, MetadataChunkStart,
        BuildDevExtent(ChunkTreeObjectId, FirstChunkTreeObjectId, MetadataChunkStart, MetadataChunkLength)),
      (1, DevExtent, DataChunkStart,
        BuildDevExtent(ChunkTreeObjectId, FirstChunkTreeObjectId, DataChunkStart, DataChunkLength)),
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

  private static void WriteRootTree(byte[] image) {
    var items = new List<(long objId, byte type, long offset, byte[] data)> {
      (ExtentTreeObjectId, RootItem, 0, BuildRootItem(ExtentTreeOff, rootDirId: 0)),
      (DevTreeObjectId,    RootItem, 0, BuildRootItem(DevTreeOff,    rootDirId: 0)),
      (FsTreeObjectId,     RootItem, 0, BuildRootItem(FsTreeOff,     rootDirId: FirstFreeObjectId)),
      (CsumTreeObjectId,   RootItem, 0, BuildRootItem(CsumTreeOff,   rootDirId: 0)),
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

  private void BuildFsTree(byte[] image) {
    var items = new List<(long objId, byte type, long offset, byte[] data)>();

    // Root directory inode size = sum of (name_len * 2) across its dir
    // entries. Per fs/btrfs/inode.c btrfs_i_size_write each link adds
    // name_len*2 (once for DIR_ITEM, once for DIR_INDEX). btrfs check
    // validates dir_isize matches this exact accounting.
    long rootDirSize = 0;
    foreach (var (name, _) in this._files)
      rootDirSize += Encoding.UTF8.GetBytes(name).Length * 2;

    // Root directory INODE_ITEM (objectid = 256). Btrfs counts only "." as
    // a link against directory nlink — not ".." and not children — so
    // nlink=1 for every directory regardless of child count.
    var rootInode = BuildInodeItem(mode: 0x41ED /* S_IFDIR | 0755 */, size: rootDirSize, nlink: 1);
    items.Add((FirstFreeObjectId, InodeItem, 0L, rootInode));

    // INODE_REF for the root directory. mkfs stores (256, INODE_REF, 256)
    // with name "..". btrfs check happily accepts a self-reference here.
    var rootDotDot = "..\0"u8[..2].ToArray();
    var rootRef = new byte[10 + rootDotDot.Length];
    BinaryPrimitives.WriteInt64LittleEndian(rootRef.AsSpan(0), 0); // index
    BinaryPrimitives.WriteUInt16LittleEndian(rootRef.AsSpan(8), (ushort)rootDotDot.Length);
    rootDotDot.CopyTo(rootRef, 10);
    items.Add((FirstFreeObjectId, InodeRef, FirstFreeObjectId, rootRef));

    for (var i = 0; i < this._files.Count; i++) {
      var childInode = FirstFreeObjectId + 1 + i;
      var (name, data) = this._files[i];

      var fileInode = BuildInodeItem(mode: 0x81A4 /* S_IFREG | 0644 */, size: data.Length, nlink: 1);
      items.Add((childInode, InodeItem, 0L, fileInode));

      // INODE_REF links the child to its parent directory. Index = DIR_INDEX
      // key offset used below (starts at 2 to reserve 0/1 for "." / "..").
      var nameBytes = Encoding.UTF8.GetBytes(name);
      var inodeRef = new byte[10 + nameBytes.Length];
      BinaryPrimitives.WriteInt64LittleEndian(inodeRef.AsSpan(0), 2 + i);
      BinaryPrimitives.WriteUInt16LittleEndian(inodeRef.AsSpan(8), (ushort)nameBytes.Length);
      nameBytes.CopyTo(inodeRef, 10);
      items.Add((childInode, InodeRef, FirstFreeObjectId, inodeRef));

      // DIR_INDEX entry in the parent dir; reader uses this for enumeration.
      var dirEntry = BuildDirItemValue(childInode, name, isDir: false);
      items.Add((FirstFreeObjectId, DirIndex, 2 + i, dirEntry));

      // DIR_ITEM with btrfs_name_hash-keyed offset for name lookup. btrfs
      // uses CRC-32C seeded with ~1 (0xFFFFFFFE) and omits the final XOR
      // inversion — see fs/btrfs/crc32c.h btrfs_name_hash.
      items.Add((FirstFreeObjectId, DirItem, BtrfsNameHash(nameBytes), dirEntry));

      // Inline EXTENT_DATA (btrfs_file_extent_item). Layout:
      //   0..7  generation
      //   8..15 ram_bytes
      //   16    compression (0=none)
      //   17    encryption  (0)
      //   18..19 other_encoding
      //   20    type (0=inline)
      //   21..  inline payload
      var extent = new byte[21 + data.Length];
      BinaryPrimitives.WriteInt64LittleEndian(extent.AsSpan(0), 1);           // generation
      BinaryPrimitives.WriteInt64LittleEndian(extent.AsSpan(8), data.Length); // ram_bytes
      extent[16] = 0; // compression = none
      extent[20] = 0; // type = inline
      data.CopyTo(extent, 21);
      items.Add((childInode, ExtentData, 0L, extent));
    }

    SortLeafItems(items);
    WriteLeafNode(image, FsTreeOff, FsTreeObjectId, items);
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
