#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Btrfs;

/// <summary>
/// Genuine copy-on-write in-place add for Btrfs images produced by
/// <see cref="BtrfsWriter"/> — the spec-faithful alternative to the whole-image
/// rebuild in <see cref="BtrfsModifier"/>. A file is added (or replaced) by
/// writing NEW (CoW) tree blocks only for the path that changed and leaving
/// every untouched node and every existing data extent byte-identical at its
/// original offset. The full add pipeline:
/// <list type="number">
///   <item>The whole FS tree is read into one flat item list — both the
///   single-leaf shape and the level-1 internal-node-over-N-leaves shape.</item>
///   <item>The target parent directory is resolved, creating any missing
///   intermediate directory inodes (<c>INODE_ITEM</c>/<c>INODE_REF</c>/parent
///   links) for nested targets.</item>
///   <item>The new file's items are inserted. Files below one sector stay inline
///   in the FS-tree leaf; files at/above one sector get a real data extent
///   allocated from the DATA chunk's free space, the payload written there, a
///   regular <c>EXTENT_DATA</c> item, a data <c>EXTENT_ITEM</c> (with inline
///   <c>EXTENT_DATA_REF</c>) in the extent tree, and per-sector CRC-32C
///   <c>EXTENT_CSUM</c> items in the csum tree.</item>
///   <item>The flat FS-tree item set is re-sorted and re-packed into leaves; a
///   leaf that would overflow the node splits, and an internal index node is
///   (re)built above the leaves when more than one results.</item>
///   <item>Every CoW'd metadata block (each FS leaf, the FS internal node, the
///   extent / csum / root leaves) is allocated — preferring genuinely-free node
///   slots, then recycling the blocks this operation frees. The extent tree's
///   tree-block <c>EXTENT_ITEM</c>s are rewritten to match, block-group
///   accounting and the superblock <c>bytes_used</c> are recomputed.</item>
///   <item>The <c>FS_TREE</c> / <c>EXTENT_TREE</c> / <c>CSUM_TREE</c>
///   <c>ROOT_ITEM</c>s are repointed and the superblock <c>root</c> +
///   <c>generation</c> bumped; CRC-32C is recomputed for every new block and the
///   superblock.</item>
/// </list>
/// <para>
/// Verified byte-for-byte against <c>btrfs check</c> (incl.
/// <c>--check-data-csum</c>) for: inline and regular (data-extent) files, nested
/// sub-directory targets, multi-leaf FS trees (internal root node), leaf splits,
/// and add-or-replace of existing inline/regular files. Cases still throwing
/// <see cref="NotSupportedException"/> for the rebuild fallback: non-default
/// node/sector sizes, a multi-level root/extent/csum tree, an FS tree deeper
/// than one internal node, a full metadata or DATA chunk.
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
  private const byte ExtentDataRef = 178;
  private const byte ExtentCsumType = 128;

  // Extent-item flags (fs/btrfs/ctree.h).
  private const ulong ExtentFlagData = 0x01;

  // BTRFS_INODE_NODATASUM (linux/btrfs_tree.h): the file's data carries no
  // checksums in the CSUM_TREE. Mirrors BtrfsWriter for regular extents.
  private const ulong InodeNoDataSum = 1UL << 0;

  // Well-known object IDs.
  private const long RootTreeObjectId = 1;
  private const long ExtentTreeObjectId = 2;
  private const long FsTreeObjectId = 5;
  private const long CsumTreeObjectId = 7;
  private const long ExtentCsumObjectId = -10; // BTRFS_EXTENT_CSUM_OBJECTID
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
    var csumRootLogical = FindRootItemBytenr(rootItems, CsumTreeObjectId)
      ?? throw new InvalidDataException("Btrfs in-place add: CSUM_TREE ROOT_ITEM missing.");

    // The FS tree may be a single leaf (level 0) or an internal root node over
    // N leaves (level 1). Read every leaf into one flat item list and remember
    // which physical blocks the tree currently occupies so they can be freed.
    var fsPhys = chunkMap.ToPhysical(fsRootLogical);
    if (fsPhys < 0) throw new InvalidDataException("Btrfs in-place add: FS tree unreachable.");
    var fsRootLevel = image[fsPhys + 100];
    if (fsRootLevel > 1)
      throw new NotSupportedException("Btrfs in-place add: FS tree deeper than one internal node — use rebuild.");

    var oldFsBlocks = new List<long>();
    var fsItems = ReadFsTree(image, chunkMap, fsRootLogical, oldFsBlocks);

    var extentPhys = chunkMap.ToPhysical(extentRootLogical);
    if (extentPhys < 0) throw new InvalidDataException("Btrfs in-place add: extent tree unreachable.");
    if (image[extentPhys + 100] != 0)
      throw new NotSupportedException("Btrfs in-place add: multi-level extent tree not handled — use rebuild.");

    var extentItems = ReadLeafItems(image, extentPhys);

    var csumPhys = chunkMap.ToPhysical(csumRootLogical);
    if (csumPhys < 0) throw new InvalidDataException("Btrfs in-place add: csum tree unreachable.");
    if (image[csumPhys + 100] != 0)
      throw new NotSupportedException("Btrfs in-place add: multi-level csum tree not handled — use rebuild.");

    var csumItems = ReadLeafItems(image, csumPhys);

    // Normalise the requested path: split into directory components + leaf name.
    var pathParts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (pathParts.Length == 0)
      throw new InvalidDataException("Btrfs in-place add: empty file name.");
    var fileName = pathParts[^1];
    var dirParts = pathParts[..^1];

    // Replace-by-name within the resolved parent directory: drop any prior entry
    // with this name plus its inode / extent / csum items.
    var parentObjectId = ResolveOrCreateParent(fsItems, dirParts, ref name);
    RemoveExistingFile(fsItems, extentItems, csumItems, parentObjectId, fileName);

    var nextGen = sb.Generation + 1;
    var newObjectId = NextObjectId(fsItems);
    var nameBytes = Encoding.UTF8.GetBytes(fileName);

    // ── Insert the new file's FS-tree items ─────────────────────────────────
    var dirIndex = NextDirIndex(fsItems, parentObjectId);

    var dirEntry = BuildDirItemValue(newObjectId, nameBytes, isDir: false);
    fsItems.Add(new Item(parentObjectId, DirIndex, dirIndex, dirEntry));
    fsItems.Add(new Item(parentObjectId, DirItem, BtrfsNameHash(nameBytes), dirEntry));

    var inodeRef = BuildInodeRef(dirIndex, nameBytes);
    fsItems.Add(new Item(newObjectId, InodeRef, parentObjectId, inodeRef));

    if (data.Length < MaxInlineDataSize) {
      // Inline file: payload lives in the FS-tree leaf; no data extent.
      var fileInode = BuildInodeItem(mode: 0x81A4 /* S_IFREG|0644 */, size: data.Length,
        bytes: data.Length, nlink: 1, gen: nextGen);
      fsItems.Add(new Item(newObjectId, InodeItem, 0, fileInode));
      fsItems.Add(new Item(newObjectId, ExtentData, 0, BuildInlineExtentData(data, nextGen)));
    } else {
      // Regular (non-inline) file: allocate a real data extent in the DATA chunk
      // and add the matching extent-tree EXTENT_ITEM + per-sector csum items.
      var aligned = RoundUpToSector(data.Length);
      var dataExtentBytenr = AllocateDataExtent(image, chunkMap, extentItems, aligned);

      // flags=0 (NOT NODATASUM): unlike BtrfsWriter — which marks regular files
      // NODATASUM and leaves the csum tree empty — the in-place adder writes
      // genuine per-sector CRC-32C EXTENT_CSUM items, so the inode must declare
      // that its data is checksummed.
      var fileInode = BuildInodeItem(mode: 0x81A4, size: data.Length,
        bytes: aligned, nlink: 1, gen: nextGen, flags: 0);
      fsItems.Add(new Item(newObjectId, InodeItem, 0, fileInode));
      fsItems.Add(new Item(newObjectId, ExtentData,
        0, BuildRegularExtentData(dataExtentBytenr, aligned, data.Length, nextGen)));

      // Write the payload into its DATA-chunk slot and append the csum items.
      var dataPhys = chunkMap.ToPhysical(dataExtentBytenr);
      if (dataPhys < 0) throw new InvalidDataException("Btrfs in-place add: data extent unreachable.");
      image.AsSpan((int)dataPhys, (int)aligned).Clear();
      data.CopyTo(image.AsSpan((int)dataPhys));
      AddCsumItems(csumItems, dataExtentBytenr, image, (int)dataPhys, (int)aligned);

      // The new data EXTENT_ITEM names this inode as its owner.
      AddDataExtentItem(extentItems, dataExtentBytenr, aligned, newObjectId);
    }

    // Grow the parent directory inode's size by name_len*2 (DIR_ITEM + DIR_INDEX).
    GrowDirectorySize(fsItems, parentObjectId, nameBytes.Length * 2, nextGen);

    SortItems(fsItems);

    // ── Pack the FS tree into leaves (split when a leaf overflows) ───────────
    var fsLeaves = PackIntoLeaves(fsItems);

    // ── Plan metadata-block allocation ──────────────────────────────────────
    // CoW frees every old FS-tree block plus the old extent/root/csum leaves and
    // allocates fresh blocks for: each new FS leaf, an FS internal node (when >1
    // leaf), the new extent leaf, the new root leaf, and the new csum leaf.
    //
    // The allocator first hands out genuinely-free node slots (true CoW — the old
    // tree stays intact until the superblock flips). When those run out it
    // recycles the blocks this operation is about to free: their contents are
    // already loaded into memory, so reusing their offsets keeps the final image
    // consistent. Untouched metadata (chunk/dev trees) and every data extent are
    // never in the recycle pool, so they remain byte-identical.
    var liveBlocks = new HashSet<long>();
    AddAllMetadataBlockBytenrs(extentItems, liveBlocks);
    liveBlocks.Add(sb.RootTreeLogical);

    var freed = new List<long>();
    foreach (var b in oldFsBlocks) freed.Add(b);
    freed.Add(extentRootLogical);
    freed.Add(csumRootLogical);
    freed.Add(sb.RootTreeLogical);

    var (metaStart, metaLen) = chunkMap.MetadataChunk
      ?? throw new InvalidDataException("Btrfs in-place add: metadata chunk not found.");

    var alloc = new MetadataAllocator(metaStart, metaLen, liveBlocks, freed);

    var newFsLeafLogical = new long[fsLeaves.Count];
    for (var i = 0; i < fsLeaves.Count; i++)
      newFsLeafLogical[i] = alloc.Next();
    var needFsInternal = fsLeaves.Count > 1;
    long newFsRootLogical = needFsInternal ? alloc.Next() : newFsLeafLogical[0];
    var newExtentLogical = alloc.Next();
    var newCsumLogical = alloc.Next();
    var newRootLogical = alloc.Next();

    // ── CoW the extent tree: drop old tree-block extents, add the new ones ───
    foreach (var b in oldFsBlocks) RemoveTreeBlockExtent(extentItems, b);
    RemoveTreeBlockExtent(extentItems, extentRootLogical);
    RemoveTreeBlockExtent(extentItems, csumRootLogical);
    RemoveTreeBlockExtent(extentItems, sb.RootTreeLogical);

    var fsLeafLevel = (byte)0;
    var fsRootNewLevel = (byte)(needFsInternal ? 1 : 0);
    for (var i = 0; i < fsLeaves.Count; i++)
      AddTreeBlockExtent(extentItems, newFsLeafLogical[i], FsTreeObjectId,
        needFsInternal ? fsLeafLevel : fsRootNewLevel);
    if (needFsInternal)
      AddTreeBlockExtent(extentItems, newFsRootLogical, FsTreeObjectId, 1);
    AddTreeBlockExtent(extentItems, newExtentLogical, ExtentTreeObjectId, 0);
    AddTreeBlockExtent(extentItems, newCsumLogical, CsumTreeObjectId, 0);
    AddTreeBlockExtent(extentItems, newRootLogical, RootTreeObjectId, 0);

    // Recompute block-group accounting for both the metadata and data chunks.
    RecomputeBlockGroups(extentItems, chunkMap);

    SortItems(extentItems);
    EnsureFits(extentItems, "extent tree");

    SortItems(csumItems);
    EnsureFits(csumItems, "csum tree");

    // ── CoW the root tree: repoint FS / EXTENT / CSUM ROOT_ITEMs ─────────────
    RepointRootItem(rootItems, FsTreeObjectId, newFsRootLogical, nextGen, fsRootNewLevel);
    RepointRootItem(rootItems, ExtentTreeObjectId, newExtentLogical, nextGen, 0);
    RepointRootItem(rootItems, CsumTreeObjectId, newCsumLogical, nextGen, 0);
    SortItems(rootItems);
    EnsureFits(rootItems, "root tree");

    // ── Serialise the new blocks at their freshly allocated offsets ──────────
    if (needFsInternal) {
      var keyPtrs = new List<(long objId, byte type, long offset, long blockPtr)>();
      for (var i = 0; i < fsLeaves.Count; i++) {
        var leaf = fsLeaves[i];
        WriteLeaf(image, (int)chunkMap.ToPhysical(newFsLeafLogical[i]), newFsLeafLogical[i],
          FsTreeObjectId, nextGen, sb, leaf);
        var first = leaf[0];
        keyPtrs.Add((first.ObjectId, first.Type, first.Offset, newFsLeafLogical[i]));
      }
      WriteInternalNode(image, (int)chunkMap.ToPhysical(newFsRootLogical), newFsRootLogical,
        FsTreeObjectId, 1, nextGen, sb, keyPtrs);
    } else {
      WriteLeaf(image, (int)chunkMap.ToPhysical(newFsLeafLogical[0]), newFsLeafLogical[0],
        FsTreeObjectId, nextGen, sb, fsLeaves[0]);
    }

    WriteLeaf(image, (int)chunkMap.ToPhysical(newExtentLogical), newExtentLogical,
      ExtentTreeObjectId, nextGen, sb, extentItems);
    WriteLeaf(image, (int)chunkMap.ToPhysical(newCsumLogical), newCsumLogical,
      CsumTreeObjectId, nextGen, sb, csumItems);
    WriteLeaf(image, (int)chunkMap.ToPhysical(newRootLogical), newRootLogical,
      RootTreeObjectId, nextGen, sb, rootItems);

    // Free the old blocks (zero them so stale tree data never confuses a reader
    // that scans by signature; they are no longer referenced by any tree). Skip
    // any block the allocator recycled into a new node — those already hold
    // freshly serialised, checksummed content.
    void FreeOldBlock(long logical, long phys) {
      if (alloc.WasHandedOut(logical)) return;
      image.AsSpan((int)phys, NodeSize).Clear();
    }
    foreach (var b in oldFsBlocks) FreeOldBlock(b, chunkMap.ToPhysical(b));
    FreeOldBlock(sb.RootTreeLogical, rootPhys);
    FreeOldBlock(extentRootLogical, extentPhys);
    FreeOldBlock(csumRootLogical, csumPhys);

    // ── Update + re-checksum the superblock ─────────────────────────────────
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(SbOffset + 0x48), nextGen);     // generation
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(SbOffset + 0x50), newRootLogical); // root
    UpdateSuperblockBytesUsed(image, extentItems);
    // chunk_root / chunk_root_generation are NOT touched: the chunk tree is not
    // CoW'd by an in-place file add, so it keeps its original generation.
    WriteBlockChecksum(image, SbOffset, SectorSize);
  }

  // ── FS-tree edits ─────────────────────────────────────────────────────────

  // Reads every leaf of the FS tree into a flat item list. When the root block
  // is an internal node (level 1), every child leaf is read in key order; the
  // physical bytenr of each block visited (root + leaves) is appended to
  // <paramref name="blocks"/> so the caller can free them.
  private static List<Item> ReadFsTree(byte[] image, ChunkMap chunkMap, long rootLogical, List<long> blocks) {
    var rootPhys = chunkMap.ToPhysical(rootLogical);
    if (rootPhys < 0) throw new InvalidDataException("Btrfs in-place add: FS root unreachable.");
    blocks.Add(rootLogical);
    if (image[rootPhys + 100] == 0)
      return ReadLeafItems(image, rootPhys); // single-leaf tree

    // Internal node: walk each key pointer to its leaf.
    var items = new List<Item>();
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)rootPhys + 96));
    for (uint i = 0; i < nritems; i++) {
      var p = (int)rootPhys + HeaderSize + (int)i * 33;
      var childLogical = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(p + 17));
      var childPhys = chunkMap.ToPhysical(childLogical);
      if (childPhys < 0) throw new InvalidDataException("Btrfs in-place add: FS leaf unreachable.");
      if (image[childPhys + 100] != 0)
        throw new NotSupportedException("Btrfs in-place add: FS tree deeper than one internal node — use rebuild.");
      blocks.Add(childLogical);
      items.AddRange(ReadLeafItems(image, childPhys));
    }
    return items;
  }

  // Resolves the directory inode the new file's parent should live in, creating
  // any missing intermediate directories (INODE_ITEM + back-ref + parent links).
  // Returns BTRFS_FIRST_FREE_OBJECTID for a root-directory target.
  private static long ResolveOrCreateParent(List<Item> fsItems, string[] dirParts, ref string name) {
    var parent = FirstFreeObjectId;
    foreach (var component in dirParts) {
      var compBytes = Encoding.UTF8.GetBytes(component);
      var hash = BtrfsNameHash(compBytes);
      long? childInode = null;
      var childIsDir = false;
      foreach (var it in fsItems) {
        if (it.ObjectId != parent || it.Type != DirItem || it.Offset != hash) continue;
        if (!DirItemNameMatches(it.Data, compBytes)) continue;
        childInode = BinaryPrimitives.ReadInt64LittleEndian(it.Data);
        childIsDir = it.Data.Length >= 30 && it.Data[29] == 2;
        break;
      }
      if (childInode != null) {
        if (!childIsDir)
          throw new InvalidDataException($"Btrfs in-place add: path component '{component}' is a file, not a directory.");
        parent = childInode.Value;
        continue;
      }

      // Create the directory inode.
      var newDirId = NextObjectId(fsItems);
      var idx = NextDirIndex(fsItems, parent);
      var entry = BuildDirItemValue(newDirId, compBytes, isDir: true);
      fsItems.Add(new Item(parent, DirIndex, idx, entry));
      fsItems.Add(new Item(parent, DirItem, hash, entry));
      fsItems.Add(new Item(newDirId, InodeRef, parent, BuildInodeRef(idx, compBytes)));
      // A freshly created directory starts empty (size 0, nlink 1 for ".").
      fsItems.Add(new Item(newDirId, InodeItem, 0,
        BuildInodeItem(mode: 0x41ED /* S_IFDIR|0755 */, size: 0, bytes: 0, nlink: 1, gen: 1)));
      // The parent gains name_len*2 for the new directory link.
      GrowDirectorySize(fsItems, parent, compBytes.Length * 2, 0);
      parent = newDirId;
    }
    _ = name;
    return parent;
  }

  // Removes any existing file named <paramref name="fileName"/> in the parent
  // directory, along with its inode / extent / csum items, so the add behaves as
  // an add-or-replace. Returns silently when no such entry exists.
  private static void RemoveExistingFile(List<Item> fsItems, List<Item> extentItems, List<Item> csumItems, long parentObjectId, string fileName) {
    var nameBytes = Encoding.UTF8.GetBytes(fileName);
    var nameHash = BtrfsNameHash(nameBytes);

    long? childInode = null;
    Item? dirItem = null, dirIndex = null;
    foreach (var it in fsItems) {
      if (it.ObjectId != parentObjectId) continue;
      if (it.Type == DirItem && it.Offset == nameHash && DirItemNameMatches(it.Data, nameBytes)) {
        dirItem = it;
        childInode = BinaryPrimitives.ReadInt64LittleEndian(it.Data);
      }
    }
    if (childInode == null) return; // new file

    foreach (var it in fsItems) {
      if (it.ObjectId != parentObjectId || it.Type != DirIndex) continue;
      if (DirItemNameMatches(it.Data, nameBytes)
          && BinaryPrimitives.ReadInt64LittleEndian(it.Data) == childInode.Value) {
        dirIndex = it;
        break;
      }
    }

    // If the replaced file owned a regular data extent, drop its data
    // EXTENT_ITEM (extent tree) and its csum items too, so the data extent is
    // freed and no orphan backref remains. The DATA-chunk bytes themselves stay
    // until the slot is reused — block-group accounting is recomputed later.
    foreach (var it in fsItems) {
      if (it.ObjectId != childInode.Value || it.Type != ExtentData) continue;
      if (it.Data.Length >= 21 && it.Data[20] == 1) { // type=regular
        var diskBytenr = BinaryPrimitives.ReadInt64LittleEndian(it.Data.AsSpan(21));
        var numBytes = BinaryPrimitives.ReadInt64LittleEndian(it.Data.AsSpan(45));
        if (diskBytenr != 0) {
          extentItems.RemoveAll(e =>
            e.Type == ExtentItemType && e.ObjectId == diskBytenr && e.Offset == numBytes);
          RemoveCsumRange(csumItems, diskBytenr, numBytes);
        }
      }
    }

    fsItems.RemoveAll(it =>
      ReferenceEquals(it, dirItem) || ReferenceEquals(it, dirIndex)
      || it.ObjectId == childInode.Value);

    GrowDirectorySize(fsItems, parentObjectId, -(nameBytes.Length * 2), 0);
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

  // Adjusts a directory inode's i_size by delta and (when gen > 0) bumps its
  // generation/transid to the new transaction.
  private static void GrowDirectorySize(List<Item> fsItems, long dirObjectId, long delta, long gen) {
    foreach (var it in fsItems) {
      if (it.ObjectId != dirObjectId || it.Type != InodeItem) continue;
      var size = BinaryPrimitives.ReadInt64LittleEndian(it.Data.AsSpan(16));
      BinaryPrimitives.WriteInt64LittleEndian(it.Data.AsSpan(16), size + delta);
      if (gen > 0) {
        BinaryPrimitives.WriteInt64LittleEndian(it.Data.AsSpan(0), gen);  // generation
        BinaryPrimitives.WriteInt64LittleEndian(it.Data.AsSpan(8), gen);  // transid
      }
      return;
    }
  }

  // ── Extent-tree / root-tree edits ──────────────────────────────────────────

  // Removes the tree-block EXTENT_ITEM (key = (bytenr, EXTENT_ITEM, NodeSize))
  // for a freed metadata node. No-op when no such item exists.
  private static void RemoveTreeBlockExtent(List<Item> extentItems, long bytenr) {
    extentItems.RemoveAll(it =>
      it.Type == ExtentItemType && it.ObjectId == bytenr && it.Offset == NodeSize);
  }

  // Adds an EXTENT_ITEM (flags=TREE_BLOCK) with one inline TREE_BLOCK_REF naming
  // the owning root, for a metadata node at <paramref name="bytenr"/>. Mirrors
  // BtrfsWriter.AddTreeBlockExtent byte-for-byte.
  private const byte TreeBlockRef = 176;
  private static void AddTreeBlockExtent(List<Item> extentItems, long bytenr, long ownerRoot, byte level) {
    var v = new byte[24 + 18 + 9];
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0), 1);                       // refs
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(8), 1);                       // generation
    const ulong ExtentFlagTreeBlock = 0x02;
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(16), ExtentFlagTreeBlock);   // flags
    v[24 + 17] = level;                                                            // tree_block_info.level
    v[24 + 18] = TreeBlockRef;                                                     // inline ref type
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(24 + 18 + 1), ownerRoot);
    extentItems.Add(new Item(bytenr, ExtentItemType, NodeSize, v));
  }

  // Adds a data EXTENT_ITEM (key = (bytenr, EXTENT_ITEM, length)) with one inline
  // EXTENT_DATA_REF naming the owning FS_TREE inode. Mirrors
  // BtrfsWriter.AddDataExtent byte-for-byte.
  private static void AddDataExtentItem(List<Item> extentItems, long bytenr, long length, long ownerInode) {
    var v = new byte[24 + 1 + 28];
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0), 1);                       // refs
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(8), 1);                       // generation
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(16), ExtentFlagData);        // flags = DATA
    v[24] = ExtentDataRef;                                                         // inline ref type
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(25), FsTreeObjectId);         // root
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(33), ownerInode);            // objectid (inode)
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(41), 0);                     // offset (file offset)
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(49), 1);                    // count (refs)
    extentItems.Add(new Item(bytenr, ExtentItemType, length, v));
  }

  private static void RepointRootItem(List<Item> rootItems, long treeObjectId, long newBytenr, long gen, byte level) {
    foreach (var it in rootItems) {
      if (it.Type == RootItem && it.ObjectId == treeObjectId) {
        // ROOT_ITEM: generation@160, bytenr@176, byte_limit@184, bytes_used@192,
        // level@238.
        BinaryPrimitives.WriteInt64LittleEndian(it.Data.AsSpan(160), gen);
        BinaryPrimitives.WriteInt64LittleEndian(it.Data.AsSpan(176), newBytenr);
        if (it.Data.Length > 238) it.Data[238] = level;
        return;
      }
    }
    throw new InvalidDataException(
      $"Btrfs in-place add: ROOT_ITEM for tree {treeObjectId} not found.");
  }

  // Recomputes BLOCK_GROUP_ITEM used counters from the current extent items.
  // Metadata block groups account NodeSize per tree-block extent that falls in
  // their range; the data block group accounts the length of every data extent.
  private static void RecomputeBlockGroups(List<Item> extentItems, ChunkMap chunkMap) {
    const byte BlockGroupItem = 192;
    foreach (var bg in extentItems) {
      if (bg.Type != BlockGroupItem) continue;
      var bgStart = bg.ObjectId;
      var bgLen = bg.Offset;
      long used = 0;
      foreach (var it in extentItems) {
        if (it.Type != ExtentItemType) continue;
        if (it.ObjectId < bgStart || it.ObjectId >= bgStart + bgLen) continue;
        used += it.Offset; // EXTENT_ITEM key offset == extent length (NodeSize or data length)
      }
      BinaryPrimitives.WriteInt64LittleEndian(bg.Data.AsSpan(0), used);
    }
    _ = chunkMap;
  }

  // Updates the superblock bytes_used to the sum of every allocated extent's
  // length (every EXTENT_ITEM offset). Both tree blocks (NodeSize) and data
  // extents (sector-aligned length) are accounted, matching what btrfs check
  // sums while walking the extent tree.
  private static void UpdateSuperblockBytesUsed(byte[] image, List<Item> extentItems) {
    long used = 0;
    foreach (var it in extentItems)
      if (it.Type == ExtentItemType)
        used += it.Offset;
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(SbOffset + 0x78), used);
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

  // Hands out node-sized metadata blocks for CoW. Genuinely-free slots come
  // first (the old tree survives until the superblock flips); when exhausted it
  // recycles the blocks this operation frees (already buffered in memory).
  private sealed class MetadataAllocator {
    private readonly long _metaStart;
    private readonly long _metaLen;
    private readonly HashSet<long> _handedOut = [];
    private readonly HashSet<long> _live;
    private readonly Queue<long> _recyclePool;

    public MetadataAllocator(long metaStart, long metaLen, HashSet<long> live, List<long> freed) {
      this._metaStart = metaStart;
      this._metaLen = metaLen;
      this._live = live;
      this._recyclePool = new Queue<long>(freed);
    }

    // True for every block this allocator has handed out — used to guard the
    // post-write "zero the freed blocks" pass so it never wipes a reused slot.
    public bool WasHandedOut(long bytenr) => this._handedOut.Contains(bytenr);

    public long Next() {
      for (var off = this._metaStart; off + NodeSize <= this._metaStart + this._metaLen; off += NodeSize) {
        if (this._live.Contains(off) || this._handedOut.Contains(off)) continue;
        if (this._recyclePool.Contains(off)) continue; // keep recycled slots for the explicit phase
        this._handedOut.Add(off);
        return off;
      }
      while (this._recyclePool.Count > 0) {
        var off = this._recyclePool.Dequeue();
        if (this._handedOut.Contains(off)) continue;
        this._handedOut.Add(off);
        return off;
      }
      throw new NotSupportedException(
        "Btrfs in-place add: no free metadata block for CoW (chunk full) — use rebuild.");
    }
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

  // Serialises an internal (index) node. Body is a packed array of key_ptr
  // entries: key(17) + blockptr(8) + generation(8) = 33 bytes. Mirrors
  // BtrfsWriter.WriteInternalNode.
  private static void WriteInternalNode(byte[] image, int nodeOff, long bytenr, long ownerObjectId,
      byte level, long gen, Superblock sb, List<(long objId, byte type, long offset, long blockPtr)> keyPtrs) {
    image.AsSpan(nodeOff, NodeSize).Clear();
    sb.FsUuid.CopyTo(image.AsSpan(nodeOff + 32));
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 48), bytenr);
    const long WrittenFlag = 1L;
    const long MixedBackrefRev = 1L << 56;
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 56), WrittenFlag | MixedBackrefRev);
    sb.ChunkTreeUuid.CopyTo(image.AsSpan(nodeOff + 64));
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 80), gen);
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(nodeOff + 88), ownerObjectId);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(nodeOff + 96), (uint)keyPtrs.Count);
    image[nodeOff + 100] = level;

    for (var i = 0; i < keyPtrs.Count; i++) {
      var (objId, type, offset, blockPtr) = keyPtrs[i];
      var p = nodeOff + HeaderSize + i * 33;
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(p), objId);
      image[p + 8] = type;
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(p + 9), offset);
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(p + 17), blockPtr);
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(p + 25), gen);
    }

    WriteBlockChecksum(image, nodeOff, NodeSize);
  }

  // Greedily slices a sorted item set into leaf-sized batches. Each leaf is
  // bounded by the node's usable space (NodeSize - HeaderSize). A single leaf
  // that still overflows after the split is a hard error (would need a deeper
  // tree than one internal node, which the in-place path does not build).
  private static List<List<Item>> PackIntoLeaves(List<Item> items) {
    var leaves = new List<List<Item>>();
    var current = new List<Item>();
    var used = 0;
    var capacity = NodeSize - HeaderSize;
    foreach (var item in items) {
      var cost = LeafItemHeader + item.Data.Length;
      if (current.Count > 0 && used + cost > capacity) {
        leaves.Add(current);
        current = [];
        used = 0;
      }
      current.Add(item);
      used += cost;
    }
    if (current.Count > 0 || leaves.Count == 0)
      leaves.Add(current);
    return leaves;
  }

  private static long RoundUpToSector(long length) {
    var rem = length % SectorSize;
    return rem == 0 ? length : length + (SectorSize - rem);
  }

  // ── Data-extent allocation in the DATA chunk ────────────────────────────────

  // Finds a free, sector-aligned run of <paramref name="length"/> bytes inside
  // the DATA chunk and returns its logical (== byte) address. Free space is the
  // DATA chunk minus every existing data EXTENT_ITEM range.
  private static long AllocateDataExtent(byte[] image, ChunkMap chunkMap, List<Item> extentItems, long length) {
    var (dataStart, dataLen) = chunkMap.DataChunk
      ?? throw new NotSupportedException("Btrfs in-place add: no DATA chunk — use rebuild.");

    // Collect occupied [start,end) ranges from existing DATA extents.
    var occupied = new List<(long start, long end)>();
    foreach (var it in extentItems) {
      if (it.Type != ExtentItemType) continue;
      if (it.ObjectId < dataStart || it.ObjectId >= dataStart + dataLen) continue;
      // A data extent's key offset is its length; flags carry DATA. Tree blocks
      // never live in the data chunk, so any extent here is a data extent.
      occupied.Add((it.ObjectId, it.ObjectId + it.Offset));
    }
    occupied.Sort((a, b) => a.start.CompareTo(b.start));

    var cursor = dataStart;
    foreach (var (s, e) in occupied) {
      if (s - cursor >= length) break; // gap before this extent is big enough
      if (e > cursor) cursor = e;
    }
    if (cursor + length > dataStart + dataLen)
      throw new NotSupportedException("Btrfs in-place add: DATA chunk full — use rebuild.");
    return cursor;
  }

  // ── CSUM tree (CRC-32C per sector) ──────────────────────────────────────────

  // Appends one EXTENT_CSUM item covering [diskBytenr, diskBytenr+length): a run
  // of 4-byte little-endian CRC-32C values, one per SectorSize block of data.
  // Key = (BTRFS_EXTENT_CSUM_OBJECTID, EXTENT_CSUM, diskBytenr). A single
  // contiguous extent produces one csum item whose value packs every sector's
  // checksum, exactly as the kernel stores them.
  private static void AddCsumItems(List<Item> csumItems, long diskBytenr, byte[] image, int dataPhys, int length) {
    var sectors = length / SectorSize;
    var v = new byte[sectors * 4];
    for (var i = 0; i < sectors; i++) {
      var crc = Crc32.Compute(image.AsSpan(dataPhys + i * SectorSize, SectorSize), Crc32.Castagnoli);
      BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(i * 4), crc);
    }
    csumItems.Add(new Item(ExtentCsumObjectId, ExtentCsumType, diskBytenr, v));
  }

  // Drops the csum coverage for a freed data extent [diskBytenr, +numBytes).
  // The writer stores one csum item per extent keyed by its disk_bytenr, so a
  // match on the key offset removes the whole run.
  private static void RemoveCsumRange(List<Item> csumItems, long diskBytenr, long numBytes) {
    csumItems.RemoveAll(it =>
      it.Type == ExtentCsumType && it.Offset == diskBytenr);
    _ = numBytes;
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

  private static byte[] BuildInodeItem(uint mode, long size, long bytes, uint nlink, long gen, ulong flags = 0) {
    var d = new byte[160];
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(0), gen);   // generation
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(8), gen);   // transid
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(16), size);
    BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(24), bytes);
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(40), nlink);
    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(52), mode);
    BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(64), flags); // btrfs_inode_item.flags
    return d;
  }

  // Regular (non-inline) btrfs_file_extent_item. 53 bytes — mirrors BtrfsWriter.
  private static byte[] BuildRegularExtentData(long diskBytenr, long alignedLength, long logicalSize, long gen) {
    var reg = new byte[53];
    BinaryPrimitives.WriteInt64LittleEndian(reg.AsSpan(0), gen);            // generation
    BinaryPrimitives.WriteInt64LittleEndian(reg.AsSpan(8), alignedLength);  // ram_bytes
    reg[16] = 0; // compression none
    reg[20] = 1; // type = regular
    BinaryPrimitives.WriteInt64LittleEndian(reg.AsSpan(21), diskBytenr);    // disk_bytenr
    BinaryPrimitives.WriteInt64LittleEndian(reg.AsSpan(29), alignedLength); // disk_num_bytes
    BinaryPrimitives.WriteInt64LittleEndian(reg.AsSpan(37), 0);             // offset into extent
    BinaryPrimitives.WriteInt64LittleEndian(reg.AsSpan(45), alignedLength); // num_bytes
    _ = logicalSize;
    return reg;
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
    public (long Start, long Length)? DataChunk { get; private set; }

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
      const ulong BlockGroupData = 0x01;
      if ((type & BlockGroupMetadata) != 0) this.MetadataChunk = (logical, length);
      if ((type & BlockGroupData) != 0) this.DataChunk = (logical, length);
    }
  }
}
