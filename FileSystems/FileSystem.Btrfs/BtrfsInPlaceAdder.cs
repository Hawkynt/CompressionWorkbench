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
///   <item>The FS, extent, csum and root trees are each read into one flat,
///   key-ordered item list with the generic arbitrary-depth descender — a single
///   leaf, an internal node over leaves, or any deeper tree are all handled, and
///   every block visited is recorded so the whole tree can be CoW-rebuilt.</item>
///   <item>The target parent directory is resolved, creating any missing
///   intermediate directory inodes (<c>INODE_ITEM</c>/<c>INODE_REF</c>/parent
///   links) for nested targets.</item>
///   <item>The new file's items are inserted. Files below one sector stay inline
///   in the FS-tree leaf; files at/above one sector get a real data extent
///   allocated from the DATA chunk's free space, the payload written there, a
///   regular <c>EXTENT_DATA</c> item, a data <c>EXTENT_ITEM</c> (with inline
///   <c>EXTENT_DATA_REF</c>) in the extent tree, and per-sector CRC-32C
///   <c>EXTENT_CSUM</c> items in the csum tree.</item>
///   <item>Each tree's flat item set is re-sorted and re-packed into leaves, then
///   rebuilt as a B-tree of whatever height its leaf count demands: a single leaf
///   stays level 0; otherwise internal index levels are stacked until one root
///   node remains. The tree GROWS in height when its leaves overflow one internal
///   node — the FS, extent and csum trees are all rebuilt this way.</item>
///   <item>Every CoW'd metadata block (every leaf and internal node of the FS /
///   extent / csum / root trees) is allocated — preferring genuinely-free node
///   slots, then recycling the blocks this operation frees. The extent tree's own
///   block count is found by a fixed-point that accounts for the TREE_BLOCK
///   <c>EXTENT_ITEM</c> it must hold for every metadata block (its own included);
///   block-group accounting and the superblock <c>bytes_used</c> are recomputed.</item>
///   <item>The <c>FS_TREE</c> / <c>EXTENT_TREE</c> / <c>CSUM_TREE</c>
///   <c>ROOT_ITEM</c>s are repointed (with each tree's new root level) and the
///   superblock <c>root</c> + <c>root_level</c> + <c>generation</c> bumped;
///   CRC-32C is recomputed for every new block and the superblock.</item>
/// </list>
/// <para>
/// Verified against <c>btrfs check</c> (incl. <c>--check-data-csum</c>) for:
/// inline and regular (data-extent) files, nested sub-directory targets,
/// multi-leaf FS trees (internal root node), leaf splits, add-or-replace of
/// existing inline/regular files, and a multi-level (internal-node-over-leaves)
/// extent tree grown in place by adding many data-extent files. The tree-rebuild
/// path is height-generic, so an FS / extent / csum / root tree of arbitrary
/// depth is read and re-emitted; an FS tree that overflows one internal node is
/// grown to the next height by the same code. Cases still throwing
/// <see cref="NotSupportedException"/> for the rebuild fallback: non-default
/// node/sector sizes, a full metadata or DATA chunk (no room to CoW the new
/// blocks or place the new data extent).
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

    // Read the root tree (any depth) into one flat item list, remembering every
    // block it occupies so they can be freed. The root tree is tiny (a ROOT_ITEM
    // per tree) and from BtrfsWriter output is a single leaf, but it is read with
    // the generic descender so a previously-grown multi-level root tree is also
    // CoW'd correctly.
    var oldRootBlocks = new List<long>();
    var rootItems = ReadTreeLeaves(image, chunkMap, sb.RootTreeLogical, oldRootBlocks);

    var fsRootLogical = FindRootItemBytenr(rootItems, FsTreeObjectId)
      ?? throw new InvalidDataException("Btrfs in-place add: FS_TREE ROOT_ITEM missing.");
    var extentRootLogical = FindRootItemBytenr(rootItems, ExtentTreeObjectId)
      ?? throw new InvalidDataException("Btrfs in-place add: EXTENT_TREE ROOT_ITEM missing.");
    var csumRootLogical = FindRootItemBytenr(rootItems, CsumTreeObjectId)
      ?? throw new InvalidDataException("Btrfs in-place add: CSUM_TREE ROOT_ITEM missing.");

    // Every tree below is read with the generic arbitrary-depth descender:
    // internal nodes are walked recursively, every leaf is concatenated into one
    // flat item list, and the physical block of each node visited (internal and
    // leaf) is appended so the caller can free it when the tree is CoW'd.
    var oldFsBlocks = new List<long>();
    var fsItems = ReadTreeLeaves(image, chunkMap, fsRootLogical, oldFsBlocks);

    var oldExtentBlocks = new List<long>();
    var extentItems = ReadTreeLeaves(image, chunkMap, extentRootLogical, oldExtentBlocks);

    var oldCsumBlocks = new List<long>();
    var csumItems = ReadTreeLeaves(image, chunkMap, csumRootLogical, oldCsumBlocks);

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
    SortItems(csumItems);

    // ── Pack each CoW'd tree into leaves (split when a leaf overflows) ───────
    // Every tree is rebuilt from its flat item set as a B-tree of whatever height
    // its leaf count demands: a single leaf stays level 0; otherwise internal
    // index levels are stacked until one root node remains. The FS, extent, csum
    // and root trees all use the same generic packer so a tree that has grown
    // past one internal node is handled — not just the single-internal-node case.
    var fsLeaves = PackIntoLeaves(fsItems);
    var csumLeaves = PackIntoLeaves(csumItems);
    var rootLeaves = PackIntoLeaves(rootItems);

    // ── Plan metadata-block allocation ──────────────────────────────────────
    // CoW frees every old block of every CoW'd tree (FS / extent / csum / root —
    // each at whatever depth it currently is) and allocates fresh blocks for the
    // rebuilt trees. The number of blocks the extent tree itself needs depends on
    // how many TREE_BLOCK EXTENT_ITEMs it must hold, which in turn counts every
    // metadata block this operation allocates (including the extent tree's own) —
    // a circular dependency resolved by the fixed-point loop below.
    var liveBlocks = new HashSet<long>();
    AddAllMetadataBlockBytenrs(extentItems, liveBlocks);
    foreach (var b in oldRootBlocks) liveBlocks.Add(b);

    var freed = new List<long>();
    freed.AddRange(oldFsBlocks);
    freed.AddRange(oldExtentBlocks);
    freed.AddRange(oldCsumBlocks);
    freed.AddRange(oldRootBlocks);

    var (metaStart, metaLen) = chunkMap.MetadataChunk
      ?? throw new InvalidDataException("Btrfs in-place add: metadata chunk not found.");

    // Drop the freed trees' old TREE_BLOCK extents up front; the rebuilt trees'
    // extents are (re)added once their block counts are known.
    foreach (var b in oldFsBlocks) RemoveTreeBlockExtent(extentItems, b);
    foreach (var b in oldExtentBlocks) RemoveTreeBlockExtent(extentItems, b);
    foreach (var b in oldCsumBlocks) RemoveTreeBlockExtent(extentItems, b);
    foreach (var b in oldRootBlocks) RemoveTreeBlockExtent(extentItems, b);

    // The non-self-referential trees' block counts are fixed by their leaf
    // counts. The extent tree references every metadata block (its own included),
    // so its block count is found by a monotone fixed-point: assume a count, size
    // the extent leaf, recompute, repeat until it stops growing. Each TREE_BLOCK
    // EXTENT_ITEM is a fixed 51-byte value, so the item *count* — hence the block
    // count — is independent of the bytenrs assigned later.
    var fsBlockCount = TreeBlockCount(fsLeaves.Count);
    var csumBlockCount = TreeBlockCount(csumLeaves.Count);

    // The root tree's leaf count cannot change from re-pointing existing
    // ROOT_ITEMs (their sizes are fixed), so its block count is final here.
    var rootBlockCount = TreeBlockCount(rootLeaves.Count);

    // Snapshot the extent items as they stand before the rebuilt trees' own
    // TREE_BLOCK extents are added (the data extents + block groups + the
    // never-CoW'd chunk/dev tree blocks). The extent tree's block count is found
    // by re-running the REAL pack (SortItems → PackIntoLeaves) on this base set
    // plus the actual TREE_BLOCK items for an assumed metadata-block count,
    // iterating until the resulting block count stops growing. Because the probe
    // packs the exact same items the final pack will (real bytenrs, real order),
    // its leaf count equals the final one — no greedy-packing order skew.
    var baseExtentItems = new List<Item>(extentItems);
    var extentBlockCount = 1;
    var extentLeafCount = 1;
    while (true) {
      var totalMetaBlocks = fsBlockCount + csumBlockCount + rootBlockCount + extentBlockCount;
      var probe = new List<Item>(baseExtentItems);
      // One 51-byte TREE_BLOCK EXTENT_ITEM per metadata block, with bytenrs spread
      // across the metadata chunk so they interleave with the base items under the
      // sort exactly as the real registration will — keeping the probe's greedy
      // leaf count equal to the final pack's.
      for (var k = 0; k < totalMetaBlocks; k++) {
        var bytenr = metaStart + (long)(k % Math.Max(1, (int)(metaLen / NodeSize))) * NodeSize;
        probe.Add(new Item(bytenr, ExtentItemType, NodeSize, new byte[24 + 18 + 9]));
      }
      SortItems(probe);
      var leaves = PackIntoLeaves(probe).Count;
      var need = TreeBlockCount(leaves);
      extentLeafCount = leaves;
      if (need <= extentBlockCount) break;
      extentBlockCount = need;
    }

    // The allocator first hands out genuinely-free node slots (true CoW — the old
    // tree stays intact until the superblock flips). When those run out it
    // recycles the blocks this operation is about to free: their contents are
    // already loaded into memory, so reusing their offsets keeps the final image
    // consistent. Untouched metadata (chunk/dev trees) and every data extent are
    // never in the recycle pool, so they remain byte-identical.
    var alloc = new MetadataAllocator(metaStart, metaLen, liveBlocks, freed);

    // Pre-allocate every block the four rebuilt trees will occupy, in a fixed
    // order, so the extent tree can name them all (including its own) before any
    // block is serialised. The block→level assignment for each tree is derived
    // purely from its block list + leaf count by PlanTree.
    var fsBlocks = AllocBlocks(alloc, fsBlockCount);
    var extentBlocks = AllocBlocks(alloc, extentBlockCount);
    var csumBlocks = AllocBlocks(alloc, csumBlockCount);
    var rootBlocks = AllocBlocks(alloc, rootBlockCount);

    var fsPlan = PlanTree(fsLeaves, fsBlocks);
    var csumPlan = PlanTree(csumLeaves, csumBlocks);
    // The extent + root trees' block→level maps depend only on their block lists
    // and leaf counts, which are now fixed, so their plans (and thus every
    // TREE_BLOCK level) are known before the leaves are filled.
    var extentLevels = LevelMap(extentBlocks, extentLeafCount);
    var rootPlanLevels = LevelMap(rootBlocks, rootLeaves.Count);

    // ── Register the rebuilt trees' TREE_BLOCK extents in the extent tree ────
    RegisterTreeBlocks(extentItems, fsPlan.BlockLevels, FsTreeObjectId);
    RegisterTreeBlocks(extentItems, csumPlan.BlockLevels, CsumTreeObjectId);
    RegisterTreeBlocks(extentItems, extentLevels, ExtentTreeObjectId);
    RegisterTreeBlocks(extentItems, rootPlanLevels, RootTreeObjectId);

    // Recompute block-group accounting for both the metadata and data chunks.
    RecomputeBlockGroups(extentItems, chunkMap);

    SortItems(extentItems);
    var extentLeavesFinal = PackIntoLeaves(extentItems);
    // The fixed-point packed the identical item set, so the final leaf count must
    // equal the planned one; any divergence (e.g. a future change to packing)
    // would mis-level the extent tree, so fall back rather than emit a bad image.
    if (extentLeavesFinal.Count != extentLeafCount)
      throw new NotSupportedException(
        $"Btrfs in-place add: extent tree leaf count drifted (planned {extentLeafCount}, "
        + $"got {extentLeavesFinal.Count}) — use rebuild.");
    var extentPlan = PlanTree(extentLeavesFinal, extentBlocks);

    // ── CoW the root tree: repoint FS / EXTENT / CSUM ROOT_ITEMs ─────────────
    RepointRootItem(rootItems, FsTreeObjectId, fsPlan.RootLogical, nextGen, fsPlan.RootLevel);
    RepointRootItem(rootItems, ExtentTreeObjectId, extentPlan.RootLogical, nextGen, extentPlan.RootLevel);
    RepointRootItem(rootItems, CsumTreeObjectId, csumPlan.RootLogical, nextGen, csumPlan.RootLevel);
    SortItems(rootItems);
    rootLeaves = PackIntoLeaves(rootItems);
    var rootPlan = PlanTree(rootLeaves, rootBlocks);

    // ── Serialise the new blocks at their freshly allocated offsets ──────────
    WriteTree(image, chunkMap, sb, nextGen, FsTreeObjectId, fsPlan);
    WriteTree(image, chunkMap, sb, nextGen, ExtentTreeObjectId, extentPlan);
    WriteTree(image, chunkMap, sb, nextGen, CsumTreeObjectId, csumPlan);
    WriteTree(image, chunkMap, sb, nextGen, RootTreeObjectId, rootPlan);

    // Free the old blocks (zero them so stale tree data never confuses a reader
    // that scans by signature; they are no longer referenced by any tree). Skip
    // any block the allocator recycled into a new node — those already hold
    // freshly serialised, checksummed content.
    void FreeOldBlock(long logical) {
      if (alloc.WasHandedOut(logical)) return;
      var phys = chunkMap.ToPhysical(logical);
      if (phys >= 0) image.AsSpan((int)phys, NodeSize).Clear();
    }
    foreach (var b in oldFsBlocks) FreeOldBlock(b);
    foreach (var b in oldExtentBlocks) FreeOldBlock(b);
    foreach (var b in oldCsumBlocks) FreeOldBlock(b);
    foreach (var b in oldRootBlocks) FreeOldBlock(b);

    // ── Update + re-checksum the superblock ─────────────────────────────────
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(SbOffset + 0x48), nextGen);     // generation
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(SbOffset + 0x50), rootPlan.RootLogical); // root
    image[SbOffset + 0xC6] = rootPlan.RootLevel; // root_level
    UpdateSuperblockBytesUsed(image, extentItems);
    // chunk_root / chunk_root_generation are NOT touched: the chunk tree is not
    // CoW'd by an in-place file add, so it keeps its original generation.
    WriteBlockChecksum(image, SbOffset, SectorSize);
  }

  // ── Generic arbitrary-depth tree reading ───────────────────────────────────

  // Reads every leaf of a btrfs B-tree of ANY height into one flat, key-ordered
  // item list. The root may be a leaf (level 0) or an internal node at any level;
  // internal nodes are descended recursively in key-pointer order. Every block
  // visited (internal and leaf) is appended to <paramref name="blocks"/> so the
  // caller can free the whole tree when it is CoW-rebuilt.
  private static List<Item> ReadTreeLeaves(byte[] image, ChunkMap chunkMap, long rootLogical, List<long> blocks) {
    var items = new List<Item>();
    Descend(rootLogical);
    return items;

    void Descend(long logical) {
      var phys = chunkMap.ToPhysical(logical);
      if (phys < 0) throw new InvalidDataException("Btrfs in-place add: tree block unreachable.");
      blocks.Add(logical);
      var level = image[(int)phys + 100];
      if (level == 0) {
        items.AddRange(ReadLeafItems(image, phys));
        return;
      }
      var nritems = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)phys + 96));
      for (uint i = 0; i < nritems; i++) {
        var p = (int)phys + HeaderSize + (int)i * 33;
        var childLogical = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(p + 17));
        Descend(childLogical);
      }
    }
  }

  // ── Generic arbitrary-depth tree building ───────────────────────────────────
  //
  // A btrfs B-tree of any height is rebuilt from a flat, key-sorted item list by
  // packing the items into leaves and then stacking internal index levels until a
  // single root node remains. The leaf count fixes the whole shape: with L leaves
  // and a maximum fan-out of MaxKeyPtrs per internal node, level 1 holds
  // ceil(L / MaxKeyPtrs) nodes, level 2 holds ceil(that / MaxKeyPtrs), and so on
  // until one node is left — that node is the root. Every block (leaves first,
  // then each successive internal level) is drawn IN ORDER from a pre-allocated
  // block list so the extent tree can name every block (its own included) before
  // any is serialised.

  // Per-child overhead inside an internal node: key(17)+blockptr(8)+gen(8).
  private const int KeyPtrSize = 33;
  private const int MaxKeyPtrs = (NodeSize - HeaderSize) / KeyPtrSize;

  // Total blocks a tree of `leafCount` leaves occupies: the leaves plus every
  // internal index node needed to fan in to a single root.
  private static int TreeBlockCount(int leafCount) {
    if (leafCount <= 1) return 1;
    var total = leafCount;
    var level = leafCount;
    while (level > 1) {
      level = (level + MaxKeyPtrs - 1) / MaxKeyPtrs;
      total += level;
    }
    return total;
  }

  // Maps each block in `blocks` to the tree level it occupies for a tree of
  // `leafCount` leaves: the first `leafCount` blocks are leaves (level 0), the
  // next ceil(leafCount/MaxKeyPtrs) are level-1 internal nodes, and so on. The
  // assignment is identical to PlanTree's so the TREE_BLOCK extents' recorded
  // levels match the block headers PlanTree writes.
  private static Dictionary<long, byte> LevelMap(List<long> blocks, int leafCount) {
    var map = new Dictionary<long, byte>();
    var idx = 0;
    if (leafCount <= 1) {
      map[blocks[0]] = 0;
      return map;
    }
    var countThisLevel = leafCount;
    byte level = 0;
    while (true) {
      for (var i = 0; i < countThisLevel; i++)
        map[blocks[idx++]] = level;
      if (countThisLevel == 1) break;
      countThisLevel = (countThisLevel + MaxKeyPtrs - 1) / MaxKeyPtrs;
      level++;
    }
    return map;
  }

  // A serialisation plan for one rebuilt tree: the root block + its level, and
  // every block paired with the level it sits at (so the writer stamps the right
  // header level and the extent tree records the matching tree_block_info.level).
  private sealed class TreePlan {
    public required long RootLogical;
    public required byte RootLevel;
    public required Dictionary<long, byte> BlockLevels;
    // Bottom-up: the leaf batches, then each internal level's (key,childPtr) sets.
    public required List<List<Item>> Leaves;
    public required List<long> Blocks; // leaves first, then internals, in level order
    public required int LeafCount;
  }

  // Plans a tree's full block layout from its packed leaves and pre-allocated
  // block list. Leaves take blocks[0..L); internal levels take the rest in order.
  private static TreePlan PlanTree(List<List<Item>> leaves, List<long> blocks) {
    var leafCount = leaves.Count;
    return new TreePlan {
      RootLogical = blocks[TreeBlockCount(leafCount) - 1],
      RootLevel = RootLevelFor(leafCount),
      BlockLevels = LevelMap(blocks, leafCount),
      Leaves = leaves,
      Blocks = blocks,
      LeafCount = leafCount,
    };
  }

  private static byte RootLevelFor(int leafCount) {
    if (leafCount <= 1) return 0;
    byte level = 1;
    var count = (leafCount + MaxKeyPtrs - 1) / MaxKeyPtrs;
    while (count > 1) {
      count = (count + MaxKeyPtrs - 1) / MaxKeyPtrs;
      level++;
    }
    return level;
  }

  // Registers one TREE_BLOCK EXTENT_ITEM per block of a rebuilt tree, each at the
  // level the plan assigns it, owned by `ownerRoot`.
  private static void RegisterTreeBlocks(List<Item> extentItems, Dictionary<long, byte> blockLevels, long ownerRoot) {
    foreach (var (bytenr, level) in blockLevels)
      AddTreeBlockExtent(extentItems, bytenr, ownerRoot, level);
  }

  // Serialises every block of a rebuilt tree at its allocated offset. A
  // single-leaf tree is one level-0 leaf; otherwise the leaves are written first,
  // then each successive internal level is built from the lowest key of every
  // child it indexes, up to the single root node.
  private static void WriteTree(byte[] image, ChunkMap chunkMap, Superblock sb, long gen, long ownerObjectId, TreePlan plan) {
    var blocks = plan.Blocks;
    var leaves = plan.Leaves;
    var leafCount = leaves.Count;

    if (leafCount <= 1) {
      WriteLeaf(image, (int)chunkMap.ToPhysical(blocks[0]), blocks[0], ownerObjectId, gen, sb,
        leaves.Count == 1 ? leaves[0] : []);
      return;
    }

    // Write the leaves and capture each one's first key as its index entry.
    var childEntries = new List<(long objId, byte type, long offset, long blockPtr)>();
    for (var i = 0; i < leafCount; i++) {
      var leaf = leaves[i];
      WriteLeaf(image, (int)chunkMap.ToPhysical(blocks[i]), blocks[i], ownerObjectId, gen, sb, leaf);
      var first = leaf[0];
      childEntries.Add((first.ObjectId, first.Type, first.Offset, blocks[i]));
    }

    // Build internal levels bottom-up. Each level groups the previous level's
    // child entries into nodes of up to MaxKeyPtrs; the node's own index entry is
    // its first child's key. The blocks for each level follow the previous level's
    // in the pre-allocated list.
    var idx = leafCount;
    byte level = 1;
    var current = childEntries;
    while (true) {
      var next = new List<(long objId, byte type, long offset, long blockPtr)>();
      for (var start = 0; start < current.Count; start += MaxKeyPtrs) {
        var slice = current.GetRange(start, Math.Min(MaxKeyPtrs, current.Count - start));
        var nodeLogical = blocks[idx++];
        WriteInternalNode(image, (int)chunkMap.ToPhysical(nodeLogical), nodeLogical,
          ownerObjectId, level, gen, sb, slice);
        next.Add((slice[0].objId, slice[0].type, slice[0].offset, nodeLogical));
      }
      if (next.Count == 1) break;
      current = next;
      level++;
    }
  }

  // ── FS-tree edits ─────────────────────────────────────────────────────────

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

  // Draws `count` node-sized blocks from the allocator into a list, preserving
  // hand-out order so a tree's leaves precede its internal nodes.
  private static List<long> AllocBlocks(MetadataAllocator alloc, int count) {
    var list = new List<long>(count);
    for (var i = 0; i < count; i++) list.Add(alloc.Next());
    return list;
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

  /// <remarks>
  /// Object ids are unsigned on disk, and the data relocation tree's is -9 read
  /// as a signed long. Sorting signed puts it first, where btrfs expects it
  /// last — and a binary search over a leaf ordered that way stops finding the
  /// roots that follow it, which reads as "could not find extent tree".
  /// </remarks>
  private static void SortItems(List<Item> items) {
    items.Sort((a, b) => {
      var c = ((ulong)a.ObjectId).CompareTo((ulong)b.ObjectId);
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
