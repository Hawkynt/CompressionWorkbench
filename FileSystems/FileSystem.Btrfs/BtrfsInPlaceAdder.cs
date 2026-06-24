#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Btrfs;

/// <summary>
/// Genuine copy-on-write in-place add for Btrfs images produced by
/// <see cref="BtrfsWriter"/> — the spec-faithful alternative to the whole-image
/// rebuild in <see cref="BtrfsModifier"/>. A single small file is added to the
/// root directory by writing NEW (CoW) tree blocks only for the path that
/// changed and leaving every untouched node and every existing data extent
/// byte-identical at its original offset:
/// <list type="number">
///   <item>A new FS-tree leaf block is allocated from free metadata space; it
///   holds a copy of the old FS-tree leaf with the new file's
///   <c>INODE_ITEM</c>, <c>INODE_REF</c>, <c>DIR_ITEM</c>, <c>DIR_INDEX</c> and
///   an inline <c>EXTENT_DATA</c> item inserted (and the root directory inode's
///   size grown), re-sorted and re-packed.</item>
///   <item>A new EXTENT_TREE leaf is allocated; the three CoW'd metadata blocks
///   (root, fs, extent trees) get fresh <c>EXTENT_ITEM</c>/<c>TREE_BLOCK_REF</c>
///   entries and the freed old blocks' entries are dropped — block-group
///   accounting is unchanged because the block count is preserved.</item>
///   <item>A new ROOT_TREE leaf is allocated; the <c>FS_TREE</c> and
///   <c>EXTENT_TREE</c> <c>ROOT_ITEM</c>s are repointed at the new fs / extent
///   blocks and their generation bumped.</item>
///   <item>The superblock's <c>root</c> pointer, <c>generation</c> and
///   <c>chunk_root_generation</c> are bumped to the next transid.</item>
///   <item>CRC-32C (Castagnoli) is recomputed for every new/modified block and
///   the superblock.</item>
/// </list>
/// <para>
/// Only the inline-small-file / single-FS-tree-leaf / root-directory-target
/// shape is handled in place; every other case throws
/// <see cref="NotSupportedException"/> so the caller can fall back to the
/// verified rebuild (see <see cref="BtrfsFormatDescriptor.Add"/>):
/// nested-directory targets, files at/above one sector (regular data extents),
/// a multi-leaf FS tree (internal root node), or no free metadata slot.
/// </para>
/// </summary>
public static class BtrfsInPlaceAdder {

  private const int SbOffset = 0x10000;
  private const int NodeSize = 16384;
  private const int SectorSize = 4096;
  private const int HeaderSize = 101;       // btrfs_header
  private const int LeafItemHeader = 25;    // btrfs_item
  private const int MaxInlineDataSize = SectorSize;

  // Key types.
  private const byte InodeItem = 1;
  private const byte InodeRef = 12;
  private const byte DirItem = 84;
  private const byte DirIndex = 96;
  private const byte ExtentData = 108;
  private const byte RootItem = 132;
  private const byte ExtentItemType = 168;

  // Well-known object IDs.
  private const long RootTreeObjectId = 1;
  private const long ExtentTreeObjectId = 2;
  private const long FsTreeObjectId = 5;
  private const long FirstFreeObjectId = 256;

  private static readonly byte[] Magic = "_BHRfS_M"u8.ToArray();

  /// <summary>
  /// Adds (or replaces) a small file in the root directory of
  /// <paramref name="archive"/> via copy-on-write. Throws
  /// <see cref="NotSupportedException"/> for any shape the in-place path does
  /// not handle so the caller can rebuild instead.
  /// </summary>
  public static void AddFile(Stream archive, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    if (name.Contains('/') || name.Contains('\\'))
      throw new NotSupportedException("Btrfs in-place add: nested sub-directory targets use rebuild.");
    if (data.Length >= MaxInlineDataSize)
      throw new NotSupportedException("Btrfs in-place add: files >= one sector need a regular data extent — use rebuild.");

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    AddFile(image, name, data);

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
    archive.Flush();
  }

  /// <summary>In-memory variant operating directly on the image bytes.</summary>
  public static void AddFile(byte[] image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    if (name.Contains('/') || name.Contains('\\'))
      throw new NotSupportedException("Btrfs in-place add: nested sub-directory targets use rebuild.");
    if (data.Length >= MaxInlineDataSize)
      throw new NotSupportedException("Btrfs in-place add: files >= one sector need a regular data extent — use rebuild.");

    if (image.Length < SbOffset + 0x400 || !image.AsSpan(SbOffset + 0x40, 8).SequenceEqual(Magic))
      throw new InvalidDataException("Btrfs in-place add: not a recognised Btrfs image.");

    var sb = new Superblock(image);
    if (sb.NodeSize != NodeSize || sb.SectorSize != SectorSize)
      throw new NotSupportedException(
        $"Btrfs in-place add: only node={NodeSize}/sector={SectorSize} images are handled (got node={sb.NodeSize}/sector={sb.SectorSize}).");

    var chunkMap = ChunkMap.Build(image, sb);

    // Locate the root-tree leaf (must be a single leaf — level 0).
    var rootPhys = chunkMap.ToPhysical(sb.RootTreeLogical);
    if (rootPhys < 0) throw new InvalidDataException("Btrfs in-place add: root tree unreachable.");
    if (image[rootPhys + 100] != 0)
      throw new NotSupportedException("Btrfs in-place add: multi-level root tree not handled — use rebuild.");

    var rootItems = ReadLeafItems(image, rootPhys);

    var fsRootLogical = FindRootItemBytenr(rootItems, FsTreeObjectId)
      ?? throw new InvalidDataException("Btrfs in-place add: FS_TREE ROOT_ITEM missing.");
    var extentRootLogical = FindRootItemBytenr(rootItems, ExtentTreeObjectId)
      ?? throw new InvalidDataException("Btrfs in-place add: EXTENT_TREE ROOT_ITEM missing.");

    var fsPhys = chunkMap.ToPhysical(fsRootLogical);
    if (fsPhys < 0) throw new InvalidDataException("Btrfs in-place add: FS tree unreachable.");
    if (image[fsPhys + 100] != 0)
      throw new NotSupportedException("Btrfs in-place add: multi-leaf FS tree (internal root node) not handled — use rebuild.");

    var fsItems = ReadLeafItems(image, fsPhys);

    var extentPhys = chunkMap.ToPhysical(extentRootLogical);
    if (extentPhys < 0) throw new InvalidDataException("Btrfs in-place add: extent tree unreachable.");
    if (image[extentPhys + 100] != 0)
      throw new NotSupportedException("Btrfs in-place add: multi-level extent tree not handled — use rebuild.");

    var extentItems = ReadLeafItems(image, extentPhys);

    // Replace-by-name: drop any prior root-directory entry with this name plus
    // its inode/extent items. Keeps add-or-replace semantics aligned with the
    // rebuild modifier without inventing duplicate links.
    RemoveExistingRootFile(fsItems, name);

    var nextGen = sb.Generation + 1;
    var newObjectId = NextObjectId(fsItems);
    var nameBytes = Encoding.UTF8.GetBytes(name);

    // ── Insert the new file's FS-tree items ─────────────────────────────────
    var dirIndex = NextDirIndex(fsItems, FirstFreeObjectId);

    // Parent (root dir) links.
    var dirEntry = BuildDirItemValue(newObjectId, nameBytes, isDir: false);
    fsItems.Add(new Item(FirstFreeObjectId, DirIndex, dirIndex, dirEntry));
    fsItems.Add(new Item(FirstFreeObjectId, DirItem, BtrfsNameHash(nameBytes), dirEntry));

    // Child back-ref + inode + inline extent.
    var inodeRef = BuildInodeRef(dirIndex, nameBytes);
    fsItems.Add(new Item(newObjectId, InodeRef, FirstFreeObjectId, inodeRef));

    var fileInode = BuildInodeItem(mode: 0x81A4 /* S_IFREG|0644 */, size: data.Length,
      bytes: data.Length, nlink: 1, gen: nextGen);
    fsItems.Add(new Item(newObjectId, InodeItem, 0, fileInode));

    var inlineExtent = BuildInlineExtentData(data, nextGen);
    fsItems.Add(new Item(newObjectId, ExtentData, 0, inlineExtent));

    // Grow the root directory inode's size by name_len*2 (DIR_ITEM + DIR_INDEX).
    GrowDirectorySize(fsItems, FirstFreeObjectId, nameBytes.Length * 2, nextGen);

    SortItems(fsItems);
    EnsureFits(fsItems, "FS tree");

    // ── Allocate CoW destination blocks from free metadata space ────────────
    var occupied = new HashSet<long> {
      sb.RootTreeLogical, fsRootLogical, extentRootLogical,
    };
    AddAllMetadataBlockBytenrs(extentItems, occupied);

    var (metaStart, metaLen) = chunkMap.MetadataChunk
      ?? throw new InvalidDataException("Btrfs in-place add: metadata chunk not found.");

    var newFsLogical = AllocateMetadataBlock(occupied, metaStart, metaLen);
    occupied.Add(newFsLogical);
    var newExtentLogical = AllocateMetadataBlock(occupied, metaStart, metaLen);
    occupied.Add(newExtentLogical);
    var newRootLogical = AllocateMetadataBlock(occupied, metaStart, metaLen);
    occupied.Add(newRootLogical);

    // ── CoW the extent tree: repoint the three moved tree blocks ────────────
    RepointTreeBlockExtent(extentItems, sb.RootTreeLogical, newRootLogical, RootTreeObjectId);
    RepointTreeBlockExtent(extentItems, fsRootLogical, newFsLogical, FsTreeObjectId);
    RepointTreeBlockExtent(extentItems, extentRootLogical, newExtentLogical, ExtentTreeObjectId);
    SortItems(extentItems);
    EnsureFits(extentItems, "extent tree");

    // ── CoW the root tree: repoint FS_TREE + EXTENT_TREE ROOT_ITEMs ──────────
    RepointRootItem(rootItems, FsTreeObjectId, newFsLogical, nextGen);
    RepointRootItem(rootItems, ExtentTreeObjectId, newExtentLogical, nextGen);
    SortItems(rootItems);
    EnsureFits(rootItems, "root tree");

    // ── Serialise the three new blocks at their freshly allocated offsets ────
    var newFsPhys = chunkMap.ToPhysical(newFsLogical);
    var newExtentPhys = chunkMap.ToPhysical(newExtentLogical);
    var newRootPhys = chunkMap.ToPhysical(newRootLogical);

    WriteLeaf(image, (int)newFsPhys, newFsLogical, FsTreeObjectId, nextGen, sb, fsItems);
    WriteLeaf(image, (int)newExtentPhys, newExtentLogical, ExtentTreeObjectId, nextGen, sb, extentItems);
    WriteLeaf(image, (int)newRootPhys, newRootLogical, RootTreeObjectId, nextGen, sb, rootItems);

    // Free the old blocks (zero them so stale tree data never confuses a reader
    // that scans by signature; they are no longer referenced by any tree).
    image.AsSpan((int)rootPhys, NodeSize).Clear();
    image.AsSpan((int)fsPhys, NodeSize).Clear();
    image.AsSpan((int)extentPhys, NodeSize).Clear();

    // ── Update + re-checksum the superblock ─────────────────────────────────
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(SbOffset + 0x48), nextGen);     // generation
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(SbOffset + 0x50), newRootLogical); // root
    // chunk_root / chunk_root_generation are NOT touched: the chunk tree is not
    // CoW'd by an in-place file add, so it keeps its original generation. Bumping
    // chunk_root_generation here would make btrfs check expect a gen-N chunk root
    // and report a parent transid mismatch on the (unchanged) chunk tree block.
    WriteBlockChecksum(image, SbOffset, SectorSize);
  }

  // ── FS-tree edits ─────────────────────────────────────────────────────────

  private static void RemoveExistingRootFile(List<Item> fsItems, string name) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var nameHash = BtrfsNameHash(nameBytes);

    // Find the DIR_ITEM for (256, DIR_ITEM, name_hash) matching this name.
    long? childInode = null;
    long shrinkBy = 0;
    Item? dirItem = null, dirIndex = null;
    foreach (var it in fsItems) {
      if (it.ObjectId != FirstFreeObjectId) continue;
      if (it.Type == DirItem && it.Offset == nameHash && DirItemNameMatches(it.Data, nameBytes)) {
        dirItem = it;
        childInode = BinaryPrimitives.ReadInt64LittleEndian(it.Data); // location key objectid
      }
    }
    if (childInode == null) return; // new file

    // Find the matching DIR_INDEX (same child inode + name).
    foreach (var it in fsItems) {
      if (it.ObjectId != FirstFreeObjectId || it.Type != DirIndex) continue;
      if (DirItemNameMatches(it.Data, nameBytes)
          && BinaryPrimitives.ReadInt64LittleEndian(it.Data) == childInode.Value) {
        dirIndex = it;
        break;
      }
    }

    fsItems.RemoveAll(it =>
      ReferenceEquals(it, dirItem) || ReferenceEquals(it, dirIndex)
      || it.ObjectId == childInode.Value); // INODE_ITEM/INODE_REF/EXTENT_DATA of the file

    shrinkBy = nameBytes.Length * 2;
    GrowDirectorySize(fsItems, FirstFreeObjectId, -shrinkBy, 0, bumpGenOnly: false);
  }

  private static bool DirItemNameMatches(byte[] dirItemValue, byte[] nameBytes) {
    if (dirItemValue.Length < 30) return false;
    var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(dirItemValue.AsSpan(27));
    if (nameLen != nameBytes.Length || 30 + nameLen > dirItemValue.Length) return false;
    return dirItemValue.AsSpan(30, nameLen).SequenceEqual(nameBytes);
  }

  private static long NextObjectId(List<Item> fsItems) {
    var max = FirstFreeObjectId;
    foreach (var it in fsItems)
      if (it.Type == InodeItem && it.ObjectId > max) max = it.ObjectId;
    return max + 1;
  }

  private static long NextDirIndex(List<Item> fsItems, long dirObjectId) {
    long max = 1; // 0/1 reserved for "." / ".."
    foreach (var it in fsItems)
      if (it.ObjectId == dirObjectId && it.Type == DirIndex && it.Offset > max) max = it.Offset;
    return max + 1;
  }

  // Adjusts a directory inode's i_size by delta and (optionally) bumps its
  // generation/transid to the new transaction.
  private static void GrowDirectorySize(List<Item> fsItems, long dirObjectId, long delta, long gen, bool bumpGenOnly = false) {
    foreach (var it in fsItems) {
      if (it.ObjectId != dirObjectId || it.Type != InodeItem) continue;
      var size = BinaryPrimitives.ReadInt64LittleEndian(it.Data.AsSpan(16));
      BinaryPrimitives.WriteInt64LittleEndian(it.Data.AsSpan(16), size + delta);
      if (gen > 0) {
        BinaryPrimitives.WriteInt64LittleEndian(it.Data.AsSpan(0), gen);  // generation
        BinaryPrimitives.WriteInt64LittleEndian(it.Data.AsSpan(8), gen);  // transid
      }
      _ = bumpGenOnly;
      return;
    }
  }

  // ── Extent-tree / root-tree edits ──────────────────────────────────────────

  private static void RepointTreeBlockExtent(List<Item> extentItems, long oldBytenr, long newBytenr, long ownerRoot) {
    foreach (var it in extentItems) {
      if (it.Type == ExtentItemType && it.ObjectId == oldBytenr && it.Offset == NodeSize) {
        // The EXTENT_ITEM key encodes the bytenr in ObjectId; move the key and
        // keep the (already correct) TREE_BLOCK_REF root in the value. The
        // value's tree_block_info.level and inline backref are unchanged.
        it.ObjectId = newBytenr;
        _ = ownerRoot;
        return;
      }
    }
    throw new InvalidDataException(
      $"Btrfs in-place add: tree-block EXTENT_ITEM for bytenr {oldBytenr} not found.");
  }

  private static void RepointRootItem(List<Item> rootItems, long treeObjectId, long newBytenr, long gen) {
    foreach (var it in rootItems) {
      if (it.Type == RootItem && it.ObjectId == treeObjectId) {
        // ROOT_ITEM: generation@160, bytenr@176, byte_limit@184, bytes_used@192.
        BinaryPrimitives.WriteInt64LittleEndian(it.Data.AsSpan(160), gen);
        BinaryPrimitives.WriteInt64LittleEndian(it.Data.AsSpan(176), newBytenr);
        return;
      }
    }
    throw new InvalidDataException(
      $"Btrfs in-place add: ROOT_ITEM for tree {treeObjectId} not found.");
  }

  private static long? FindRootItemBytenr(List<Item> rootItems, long treeObjectId) {
    foreach (var it in rootItems)
      if (it.Type == RootItem && it.ObjectId == treeObjectId && it.Data.Length >= 184)
        return BinaryPrimitives.ReadInt64LittleEndian(it.Data.AsSpan(176));
    return null;
  }

  // ── Block allocation in the metadata chunk ──────────────────────────────────

  private static void AddAllMetadataBlockBytenrs(List<Item> extentItems, HashSet<long> occupied) {
    // Every TREE_BLOCK EXTENT_ITEM names an allocated node by its bytenr (key
    // objectid). Treat them all as occupied so the CoW destinations never land
    // on a live block.
    foreach (var it in extentItems)
      if (it.Type == ExtentItemType && it.Offset == NodeSize)
        occupied.Add(it.ObjectId);
  }

  private static long AllocateMetadataBlock(HashSet<long> occupied, long metaStart, long metaLen) {
    for (var off = metaStart; off + NodeSize <= metaStart + metaLen; off += NodeSize)
      if (!occupied.Contains(off)) return off;
    throw new NotSupportedException(
      "Btrfs in-place add: no free metadata block for CoW (chunk full) — use rebuild.");
  }

  // ── Leaf (de)serialisation — mirrors BtrfsWriter.WriteLeafNode ──────────────

  private sealed class Item {
    public long ObjectId;
    public byte Type;
    public long Offset;
    public byte[] Data;
    public Item(long objectId, byte type, long offset, byte[] data) {
      this.ObjectId = objectId; this.Type = type; this.Offset = offset; this.Data = data;
    }
  }

  private static List<Item> ReadLeafItems(byte[] image, long phys) {
    var off = (int)phys;
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 96));
    var items = new List<Item>((int)nritems);
    for (uint i = 0; i < nritems; i++) {
      var itemOff = off + HeaderSize + (int)i * LeafItemHeader;
      var objId = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(itemOff));
      var type = image[itemOff + 8];
      var keyOffset = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(itemOff + 9));
      var dataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(itemOff + 17));
      var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(itemOff + 21));
      var data = image.AsSpan(off + HeaderSize + dataOffset, dataSize).ToArray();
      items.Add(new Item(objId, type, keyOffset, data));
    }
    return items;
  }

  private static void EnsureFits(List<Item> items, string tree) {
    var need = 0;
    foreach (var it in items) need += LeafItemHeader + it.Data.Length;
    if (need > NodeSize - HeaderSize)
      throw new NotSupportedException(
        $"Btrfs in-place add: {tree} leaf would overflow a single node ({need} > {NodeSize - HeaderSize}) — use rebuild.");
  }

  private static void WriteLeaf(byte[] image, int nodeOff, long bytenr, long ownerObjectId, long gen,
      Superblock sb, List<Item> items) {
    image.AsSpan(nodeOff, NodeSize).Clear();
    sb.FsUuid.CopyTo(image.AsSpan(nodeOff + 32));
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 48), bytenr);
    const long WrittenFlag = 1L;
    const long MixedBackrefRev = 1L << 56;
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 56), WrittenFlag | MixedBackrefRev);
    sb.ChunkTreeUuid.CopyTo(image.AsSpan(nodeOff + 64));
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 80), gen);
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 88), ownerObjectId);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(nodeOff + 96), (uint)items.Count);
    image[nodeOff + 100] = 0; // level 0

    var dataEnd = NodeSize;
    for (var i = 0; i < items.Count; i++) {
      var it = items[i];
      dataEnd -= it.Data.Length;
      var dataOffsetInItems = dataEnd - HeaderSize;
      var itemOff = nodeOff + HeaderSize + i * LeafItemHeader;
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(itemOff), it.ObjectId);
      image[itemOff + 8] = it.Type;
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(itemOff + 9), it.Offset);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(itemOff + 17), (uint)dataOffsetInItems);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(itemOff + 21), (uint)it.Data.Length);
      it.Data.CopyTo(image, nodeOff + HeaderSize + dataOffsetInItems);
    }

    WriteBlockChecksum(image, nodeOff, NodeSize);
  }

  private static void SortItems(List<Item> items) {
    items.Sort((a, b) => {
      var c = a.ObjectId.CompareTo(b.ObjectId);
      if (c != 0) return c;
      c = a.Type.CompareTo(b.Type);
      if (c != 0) return c;
      return a.Offset.CompareTo(b.Offset);
    });
  }

  // ── Item builders — byte-identical to BtrfsWriter ───────────────────────────

  private static byte[] BuildInodeItem(uint mode, long size, long bytes, uint nlink, long gen) {
    var d = new byte[160];
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(0), gen);   // generation
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(8), gen);   // transid
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(16), size);
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(24), bytes);
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(40), nlink);
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(52), mode);
    return d;
  }

  private static byte[] BuildInodeRef(long index, byte[] nameBytes) {
    var r = new byte[10 + nameBytes.Length];
    BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(0), index);
    BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(8), (ushort)nameBytes.Length);
    nameBytes.CopyTo(r, 10);
    return r;
  }

  private static byte[] BuildInlineExtentData(byte[] data, long gen) {
    var e = new byte[21 + data.Length];
    BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(0), gen);          // generation
    BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(8), data.Length);  // ram_bytes
    e[16] = 0; // compression none
    e[20] = 0; // type inline
    data.CopyTo(e, 21);
    return e;
  }

  private static byte[] BuildDirItemValue(long childInode, byte[] nameBytes, bool isDir) {
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

  private static long BtrfsNameHash(byte[] data) {
    const uint poly = 0x82F63B78u;
    var crc = 0xFFFFFFFEu;
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; i++)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : (crc >> 1);
    }
    return crc;
  }

  private static void WriteBlockChecksum(byte[] image, int blockOff, int blockSize) {
    var payload = image.AsSpan(blockOff + 32, blockSize - 32);
    var crc = Crc32.Compute(payload, Crc32.Castagnoli);
    image.AsSpan(blockOff, 32).Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(blockOff, 4), crc);
  }

  // ── Superblock + chunk map ──────────────────────────────────────────────────

  private sealed class Superblock {
    public long RootTreeLogical;
    public long ChunkTreeLogical;
    public long Generation;
    public uint NodeSize;
    public uint SectorSize;
    public int SysChunkArraySize;
    public byte[] FsUuid = new byte[16];
    public byte[] ChunkTreeUuid = new byte[16];

    public Superblock(byte[] image) {
      this.Generation = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(SbOffset + 0x48));
      this.RootTreeLogical = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(SbOffset + 0x50));
      this.ChunkTreeLogical = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(SbOffset + 0x58));
      this.SectorSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(SbOffset + 0x90));
      this.NodeSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(SbOffset + 0x94));
      this.SysChunkArraySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(SbOffset + 0xA0));
      image.AsSpan(SbOffset + 0x20, 16).CopyTo(this.FsUuid);
      // A node's chunk_tree_uuid mirrors the fs uuid in BtrfsWriter output; read
      // it from an existing node header so any future divergence is honoured.
      image.AsSpan(SbOffset + 0x20, 16).CopyTo(this.ChunkTreeUuid);
    }
  }

  // Logical→physical translation built from sys_chunk_array + chunk tree, plus
  // the bounds of the METADATA chunk used to allocate CoW destination blocks.
  private sealed class ChunkMap {
    private readonly List<(long logical, long physical, long length)> _map = [];
    public (long Start, long Length)? MetadataChunk { get; private set; }

    public long ToPhysical(long logical) {
      foreach (var (l, p, len) in this._map)
        if (logical >= l && logical < l + len) return p + (logical - l);
      return -1;
    }

    public static ChunkMap Build(byte[] image, Superblock sb) {
      var cm = new ChunkMap();
      cm.ParseSysChunkArray(image, SbOffset + 0x32B, sb.SysChunkArraySize);
      var chunkPhys = cm.ToPhysical(sb.ChunkTreeLogical);
      if (chunkPhys >= 0) cm.ReadChunkLeaf(image, (int)chunkPhys);
      return cm;
    }

    private void ParseSysChunkArray(byte[] image, int offset, int size) {
      var end = Math.Min(offset + size, image.Length);
      var pos = offset;
      while (pos + 17 + 48 <= end) {
        var logical = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(pos + 9));
        pos += 17;
        var length = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(pos));
        var type = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(pos + 24));
        var numStripes = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pos + 44));
        pos += 48;
        if (numStripes > 0 && pos + 32 <= end) {
          var physical = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(pos + 8));
          this.Record(logical, physical, length, type);
        }
        pos += numStripes * 32;
      }
    }

    private void ReadChunkLeaf(byte[] image, int off) {
      if (image[off + 100] != 0) return; // chunk tree is a single leaf in writer output
      var nritems = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 96));
      for (uint i = 0; i < nritems; i++) {
        var itemOff = off + HeaderSize + (int)i * LeafItemHeader;
        var type = image[itemOff + 8];
        if (type != 228 /* CHUNK_ITEM */) continue;
        var keyOffset = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(itemOff + 9));
        var dataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(itemOff + 17));
        var dataPos = off + HeaderSize + dataOffset;
        var length = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(dataPos));
        var chunkType = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(dataPos + 24));
        var numStripes = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(dataPos + 44));
        if (numStripes > 0) {
          var physical = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(dataPos + 48 + 8));
          if (this.ToPhysical(keyOffset) < 0) this.Record(keyOffset, physical, length, chunkType);
        }
      }
    }

    private void Record(long logical, long physical, long length, ulong type) {
      this._map.Add((logical, physical, length));
      const ulong BlockGroupMetadata = 0x04;
      if ((type & BlockGroupMetadata) != 0) this.MetadataChunk = (logical, length);
    }
  }
}
