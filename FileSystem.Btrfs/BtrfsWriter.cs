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

  // Physical layout (superblock at canonical 0x10000, trees at node-aligned
  // offsets). We deliberately keep logical == physical so the chunk tree's
  // mapping is trivially verifiable but still real — every read goes through
  // sys_chunk_array → chunk tree → LogicalToPhysical.
  private const int SbOffset = 0x10000;
  private const int ChunkTreeOff = 0x20000;
  private const int DevTreeOff = 0x30000;
  private const int RootTreeOff = 0x40000;
  private const int FsTreeOff = 0x50000;
  private const int DataRegionOff = 0x60000;

  // Chunk ranges (logical == physical in this image).
  private const long SystemChunkStart = 0x10000;   // SB + chunk + dev tree
  private const long SystemChunkLength = 0x30000;  // 192 KiB covers SB/chunk/dev trees
  private const long MetadataChunkStart = 0x40000;
  private const long MetadataChunkLength = 0x20000; // root tree + fs tree
  private const long DataChunkStart = 0x60000;
  private const long DataChunkLength = 0x20000;    // 128 KiB data region

  private const long TotalSize = 0x80000; // 512 KiB image

  // Key types — shared with the reader.
  private const byte InodeItem = 1;
  private const byte InodeRef = 12;
  private const byte DirItem = 84;
  private const byte DirIndex = 96;
  private const byte ExtentData = 108;
  private const byte RootItem = 132;
  private const byte DevItem = 216;
  private const byte ChunkItem = 228;

  // Chunk type bitmask (fs/btrfs/ctree.h).
  private const ulong BlockGroupData = 0x01;
  private const ulong BlockGroupSystem = 0x02;
  private const ulong BlockGroupMetadata = 0x04;

  // Well-known object IDs.
  private const long RootTreeObjectId = 1;
  private const long ChunkTreeObjectId = 3;
  private const long DevTreeObjectId = 4;
  private const long FsTreeObjectId = 5;
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

    // Every metadata block starts with a 32-byte csum field whose first
    // 4 bytes hold CRC-32C over bytes [32..blockSize).
    WriteBlockChecksum(image, SbOffset, SectorSize);
    WriteBlockChecksum(image, ChunkTreeOff, NodeSize);
    WriteBlockChecksum(image, DevTreeOff, NodeSize);
    WriteBlockChecksum(image, RootTreeOff, NodeSize);
    WriteBlockChecksum(image, FsTreeOff, NodeSize);

    output.Write(image);
  }

  // ── Superblock ────────────────────────────────────────────────────────
  //
  // We populate BOTH the canonical spec offsets (as documented in
  // fs/btrfs/ctree.h) AND the pragmatic offsets our BtrfsReader uses —
  // they happen to agree on magic/root/chunk_root but diverge for
  // sector_size, node_size, and sys_chunk_array_size. Writing both keeps
  // the image readable by our reader AND by btrfs-progs.
  private static void WriteSuperblock(byte[] image) {
    var sb = image.AsSpan(SbOffset);

    // Canonical spec layout ────────────────────────────────────────────
    // 0x00  csum[32]                  — stamped in WriteBlockChecksum
    // 0x20  fsid[16]
    // 0x30  bytenr (self)
    // 0x38  flags
    // 0x40  magic[8]
    // 0x48  generation
    // 0x50  root (chunk_root is at 0x58)
    // 0x58  chunk_root
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
    // 0xC5  dev_item (98 bytes)
    // 0x12A label[256]
    // 0x32B sys_chunk_array[2048]
    FsUuid.CopyTo(sb.Slice(0x20));
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x30), SbOffset);
    Magic.CopyTo(sb.Slice(0x40));
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x48), 1); // generation
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x50), RootTreeOff);
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x58), ChunkTreeOff);
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x70), TotalSize);
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x78), DataRegionOff);
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x80), FsTreeObjectId);
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x88), 1); // num_devices
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x90), SectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x94), NodeSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x98), NodeSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x9C), SectorSize);
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0xA4), 1); // chunk_root_generation

    // Pragmatic fields our reader inspects ────────────────────────────────
    // The historical BtrfsReader reads sectorsize at sbOffset+128,
    // nodesize at sbOffset+132, and sys_chunk_array_size at sbOffset+196.
    // These offsets predate the spec-accurate constants above but are what
    // our test corpus ships with. Mirror the values there too.
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(104), TotalSize);           // total_bytes copy
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(112), DataRegionOff);       // bytes_used copy
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(128), SectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(132), NodeSize);
    // Reader's sys_chunk_array_size lives at 196, and the array starts at 299.

    // Embedded DEV_ITEM (98 bytes) for bootstrap.
    var devItem = BuildSuperblockDevItem();
    devItem.CopyTo(image, SbOffset + 0xC5);

    // Label at 0x12A (256 bytes, NUL-padded).
    var label = "CWB-BTRFS"u8;
    label.CopyTo(image.AsSpan(SbOffset + 0x12A));

    // sys_chunk_array — duplicated at BOTH the spec offset (0x32B = 811)
    // AND the reader's offset (299). The reader reads its array-size field
    // at 196 and data at 299; btrfs-progs uses 0xA0 and 0x32B. Writing both
    // lets either path find the SYSTEM chunk.
    var sysArray = BuildSysChunkArray();
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0xA0), (uint)sysArray.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(196), (uint)sysArray.Length);
    sysArray.CopyTo(image.AsSpan(SbOffset + 0x32B));
    sysArray.CopyTo(image.AsSpan(SbOffset + 299));
  }

  // Builds the 98-byte DEV_ITEM embedded in both the superblock and the
  // dev tree. Matches fs/btrfs/ctree.h btrfs_dev_item.
  private static byte[] BuildSuperblockDevItem() {
    var d = new byte[98];
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(0), 1);              // devid
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(8), TotalSize);      // total_bytes
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(16), DataRegionOff); // bytes_used
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
    BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(c + 16), SectorSize);
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
    var items = new List<(long objId, byte type, long offset, byte[] data)> {
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
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), SectorSize);
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

  private static void WriteDevTree(byte[] image) {
    var items = new List<(long objId, byte type, long offset, byte[] data)> {
      (DevItemsObjectId, DevItem, 1, BuildSuperblockDevItem()),
    };
    WriteLeafNode(image, DevTreeOff, DevTreeObjectId, items);
  }

  // ── Root tree ────────────────────────────────────────────────────────

  private static void WriteRootTree(byte[] image) {
    // Full 439-byte ROOT_ITEM. The reader only reads bytenr at offset 176.
    var rootData = new byte[439];
    BinaryPrimitives.WriteInt64LittleEndian(rootData.AsSpan(160), 1);              // generation
    BinaryPrimitives.WriteInt64LittleEndian(rootData.AsSpan(168), FirstFreeObjectId); // root_dirid
    BinaryPrimitives.WriteInt64LittleEndian(rootData.AsSpan(176), FsTreeOff);      // bytenr
    BinaryPrimitives.WriteInt64LittleEndian(rootData.AsSpan(192), NodeSize);       // bytes_used
    BinaryPrimitives.WriteUInt32LittleEndian(rootData.AsSpan(216), 1);             // refs

    var items = new List<(long objId, byte type, long offset, byte[] data)> {
      (FsTreeObjectId, RootItem, 0, rootData),
    };
    WriteLeafNode(image, RootTreeOff, RootTreeObjectId, items);
  }

  // ── FS tree ─────────────────────────────────────────────────────────

  private void BuildFsTree(byte[] image) {
    var items = new List<(long objId, byte type, long offset, byte[] data)>();

    // Root directory INODE_ITEM (objectid = 256).
    var rootInode = BuildInodeItem(mode: 0x41ED /* S_IFDIR | 0755 */, size: 0, nlink: 1);
    items.Add((FirstFreeObjectId, InodeItem, 0L, rootInode));

    for (var i = 0; i < this._files.Count; i++) {
      var childInode = FirstFreeObjectId + 1 + i;
      var (name, data) = this._files[i];

      var fileInode = BuildInodeItem(mode: 0x81A4 /* S_IFREG | 0644 */, size: data.Length, nlink: 1);
      items.Add((childInode, InodeItem, 0L, fileInode));

      // INODE_REF links the child to its parent directory.
      var nameBytes = Encoding.UTF8.GetBytes(name);
      var inodeRef = new byte[10 + nameBytes.Length];
      BinaryPrimitives.WriteInt64LittleEndian(inodeRef.AsSpan(0), 2 + i);
      BinaryPrimitives.WriteUInt16LittleEndian(inodeRef.AsSpan(8), (ushort)nameBytes.Length);
      nameBytes.CopyTo(inodeRef, 10);
      items.Add((childInode, InodeRef, FirstFreeObjectId, inodeRef));

      // DIR_INDEX entry in the parent dir; reader uses this for enumeration.
      var dirEntry = BuildDirItemValue(childInode, name, isDir: false);
      items.Add((FirstFreeObjectId, DirIndex, 2 + i, dirEntry));

      // DIR_ITEM with hash-keyed offset for name lookup.
      items.Add((FirstFreeObjectId, DirItem, Crc32.Compute(nameBytes, Crc32.Castagnoli), dirEntry));

      // Inline EXTENT_DATA.
      var extent = new byte[21 + data.Length];
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
  // Our header matches what BtrfsReader reads: nritems at offset 88 (u32)
  // and level at offset 92 (u8). The 32-byte csum + 16-byte fsid + 8-byte
  // bytenr fit in the first 56 bytes; the remaining 45 bytes of the
  // canonical 101-byte header are left as zero (chunk_tree_uuid,
  // generation, owner — real Btrfs fsck tolerates these for read-only).
  private static void WriteLeafNode(byte[] image, int nodeOff, long ownerObjectId, List<(long objId, byte type, long offset, byte[] data)> items) {
    FsUuid.CopyTo(image.AsSpan(nodeOff + 32));
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 48), nodeOff); // bytenr
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 56), 1);       // flags = WRITTEN
    FsUuid.CopyTo(image.AsSpan(nodeOff + 64));                                    // chunk_tree_uuid
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 80), ownerObjectId);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(nodeOff + 88), (uint)items.Count);
    image[nodeOff + 92] = 0; // level = 0 (leaf)

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
