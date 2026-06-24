#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ReiserFs;

// ─────────────────────────────────────────────────────────────────────────────
// Genuine in-place ReiserFS v3.6 add / remove.
//
// Mutates the live S+tree WITHOUT re-emitting the whole image. The superblock
// identity (UUID, journal, magic, label), every existing object id, and — most
// importantly — every existing INDIRECT file data block stays byte-identical at
// its original byte offset. Only the formatted tree blocks (leaves + internal
// nodes) the edit touches are rewritten, and new data blocks (for INDIRECT
// bodies) are appended past the end of the image.
//
// The pipeline for both Add and Remove:
//   1. Parse the WHOLE S+tree (descending any internal nodes) into a key-ordered
//      item list plus the set of formatted tree blocks (a reuse pool).
//   2. Apply the edit to the item list in memory:
//        * Add: resolve / CREATE intermediate directory objects for nested
//          paths (their SD + "."/".." dirents), allocate a fresh objectid, build
//          the file's STAT_DATA + DIRECT/INDIRECT body items, splice the new
//          R5-hashed entry into the parent directory (re-chunking the DIRENTRY
//          items so none exceeds the leaf budget), bump the parent SD size.
//          On a same-name collision the old object's items + data blocks are
//          freed first (replace-by-name).
//        * Remove: resolve the target by path; for a file drop its items and
//          free its INDIRECT data blocks; for a directory recurse and remove the
//          whole subtree; drop the parent entry and shrink the parent SD.
//   3. Re-pack the item list into leaves (greedy first-fit) and rebuild the
//      internal levels bottom-up, REUSING the tree-block pool first (so a small
//      edit that still fits one leaf does not grow the image) and allocating /
//      freeing blocks in the bitmap as the tree grows or shrinks. The superblock
//      root_block and tree_height are updated to match.
//
// This handles single-leaf images, leaf split / merge, tree-height growth and
// collapse (tree_height 2 ↔ 3 ↔ 4…), nested sub-directory targets, replace-by-
// name, directory removal and images that span MULTIPLE on-disk bitmap blocks
// (one bitmap block per BlockSize*8 = 32768 blocks; the first at block 17, every
// subsequent one at the first block of its region). Block allocation/free scans
// every bitmap block, new bitmap blocks are materialised + self-marked as the
// image grows past a region boundary, appended data runs skip the bitmap-block
// slots, and s_bmap_nr is kept in sync — all reiserfsck --check clean.
//
// References: reiserfsprogs reiserfscore/node_formats.c (item head, dir entry,
// internal node, SD layout), reiserfslib.c (reiserfs_add_entry / hash_value),
// bitmap.c. Field offsets mirror ReiserFsWriter / ReiserFsReader exactly.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Genuine in-place add / remove for ReiserFS v3.6 images — mutates the live
/// S+tree (leaf split/merge, internal-node maintenance, tree-height growth,
/// nested directory creation, replace-by-name, recursive directory removal)
/// without re-emitting the whole image or relocating existing file data blocks.
/// Throws <see cref="NotSupportedException"/> for the few structural edge cases
/// it still defers (over-long names, single item larger than a leaf) so the
/// caller can fall back to the rebuild path.
/// </summary>
internal static class ReiserFsInPlaceAdder {
  private const int SuperblockOff = 65536;
  private const int BlockSize = 4096;
  private const int BlockHeadSize = 24;
  private const int ItemHeaderSize = 24;
  private const int DehSize = 16;
  private const int SdV2Size = 44;
  private const int SdBlocksOffset = 36;
  private const int SdSizeOffset = 8;
  private const int SectorsPerBlock = BlockSize / 512;

  private const int Off_BlockCount = 0;
  private const int Off_FreeBlocks = 4;
  private const int Off_RootBlock = 8;
  private const int Off_TreeHeight = 68;
  private const int Off_OidCursize = 48;
  private const int SuperblockSize = 204;

  private const uint RootParentObjectId = 1;
  private const uint RootObjectId = 2;

  private const int MaxDirectBody = 1024;

  private const int BitmapBlock = 17;
  // One bitmap block covers BlockSize*8 blocks. The FIRST bitmap lives at block
  // 17 (right after the superblock); every subsequent bitmap N (N>=1) lives at
  // the first block of its region, i.e. absolute block N*BitmapSpan. Bitmap N
  // covers blocks [N*BitmapSpan, (N+1)*BitmapSpan). reiserfscore/bitmap.c.
  private const int BitmapSpan = BlockSize * 8; // 32768 blocks per bitmap block
  private const int Off_BmapNr = 70;
  private const int LeafPayload = BlockSize - BlockHeadSize;
  // disk_child: le32 dc_block_number + le16 dc_size + le16 reserved.
  private const int DiskChildSize = 8;
  private const int KeySize = 16;

  // Item type codes (key offset_v2 top 4 bits).
  private const ulong TypeStatDataV2 = 0UL << 60;
  private const ulong TypeIndirectV2 = 1UL << 60;
  private const ulong TypeDirectV2 = 2UL << 60;
  private const ulong TypeDirentryV2 = 3UL << 60;

  /// <summary>
  /// Attempts a genuine in-place add of <paramref name="name"/> into the root
  /// directory of <paramref name="image"/> (a full filesystem image byte array).
  /// Returns the new image bytes (possibly grown past <paramref name="image"/>'s
  /// length for INDIRECT data blocks). Throws <see cref="NotSupportedException"/>
  /// for cases the caller should rebuild instead.
  /// </summary>
  public static byte[] AddFile(byte[] image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var flat = name.Replace('\\', '/').Trim('/');
    if (flat.Length == 0)
      throw new ArgumentException("name is empty", nameof(name));
    if (Encoding.UTF8.GetByteCount(flat) > 200)
      throw new NotSupportedException("ReiserFS in-place add: over-long name uses rebuild.");

    var sb = SuperblockOff;
    var rootBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sb + Off_RootBlock));
    if ((long)rootBlock * BlockSize < 0 || (long)rootBlock * BlockSize + BlockSize > image.Length)
      throw new NotSupportedException("ReiserFS in-place add: root block out of range.");

    // Parse the WHOLE S+tree into a key-ordered item list plus the reuse pool of
    // tree blocks. This handles single-leaf, multi-leaf and tall trees uniformly.
    var (items, treeBlocks) = ParseWholeTree(image, rootBlock);

    // Resolve the target directory (creating intermediate directories in place
    // for nested paths) and the leaf-relative file name.
    var (parentDirId, parentObjId, leafName) = ResolveOrCreateParent(image, items, flat);

    // Replace-by-name: if an entry of this name already exists in the target
    // directory, drop the old object's items + dirent so the new bytes win.
    if (FindDirentItem(items, parentDirId, parentObjId) < 0)
      throw new NotSupportedException("ReiserFS in-place add: target DIRENTRY item not found.");
    var existingEntries = ReadAllDirEntries(items, parentDirId, parentObjId);
    var collision = existingEntries.FirstOrDefault(e => string.Equals(e.Name, leafName, StringComparison.Ordinal));
    if (collision != null) {
      // Free the colliding object's items (and INDIRECT data blocks) before
      // re-adding under the same name.
      RemoveObjectItems(ref image, items, parentObjId, collision.PointedObjId, wipeData: false);
      existingEntries.RemoveAll(e => string.Equals(e.Name, leafName, StringComparison.Ordinal));
    }

    // Allocate a fresh objectid past the current map range.
    var newObjId = AllocateObjectId(image);

    var useIndirect = data.Length > MaxDirectBody;
    var blockCountNeeded = useIndirect ? (data.Length + BlockSize - 1) / BlockSize : 0;

    // ── Build the new file's items ─────────────────────────────────────────────
    var sd = new byte[SdV2Size];
    WriteStatDataV2(sd, mode: 0x81A4, nlink: 1, size: (ulong)data.Length,
      blocks: useIndirect ? (uint)(blockCountNeeded * SectorsPerBlock) : 0u);
    var sdItem = new Item {
      DirId = parentObjId, ObjectId = newObjId,
      OffsetV2 = TypeStatDataV2 | 0u, KeyFormat = 1, UField = 0, Body = sd, ItemType = 0,
    };

    Item? bodyItem = null;
    byte[]? indirectBody = null;
    if (data.Length > 0) {
      if (!useIndirect) {
        bodyItem = new Item {
          DirId = parentObjId, ObjectId = newObjId,
          OffsetV2 = TypeDirectV2 | 1u, KeyFormat = 1, UField = 0, Body = data, ItemType = 2,
        };
      } else {
        indirectBody = new byte[blockCountNeeded * 4];
        bodyItem = new Item {
          DirId = parentObjId, ObjectId = newObjId,
          OffsetV2 = TypeIndirectV2 | 1u, KeyFormat = 1, UField = 0, Body = indirectBody, ItemType = 1,
        };
      }
    }

    // DIRECT-only files store their tail inside a shared leaf → 1 block.
    if (!useIndirect && data.Length > 0)
      BinaryPrimitives.WriteUInt32LittleEndian(sd.AsSpan(SdBlocksOffset), (uint)SectorsPerBlock);

    // ── New dirent into the target directory (chunked across items as needed) ──
    var mergedEntries = new List<DirEntry>(existingEntries) {
      new(leafName, parentObjId, newObjId, HashValueR5(leafName)),
    };
    var dirItemGrowth = ReplaceDirentItems(items, parentDirId, parentObjId, mergedEntries);

    // ── Insert the new items in key order ──────────────────────────────────────
    var insertList = new List<Item> { sdItem };
    if (bodyItem != null) insertList.Add(bodyItem);
    foreach (var it in insertList) {
      var idx = items.FindIndex(x => CompareKeys(x, it) > 0);
      if (idx < 0) items.Add(it); else items.Insert(idx, it);
    }

    // ── Grow the parent directory SD's size / blocks ───────────────────────────
    UpdateDirSize(items, parentDirId, parentObjId, dirItemGrowth);

    // ── Grow the image for INDIRECT data blocks, fill block pointers ────────────
    var working = image;
    if (useIndirect)
      working = AppendDataBlocks(image, data, blockCountNeeded, indirectBody!);

    // ── Re-pack the whole tree in place (reusing existing tree blocks) ──────────
    return RebuildTreeInPlace(working, treeBlocks, items);
  }

  /// <summary>
  /// Updates the (dir_id, objId) directory's stat-data sd_size by
  /// <paramref name="byteDelta"/> and recomputes sd_blocks = ceil(size / 512).
  /// </summary>
  private static void UpdateDirSize(List<Item> items, uint dirId, uint objId, int byteDelta) {
    var idx = items.FindIndex(x => x.ItemType == 0 && x.DirId == dirId && x.ObjectId == objId);
    if (idx < 0) return;
    var rsd = items[idx].Body;
    if (rsd.Length < SdSizeOffset + 8) return;
    var oldSize = BinaryPrimitives.ReadUInt64LittleEndian(rsd.AsSpan(SdSizeOffset));
    var newDirSize = (ulong)((long)oldSize + byteDelta);
    var rsdCopy = (byte[])rsd.Clone();
    BinaryPrimitives.WriteUInt64LittleEndian(rsdCopy.AsSpan(SdSizeOffset), newDirSize);
    BinaryPrimitives.WriteUInt32LittleEndian(rsdCopy.AsSpan(SdBlocksOffset), (uint)((newDirSize + 511) / 512));
    items[idx] = items[idx] with { Body = rsdCopy };
  }

  /// <summary>
  /// Appends <paramref name="blockCountNeeded"/> dedicated data blocks to the end
  /// of <paramref name="image"/>, writes <paramref name="data"/> into them, fills
  /// <paramref name="indirectBody"/> with their absolute block numbers, and flips
  /// the bitmap / superblock counters. Returns the grown image.
  /// </summary>
  private static byte[] AppendDataBlocks(byte[] image, byte[] data, int blockCountNeeded, byte[] indirectBody) {
    var sb = SuperblockOff;
    var blockCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sb + Off_BlockCount));

    // Assign block positions for the data run starting just past the current end.
    // A position that coincides with a region-boundary BITMAP block
    // (pos % BitmapSpan == 0, pos > 0) is reserved for that bitmap block and
    // SKIPPED for data, so the contiguous append "jumps over" any new on-disk
    // bitmap block. The indirect pointers therefore record the true data-block
    // numbers (which may be discontiguous around a bitmap block).
    var dataBlocks = new int[blockCountNeeded];
    var pos = blockCount;
    for (var i = 0; i < blockCountNeeded; i++) {
      while (pos > 0 && pos % BitmapSpan == 0) pos++; // skip bitmap-block slot
      dataBlocks[i] = pos;
      pos++;
    }
    var newTotalBlocks = blockCountNeeded == 0 ? blockCount : dataBlocks[^1] + 1;

    var working = new byte[(long)newTotalBlocks * BlockSize];
    Buffer.BlockCopy(image, 0, working, 0, image.Length);

    for (var i = 0; i < blockCountNeeded; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(indirectBody.AsSpan(i * 4), (uint)dataBlocks[i]);
    for (var i = 0; i < blockCountNeeded; i++) {
      var srcOff = i * BlockSize;
      var copyLen = Math.Min(BlockSize, data.Length - srcOff);
      if (copyLen > 0)
        Array.Copy(data, srcOff, working, (long)dataBlocks[i] * BlockSize, copyLen);
    }

    // Create + self-mark any new on-disk bitmap blocks the grown filesystem now
    // needs (one per BitmapSpan region), then mark every data block used and
    // tail-fill the final bitmap. Bitmap-block slots were skipped above, so no
    // data pointer ever lands on one.
    EnsureBitmapBlocks(working, newTotalBlocks);
    foreach (var b in dataBlocks)
      SetBitmapBit(working, b);
    FinalizeBitmapTail(working, newTotalBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(working.AsSpan(sb + Off_BlockCount), (uint)newTotalBlocks);
    // The appended blocks lie BEYOND the old block_count, so they were never part
    // of the free pool — growing block_count keeps s_free_blocks unchanged (every
    // new block is created already-used). The freshly-created bitmap blocks are
    // likewise metadata created already-used.
    return working;
  }

  private static int LeafUsedSpace(List<Item> items) {
    var s = 0;
    foreach (var it in items) s += it.Body.Length + ItemHeaderSize;
    return s;
  }

  // Max children an internal node can index: BLKH + n*KEY + (n+1)*DC <= block.
  private const int MaxInternalChildren =
    (BlockSize - BlockHeadSize - DiskChildSize) / (KeySize + DiskChildSize) + 1;

  /// <summary>
  /// Re-packs the whole item list into a fresh balanced S+tree, REUSING the
  /// existing tree blocks (and allocating more as needed), then patches the
  /// superblock root_block / tree_height. Existing INDIRECT data blocks are NOT
  /// touched — only formatted tree (leaf + internal) blocks are rewritten — so
  /// file payloads keep their absolute offsets. Returns the (possibly grown)
  /// image. Used for the multi-leaf / tree-growth add and remove cases that the
  /// single-leaf splice path cannot service directly.
  /// </summary>
  private static byte[] RebuildTreeInPlace(byte[] image, List<int> reusePool, List<Item> items) {
    var sb = SuperblockOff;
    var working = image;

    // 1. Pack items into leaves (greedy first-fit, single item never spans).
    var leaves = PackLeaves(items);

    // 2. Allocate a block for every node we will write (leaves + internal nodes),
    //    drawing from the reuse pool first so the tree stays the same size when
    //    it can. We compute the node count per level top-down.
    var pool = new Queue<int>(reusePool.OrderBy(b => b));
    int TakeBlock() {
      if (pool.Count > 0) {
        var b = pool.Dequeue();
        working.AsSpan(b * BlockSize, BlockSize).Clear();
        return b;
      }
      return AllocateBlock(ref working);
    }

    // 3. Assign leaf blocks.
    var leafBlocks = new int[leaves.Count];
    for (var i = 0; i < leaves.Count; i++) leafBlocks[i] = TakeBlock();

    // 4. Build the internal levels bottom-up. Each level groups the children of
    //    the level below into internal nodes of up to MaxInternalChildren.
    //    `childBlocks` / `childFirstKey` / `childUsed` describe the current
    //    level's nodes; we stop when a single node remains (the root).
    var childBlocks = new List<int>(leafBlocks);
    var childFirstKey = new List<byte[]>();
    var childUsed = new List<int>();
    for (var i = 0; i < leaves.Count; i++) {
      childFirstKey.Add(KeyOf(leaves[i][0]));
      childUsed.Add(LeafUsedSpace(leaves[i]));
    }

    var rootBlock = childBlocks[0];
    var treeHeight = 1; // a lone leaf is height... computed below

    if (childBlocks.Count == 1) {
      // Single leaf — tree_height = 2, root is the leaf. Write it and return.
      WriteLeaf(working.AsSpan(leafBlocks[0] * BlockSize, BlockSize), leaves[0], MaxKey);
      ReleaseUnusedPool(working, pool);
      BinaryPrimitives.WriteUInt32LittleEndian(working.AsSpan(sb + Off_RootBlock), (uint)leafBlocks[0]);
      BinaryPrimitives.WriteUInt16LittleEndian(working.AsSpan(sb + Off_TreeHeight), 2);
      return working;
    }

    // Write the leaves now (right-delim key = first key of the next leaf).
    for (var i = 0; i < leaves.Count; i++) {
      var rdk = i + 1 < leaves.Count ? KeyOf(leaves[i + 1][0]) : MaxKey;
      WriteLeaf(working.AsSpan(leafBlocks[i] * BlockSize, BlockSize), leaves[i], rdk);
    }

    treeHeight = 2; // leaves are level 1; at least one internal level above
    while (childBlocks.Count > 1) {
      var parentBlocks = new List<int>();
      var parentFirstKey = new List<byte[]>();
      var parentUsed = new List<int>();

      var n = childBlocks.Count;
      var idx = 0;
      while (idx < n) {
        var groupSize = Math.Min(MaxInternalChildren, n - idx);
        // Avoid leaving a single orphan child for the next group when possible.
        if (n - idx - groupSize == 1 && groupSize > 2) groupSize--;

        var blocksInGroup = childBlocks.GetRange(idx, groupSize);
        var usedInGroup = childUsed.GetRange(idx, groupSize);
        var keysInGroup = new byte[groupSize - 1][];
        for (var k = 0; k < groupSize - 1; k++)
          keysInGroup[k] = childFirstKey[idx + k + 1];

        var nodeBlk = TakeBlock();
        WriteInternalNode(working.AsSpan(nodeBlk * BlockSize, BlockSize),
          [.. blocksInGroup], [.. usedInGroup], keysInGroup);

        parentBlocks.Add(nodeBlk);
        parentFirstKey.Add(childFirstKey[idx]); // left-delim key propagates up
        parentUsed.Add(InternalUsedSpace(groupSize));
        idx += groupSize;
      }

      childBlocks = parentBlocks;
      childFirstKey = parentFirstKey;
      childUsed = parentUsed;
      treeHeight++;
    }

    rootBlock = childBlocks[0];
    ReleaseUnusedPool(working, pool);
    BinaryPrimitives.WriteUInt32LittleEndian(working.AsSpan(sb + Off_RootBlock), (uint)rootBlock);
    BinaryPrimitives.WriteUInt16LittleEndian(working.AsSpan(sb + Off_TreeHeight), (ushort)treeHeight);
    return working;
  }

  // dc_size for an internal child = used bytes = MAX_CHILD_SIZE - free, where
  // MAX_CHILD_SIZE = blocksize - BLKH_SIZE. For an internal node that is the
  // key array + the disk_child array (the block head is excluded).
  private static int InternalUsedSpace(int childCount) {
    var keyCount = childCount - 1;
    return keyCount * KeySize + childCount * DiskChildSize;
  }

  /// <summary>
  /// Greedy first-fit packing of a key-ordered item list into leaf-sized groups.
  /// A single item never spans leaves (DIRENTRY items are entry-chunked and
  /// DIRECT/INDIRECT bodies always fit one leaf).
  /// </summary>
  private static List<List<Item>> PackLeaves(List<Item> items) {
    var leaves = new List<List<Item>>();
    var current = new List<Item>();
    var used = 0;
    foreach (var it in items) {
      var cost = it.Body.Length + ItemHeaderSize;
      if (cost > LeafPayload)
        throw new NotSupportedException("ReiserFS in-place add: single item exceeds leaf payload.");
      if (current.Count > 0 && used + cost > LeafPayload) {
        leaves.Add(current);
        current = [];
        used = 0;
      }
      current.Add(it);
      used += cost;
    }
    if (current.Count > 0 || leaves.Count == 0) leaves.Add(current);
    return leaves;
  }

  /// <summary>
  /// Any tree blocks left in the reuse pool after re-packing are no longer part
  /// of the tree: free them in the bitmap and bump the free count.
  /// </summary>
  private static void ReleaseUnusedPool(byte[] image, Queue<int> pool) {
    if (pool.Count == 0) return;
    var freed = 0;
    while (pool.Count > 0) {
      var b = pool.Dequeue();
      ClearBitmapBit(image, b);
      freed++;
    }
    AdjustFreeBlocks(image, freed);
  }

  /// <summary>
  /// Attempts a genuine in-place remove of <paramref name="name"/> from the root
  /// directory of <paramref name="image"/>: drops the file's STAT_DATA + body
  /// items and its R5 dirent from the root leaf, frees any INDIRECT data blocks
  /// in the bitmap (zeroing them when <paramref name="wipeData"/>), shrinks the
  /// root SD size, and re-packs only that leaf. Returns the (possibly unchanged-
  /// length) image bytes. Throws <see cref="NotSupportedException"/> for cases
  /// the caller should rebuild instead, and <see cref="FileNotFoundException"/>
  /// when the name is not a root-directory file.
  /// </summary>
  public static byte[] RemoveFile(byte[] image, string name, bool wipeData) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var flat = name.Replace('\\', '/').Trim('/');
    if (flat.Length == 0) throw new FileNotFoundException();

    var sb = SuperblockOff;
    var rootBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sb + Off_RootBlock));
    if ((long)rootBlock * BlockSize < 0 || (long)rootBlock * BlockSize + BlockSize > image.Length)
      throw new NotSupportedException("ReiserFS in-place remove: root block out of range.");

    var (items, treeBlocks) = ParseWholeTree(image, rootBlock);

    // Resolve the parent directory of the target by walking the path. Missing
    // intermediate directories mean the entry does not exist.
    var segments = flat.Split('/', StringSplitOptions.RemoveEmptyEntries);
    uint curDirId = RootParentObjectId, curObjId = RootObjectId;
    for (var s = 0; s < segments.Length - 1; s++) {
      var entries = ReadAllDirEntries(items, curDirId, curObjId);
      var match = entries.FirstOrDefault(e => string.Equals(e.Name, segments[s], StringComparison.Ordinal));
      if (match == null) throw new FileNotFoundException(flat);
      curDirId = curObjId;
      curObjId = match.PointedObjId;
    }
    var parentDirId = curDirId;
    var parentObjId = curObjId;
    var leafName = segments[^1];

    var parentEntries = ReadAllDirEntries(items, parentDirId, parentObjId);
    var target = parentEntries.FirstOrDefault(e => string.Equals(e.Name, leafName, StringComparison.Ordinal));
    if (target == null) throw new FileNotFoundException(flat);
    var objId = target.PointedObjId;

    var sdIdx = FindSdItem(items, parentObjId, objId);
    if (sdIdx < 0) throw new NotSupportedException("ReiserFS in-place remove: target SD not found.");
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(items[sdIdx].Body.AsSpan(0));
    var isDir = (mode & 0xF000) == 0x4000;

    var working = image;
    if (isDir) {
      // Recursively remove the directory subtree. nlink of the PARENT drops by
      // one (the removed dir's ".." link is gone).
      RemoveDirectoryRecursive(ref working, items, parentObjId, objId, wipeData);
      BumpNlink(items, parentDirId, parentObjId, -1);
    } else {
      RemoveObjectItems(ref working, items, parentObjId, objId, wipeData);
    }

    // Drop the entry from the parent directory (re-chunked).
    parentEntries.RemoveAll(e => string.Equals(e.Name, leafName, StringComparison.Ordinal));
    var shrink = ReplaceDirentItems(items, parentDirId, parentObjId, parentEntries);
    UpdateDirSize(items, parentDirId, parentObjId, shrink);

    return RebuildTreeInPlace(working, treeBlocks, items);
  }

  /// <summary>
  /// Recursively removes a directory object and every descendant (files and
  /// sub-directories) from <paramref name="items"/>, freeing all INDIRECT data
  /// blocks. <paramref name="parentObjId"/> is the removed directory's parent
  /// objectid; <paramref name="dirObjId"/> is the directory itself.
  /// </summary>
  private static void RemoveDirectoryRecursive(
    ref byte[] image, List<Item> items, uint parentObjId, uint dirObjId, bool wipeData) {
    // Children live under dir_id == dirObjId. Enumerate this directory's entries
    // (skipping "." and "..") and recurse / delete each.
    var entries = ReadAllDirEntries(items, parentObjId, dirObjId);
    foreach (var e in entries) {
      if (e.Name is "." or "..") continue;
      var childObjId = e.PointedObjId;
      var childSdIdx = FindSdItem(items, dirObjId, childObjId);
      if (childSdIdx < 0) continue;
      var childMode = BinaryPrimitives.ReadUInt16LittleEndian(items[childSdIdx].Body.AsSpan(0));
      if ((childMode & 0xF000) == 0x4000)
        RemoveDirectoryRecursive(ref image, items, dirObjId, childObjId, wipeData);
      else
        RemoveObjectItems(ref image, items, dirObjId, childObjId, wipeData);
    }
    // Remove the directory's own SD + DIRENTRY items.
    RemoveObjectItems(ref image, items, parentObjId, dirObjId, wipeData);
  }

  // ── Objectid allocation ─────────────────────────────────────────────────────

  // The objectid map encodes used ranges as ascending [start, end_exclusive]
  // pairs. The last pair's end is the first id not yet used in that span. We
  // allocate that id and extend the pair (or the cursize) accordingly, exactly
  // as reiserfs_get_objectid would.
  private static uint AllocateObjectId(byte[] image) {
    var sb = SuperblockOff;
    var cursize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(sb + Off_OidCursize));
    if (cursize < 2 || (cursize & 1) != 0)
      throw new NotSupportedException("ReiserFS in-place add: unexpected objectid-map size.");
    // Read the map.
    var map = new uint[cursize];
    for (var i = 0; i < cursize; i++)
      map[i] = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sb + SuperblockSize + i * 4));

    // The map is a sequence of [used_start, used_end_excl] pairs. The simplest
    // allocation: take map[last] (the end of the last used range), then advance
    // that boundary by one. If the last entry equals the previous range start +
    // 1 boundary cleanly this stays a valid 2-pair map.
    var lastIdx = cursize - 1;
    var allocated = map[lastIdx];
    map[lastIdx] = allocated + 1;
    // Write the updated boundary back.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + SuperblockSize + lastIdx * 4), map[lastIdx]);
    return allocated;
  }

  // ── Block allocation / internal nodes ───────────────────────────────────────

  /// <summary>
  /// Allocates one fresh formatted block: reuses a free block inside the bitmap
  /// if one exists, otherwise grows the image by one block at the end. Marks the
  /// block used, decrements the free count, and zeroes its contents. Returns the
  /// absolute block number. <paramref name="image"/> may be reassigned to a
  /// larger array when the image grows.
  /// </summary>
  private static int AllocateBlock(ref byte[] image) {
    var sb = SuperblockOff;
    var blockCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sb + Off_BlockCount));

    // First-fit a free block strictly inside the current filesystem range,
    // scanning EVERY bitmap block (not just the first). Reserved low blocks,
    // the journal and the bitmap blocks themselves are already marked used so
    // they will never be picked.
    for (var b = 0; b < blockCount; b++) {
      if (!IsBitmapBitSet(image, b)) {
        SetBitmapBit(image, b);
        AdjustFreeBlocks(image, -1);
        image.AsSpan(b * BlockSize, BlockSize).Clear();
        return b;
      }
    }

    // No free block — append one at the end of the image. If the next slot is a
    // region-boundary BITMAP block (pos % BitmapSpan == 0, pos > 0), reserve that
    // slot for the bitmap block and place the formatted block one position later
    // so the formatted block and the bitmap block never collide.
    var newBlock = blockCount;
    while (newBlock > 0 && newBlock % BitmapSpan == 0) newBlock++;
    var newTotal = newBlock + 1;
    var grown = new byte[(long)newTotal * BlockSize];
    Buffer.BlockCopy(image, 0, grown, 0, image.Length);
    image = grown;
    EnsureBitmapBlocks(image, newTotal);
    SetBitmapBit(image, newBlock);
    FinalizeBitmapTail(image, newTotal);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + Off_BlockCount), (uint)newTotal);
    return newBlock;
  }

  private static void AdjustFreeBlocks(byte[] image, int delta) {
    var sb = SuperblockOff;
    var free = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sb + Off_FreeBlocks));
    free += delta;
    if (free < 0) free = 0;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + Off_FreeBlocks), (uint)free);
  }

  /// <summary>
  /// Writes an internal S+tree node: block_head (blk_level = 2) + a left-delim
  /// key for every child after the first + one disk_child per child. dc_size is
  /// the child's used space in bytes (MAX_CHILD_SIZE - free), which reiserfsck's
  /// bad_path check verifies.
  /// </summary>
  private static void WriteInternalNode(
    Span<byte> blk, int[] childBlocks, int[] childUsedSpace, byte[][] keys) {
    blk.Clear();
    var childCount = childBlocks.Length;
    var keyCount = childCount - 1;

    BinaryPrimitives.WriteUInt16LittleEndian(blk[0..], 2);              // blk_level (internal)
    BinaryPrimitives.WriteUInt16LittleEndian(blk[2..], (ushort)keyCount);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[4..], 0);              // blk_free_space
    BinaryPrimitives.WriteUInt16LittleEndian(blk[6..], 0);             // blk_reserved
    MaxKey.CopyTo(blk[8..]);                                            // right-delim key (tree root)

    var keysOff = BlockHeadSize;
    for (var i = 0; i < keyCount; i++)
      keys[i].AsSpan(0, KeySize).CopyTo(blk[(keysOff + i * KeySize)..]);

    var ptrsOff = keysOff + keyCount * KeySize;
    for (var i = 0; i < childCount; i++) {
      var dc = blk[(ptrsOff + i * DiskChildSize)..];
      BinaryPrimitives.WriteUInt32LittleEndian(dc[0..], (uint)childBlocks[i]); // dc_block_number
      BinaryPrimitives.WriteUInt16LittleEndian(dc[4..], (ushort)childUsedSpace[i]); // dc_size
      BinaryPrimitives.WriteUInt16LittleEndian(dc[6..], 0);
    }
  }

  /// <summary>Returns the 16-byte on-disk key (dir_id, objectid, offset) of an item.</summary>
  private static byte[] KeyOf(Item it) {
    var key = new byte[KeySize];
    BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(0), it.DirId);
    BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(4), it.ObjectId);
    if (it.KeyFormat == 0) {
      var (offV1, uniq) = KeyFormat1Bytes(it);
      BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(8), offV1);
      BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(12), uniq);
    } else {
      BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8), it.OffsetV2);
    }
    return key;
  }

  // ── Bitmap (multi-block aware) ───────────────────────────────────────────

  /// <summary>
  /// Absolute block number of the on-disk bitmap block that tracks block
  /// <paramref name="block"/>. Bitmap 0 is at block 17; bitmap N (N>=1) is at
  /// block N*BitmapSpan.
  /// </summary>
  private static int BitmapBlockFor(int block) {
    var region = block / BitmapSpan;
    return region == 0 ? BitmapBlock : region * BitmapSpan;
  }

  /// <summary>Marks block <paramref name="block"/> used in its bitmap.</summary>
  private static void SetBitmapBit(byte[] image, int block) {
    var bm = BitmapBlockFor(block);
    var bit = block % BitmapSpan;
    image[bm * BlockSize + (bit >> 3)] |= (byte)(1 << (bit & 7));
  }

  /// <summary>Marks block <paramref name="block"/> free in its bitmap.</summary>
  private static void ClearBitmapBit(byte[] image, int block) {
    var bm = BitmapBlockFor(block);
    var bit = block % BitmapSpan;
    image[bm * BlockSize + (bit >> 3)] &= (byte)~(1 << (bit & 7));
  }

  /// <summary>True when block <paramref name="block"/> is marked used.</summary>
  private static bool IsBitmapBitSet(byte[] image, int block) {
    var bm = BitmapBlockFor(block);
    var bit = block % BitmapSpan;
    return (image[bm * BlockSize + (bit >> 3)] & (1 << (bit & 7))) != 0;
  }

  /// <summary>
  /// After growing the filesystem to <paramref name="totalBlocks"/> blocks,
  /// patches the LAST bitmap block's tail so every bit beyond the filesystem is
  /// 1 (reiserfsck "zero bit found after the last valid bit"), and updates
  /// s_bmap_nr. Earlier bitmap blocks are full-coverage and need no tail-fill.
  /// </summary>
  private static void FinalizeBitmapTail(byte[] image, int totalBlocks) {
    var bmapNr = (totalBlocks + BitmapSpan - 1) / BitmapSpan;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(SuperblockOff + Off_BmapNr), (ushort)bmapNr);

    // The last bitmap block spans [(bmapNr-1)*BitmapSpan .. bmapNr*BitmapSpan).
    var lastBmRegion = bmapNr - 1;
    var lastBm = lastBmRegion == 0 ? BitmapBlock : lastBmRegion * BitmapSpan;
    var bmap = image.AsSpan(lastBm * BlockSize, BlockSize);
    // Local bit index of the first block PAST the filesystem within this bitmap.
    var firstFreeLocal = totalBlocks - lastBmRegion * BitmapSpan;
    // Set the remainder of the partial byte holding the boundary.
    for (var bit = firstFreeLocal; (bit & 7) != 0 && bit < BitmapSpan; bit++)
      bmap[bit >> 3] |= (byte)(1 << (bit & 7));
    var lastValidByte = (firstFreeLocal + 7) / 8;
    for (var i = lastValidByte; i < BlockSize; i++)
      bmap[i] = 0xFF;
  }

  /// <summary>
  /// Ensures a real bitmap block exists at each region boundary covered by
  /// [0, <paramref name="totalBlocks"/>): the bitmap block at N*BitmapSpan
  /// (N>=1) is itself a metadata block, so it must be present (zeroed) and
  /// marked used in its own bitmap. Called after the image array has grown to
  /// hold <paramref name="totalBlocks"/> blocks. New bitmap blocks are created
  /// already-used and therefore do NOT change s_free_blocks.
  /// </summary>
  private static void EnsureBitmapBlocks(byte[] image, int totalBlocks) {
    var bmapNr = (totalBlocks + BitmapSpan - 1) / BitmapSpan;
    for (var n = 1; n < bmapNr; n++) {
      var bmBlock = n * BitmapSpan;
      // The bitmap block records itself as used (its bit 0).
      if (!IsBitmapBitSet(image, bmBlock))
        SetBitmapBit(image, bmBlock);
    }
  }

  // ── Leaf parse / serialise ──────────────────────────────────────────────────

  private sealed record Item {
    public required uint DirId;
    public required uint ObjectId;
    public required ulong OffsetV2;
    public required ushort KeyFormat;
    public required ushort UField;
    public required byte[] Body;
    public required int ItemType;
    // KEY_FORMAT_1 stores offset+uniqueness; we preserve the raw 8 key bytes for
    // items parsed from disk so re-serialisation is byte-faithful.
    public uint OffsetV1;
    public uint UniquenessV1;
  }

  /// <summary>
  /// Walks the whole S+tree from <paramml name="rootBlock"/>, returning every
  /// leaf item in key order and the set of formatted tree block numbers (leaves
  /// and internal nodes) that currently make up the tree. The tree blocks form a
  /// reuse pool when the tree is re-packed in place.
  /// </summary>
  private static (List<Item> Items, List<int> TreeBlocks) ParseWholeTree(byte[] image, int rootBlock) {
    var items = new List<Item>();
    var treeBlocks = new List<int>();
    // Walk depth-first but accumulate leaf items left-to-right by descending in
    // key order. Internal children are already key-ordered, so an explicit
    // left-to-right recursion preserves global key order.
    void Walk(int blockNum) {
      var off = blockNum * BlockSize;
      if (off < 0 || off + BlockHeadSize > image.Length) return;
      treeBlocks.Add(blockNum);
      var level = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(off));
      var nr = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(off + 2));
      if (level > 1) {
        var ptrsOff = off + BlockHeadSize + nr * KeySize;
        for (var i = 0; i <= nr; i++) {
          var p = ptrsOff + i * DiskChildSize;
          if (p + 4 > image.Length) break;
          var child = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(p));
          if (child > 0) Walk(child);
        }
        return;
      }
      items.AddRange(ParseLeaf(image, off));
    }
    Walk(rootBlock);
    return (items, treeBlocks);
  }

  private static List<Item> ParseLeaf(byte[] image, int leafOff) {
    var nrItems = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(leafOff + 2));
    var list = new List<Item>(nrItems);
    for (var i = 0; i < nrItems; i++) {
      var ih = leafOff + BlockHeadSize + i * ItemHeaderSize;
      var dirId = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(ih + 0));
      var objId = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(ih + 4));
      var offV2 = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(ih + 8));
      var offV1 = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(ih + 8));
      var uniqV1 = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(ih + 12));
      var uField = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(ih + 16));
      var ilen = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(ih + 18));
      var iloc = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(ih + 20));
      var keyFmt = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(ih + 22));
      var body = image.AsSpan(leafOff + iloc, ilen).ToArray();
      var type = ResolveItemType(offV2, uniqV1);
      list.Add(new Item {
        DirId = dirId, ObjectId = objId, OffsetV2 = offV2, KeyFormat = keyFmt,
        UField = uField, Body = body, ItemType = type, OffsetV1 = offV1, UniquenessV1 = uniqV1,
      });
    }
    return list;
  }

  private static int ResolveItemType(ulong offV2, uint uniqV1) {
    var typeV2 = (uint)(offV2 >> 60);
    if (typeV2 == 0 || typeV2 == 15) {
      return uniqV1 switch { 0u => 0, 0xfffffffeu => 1, 0xffffffffu => 2, 500u => 3, _ => -1 };
    }
    return (int)typeV2;
  }

  private static void WriteLeaf(Span<byte> blk, List<Item> items, ReadOnlySpan<byte> rightDelimKey) {
    blk.Clear();
    var nrItems = items.Count;
    var dataEnd = BlockSize;
    for (var i = 0; i < nrItems; i++) {
      var it = items[i];
      dataEnd -= it.Body.Length;
      var loc = dataEnd;
      var ihEnd = BlockHeadSize + (i + 1) * ItemHeaderSize;
      if (loc < ihEnd)
        throw new InvalidOperationException("ReiserFsInPlaceAdder: leaf overflow during serialise.");
      it.Body.CopyTo(blk[loc..]);

      var ih = blk[(BlockHeadSize + i * ItemHeaderSize)..];
      BinaryPrimitives.WriteUInt32LittleEndian(ih[0..], it.DirId);
      BinaryPrimitives.WriteUInt32LittleEndian(ih[4..], it.ObjectId);
      if (it.KeyFormat == 0) {
        // KEY_FORMAT_1: write offset_v1 + uniqueness derived from the item type.
        var (offV1, uniq) = KeyFormat1Bytes(it);
        BinaryPrimitives.WriteUInt32LittleEndian(ih[8..], offV1);
        BinaryPrimitives.WriteUInt32LittleEndian(ih[12..], uniq);
      } else {
        BinaryPrimitives.WriteUInt64LittleEndian(ih[8..], it.OffsetV2);
      }
      BinaryPrimitives.WriteUInt16LittleEndian(ih[16..], it.UField);
      BinaryPrimitives.WriteUInt16LittleEndian(ih[18..], (ushort)it.Body.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(ih[20..], (ushort)loc);
      BinaryPrimitives.WriteUInt16LittleEndian(ih[22..], it.KeyFormat);
    }

    var itemsEnd = BlockHeadSize + nrItems * ItemHeaderSize;
    var freeSpace = dataEnd - itemsEnd;
    BinaryPrimitives.WriteUInt16LittleEndian(blk[0..], 1); // leaf level
    BinaryPrimitives.WriteUInt16LittleEndian(blk[2..], (ushort)nrItems);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[4..], (ushort)freeSpace);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[6..], 0);
    rightDelimKey[..16].CopyTo(blk[8..]);
  }

  // For items we BUILT (not parsed) the OffsetV1/UniquenessV1 fields aren't set;
  // derive the KEY_FORMAT_1 bytes from the type + offset. For PARSED items we
  // preserve their original raw values.
  private static (uint OffV1, uint Uniq) KeyFormat1Bytes(Item it) {
    if (it.UniquenessV1 != 0 || it.OffsetV1 != 0) {
      // Parsed item — keep its on-disk bytes verbatim.
      // (A genuine SD has offset 0 / uniqueness 0; this branch only fires for
      // parsed non-SD items, whose recorded fields are authoritative.)
      return (it.OffsetV1, it.UniquenessV1);
    }
    var off = (uint)(it.OffsetV2 & 0x0FFFFFFFFFFFFFFFUL);
    var uniq = it.ItemType switch {
      0 => 0u, 1 => 0xfffffffeu, 2 => 0xffffffffu, 3 => 500u, _ => 0u,
    };
    return (off, uniq);
  }

  private static int CompareKeys(Item a, Item b) {
    if (a.DirId != b.DirId) return a.DirId.CompareTo(b.DirId);
    if (a.ObjectId != b.ObjectId) return a.ObjectId.CompareTo(b.ObjectId);
    return a.OffsetV2.CompareTo(b.OffsetV2);
  }

  private static readonly byte[] MaxKey = [
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
  ];

  // ── Directory entries ───────────────────────────────────────────────────────

  private sealed record DirEntry(string Name, uint PointedDirId, uint PointedObjId, uint DehOffset);

  // Finds the FIRST DIRENTRY item of the (dirId, objId) directory. A directory
  // whose entries spill across several DIRENTRY items has more than one; callers
  // that mutate entries use ReadAllDirEntries + ReplaceDirentItems to re-chunk.
  private static int FindDirentItem(List<Item> items, uint dirId, uint objId) =>
    items.FindIndex(x => x.ItemType == 3 && x.DirId == dirId && x.ObjectId == objId);

  private static int FindSdItem(List<Item> items, uint dirId, uint objId) =>
    items.FindIndex(x => x.ItemType == 0 && x.DirId == dirId && x.ObjectId == objId);

  /// <summary>
  /// Resolves the directory that will hold the final path segment, creating any
  /// missing intermediate directory objects (their stat_data + "."/".." dirents)
  /// in place. Returns the parent directory's key (its own dir_id and objectid)
  /// and the final leaf name. For a single-segment path this is just the root
  /// directory (1, 2). Throws when a path component collides with an existing
  /// FILE (not a directory) or when a target directory's dirent has spilled
  /// across multiple items (deferred to rebuild).
  /// </summary>
  private static (uint ParentDirId, uint ParentObjId, string LeafName) ResolveOrCreateParent(
    byte[] image, List<Item> items, string flatPath) {
    var segments = flatPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0)
      throw new ArgumentException("empty path", nameof(flatPath));

    // Walk down from the root, resolving / creating each intermediate dir.
    uint curDirId = RootParentObjectId, curObjId = RootObjectId; // "/" key
    for (var s = 0; s < segments.Length - 1; s++) {
      var seg = segments[s];
      if (FindDirentItem(items, curDirId, curObjId) < 0)
        throw new NotSupportedException("ReiserFS in-place add: parent DIRENTRY not found.");

      var entries = ReadAllDirEntries(items, curDirId, curObjId);
      var match = entries.FirstOrDefault(e => string.Equals(e.Name, seg, StringComparison.Ordinal));
      if (match != null) {
        // Existing component — it MUST be a directory.
        var childObjId = match.PointedObjId;
        var childSd = FindSdItem(items, curObjId, childObjId);
        if (childSd < 0)
          throw new NotSupportedException("ReiserFS in-place add: intermediate component SD missing.");
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(items[childSd].Body.AsSpan(0));
        if ((mode & 0xF000) != 0x4000)
          throw new NotSupportedException("ReiserFS in-place add: path component is a file, not a directory.");
        curDirId = curObjId;
        curObjId = childObjId;
        continue;
      }

      // Create a new directory object `seg` under (curDirId, curObjId).
      var newObjId = AllocateObjectId(image);
      CreateDirectory(items, parentKeyDirId: curDirId, parentObjId: curObjId,
        newDirObjId: newObjId, name: seg);
      curDirId = curObjId;
      curObjId = newObjId;
    }

    return (curDirId, curObjId, segments[^1]);
  }

  /// <summary>
  /// Creates a new EMPTY directory object in place: its stat_data, its DIRENTRY
  /// item with "." (→ itself) and ".." (→ parent), and a new entry for it in the
  /// parent directory's DIRENTRY item. Bumps the parent SD's nlink (the new
  /// subdirectory's ".." adds a link back) and the parent SD's size.
  /// </summary>
  private static void CreateDirectory(
    List<Item> items, uint parentKeyDirId, uint parentObjId, uint newDirObjId, string name) {
    // New directory's own key is (parentObjId, newDirObjId).
    var sd = new byte[SdV2Size];
    var dotEntries = new List<DirEntry> {
      new(".", parentObjId, newDirObjId, 1),
      new("..", parentKeyDirId, parentObjId, 2),
    };
    AssignAscendingOffsets(dotEntries);
    var dotBody = BuildDirEntryBody(dotEntries, out var dotFirstOffset);
    WriteStatDataV2(sd, mode: 0x41ED, nlink: 2, size: (ulong)dotBody.Length,
      blocks: (uint)SectorsPerBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(sd.AsSpan(SdBlocksOffset), (uint)((dotBody.Length + 511) / 512));

    var newSdItem = new Item {
      DirId = parentObjId, ObjectId = newDirObjId,
      OffsetV2 = TypeStatDataV2 | 0u, KeyFormat = 1, UField = 0, Body = sd, ItemType = 0,
    };
    var newDirentItem = new Item {
      DirId = parentObjId, ObjectId = newDirObjId,
      OffsetV2 = TypeDirentryV2 | dotFirstOffset, KeyFormat = 0,
      UField = (ushort)dotEntries.Count, Body = dotBody, ItemType = 3,
    };
    foreach (var it in new[] { newSdItem, newDirentItem }) {
      var idx = items.FindIndex(x => CompareKeys(x, it) > 0);
      if (idx < 0) items.Add(it); else items.Insert(idx, it);
    }

    // Add the entry into the parent's directory (chunked across items as needed).
    var parentEntries = ReadAllDirEntries(items, parentKeyDirId, parentObjId);
    parentEntries.Add(new DirEntry(name, parentObjId, newDirObjId, HashValueR5(name)));
    var growth = ReplaceDirentItems(items, parentKeyDirId, parentObjId, parentEntries);
    UpdateDirSize(items, parentKeyDirId, parentObjId, growth);
    BumpNlink(items, parentKeyDirId, parentObjId, +1);
  }

  /// <summary>Adjusts the (dirId, objId) stat-data sd_nlink by <paramref name="delta"/>.</summary>
  private static void BumpNlink(List<Item> items, uint dirId, uint objId, int delta) {
    var idx = FindSdItem(items, dirId, objId);
    if (idx < 0) return;
    var body = (byte[])items[idx].Body.Clone();
    var nlink = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
    nlink = (uint)((long)nlink + delta);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), nlink);
    items[idx] = items[idx] with { Body = body };
  }

  /// <summary>
  /// Removes all items of object (dirId-of-parent=<paramref name="parentObjId"/>,
  /// objectid=<paramref name="objId"/>) from the list and frees any INDIRECT
  /// data blocks in the bitmap (zeroing them when <paramref name="wipeData"/>).
  /// Used by replace-by-name and remove.
  /// </summary>
  private static void RemoveObjectItems(ref byte[] image, List<Item> items, uint parentObjId, uint objId, bool wipeData) {
    var freed = new List<int>();
    foreach (var it in items) {
      if (it.DirId != parentObjId || it.ObjectId != objId || it.ItemType != 1) continue;
      for (var p = 0; p + 4 <= it.Body.Length; p += 4) {
        var ptr = (int)BinaryPrimitives.ReadUInt32LittleEndian(it.Body.AsSpan(p));
        if (ptr > 0) freed.Add(ptr);
      }
    }
    items.RemoveAll(x => x.DirId == parentObjId && x.ObjectId == objId);
    if (freed.Count == 0) return;
    foreach (var b in freed) {
      ClearBitmapBit(image, b);
      if (wipeData) {
        var blkOff = (long)b * BlockSize;
        if (blkOff >= 0 && blkOff + BlockSize <= image.Length)
          image.AsSpan((int)blkOff, BlockSize).Clear();
      }
    }
    AdjustFreeBlocks(image, freed.Count);
  }


  private static List<DirEntry> ParseDirEntries(Item item) {
    var count = item.UField;
    var body = item.Body;
    var result = new List<DirEntry>(count);
    for (var e = 0; e < count; e++) {
      var deh = e * DehSize;
      if (deh + DehSize > body.Length) break;
      var off = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(deh + 0));
      var pdir = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(deh + 4));
      var pobj = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(deh + 8));
      var loc = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(deh + 12));
      int nameEnd = e == 0 ? body.Length
        : BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan((e - 1) * DehSize + 12));
      var k = loc;
      while (k < nameEnd && k < body.Length && body[k] != 0) k++;
      var name = Encoding.UTF8.GetString(body, loc, k - loc);
      result.Add(new DirEntry(name, pdir, pobj, off));
    }
    return result;
  }

  // Re-assign ascending deh_offsets after sorting (matches the writer / kernel
  // generation-counter disambiguation for hash collisions).
  private static void AssignAscendingOffsets(List<DirEntry> entries) {
    entries.Sort((a, b) => a.DehOffset.CompareTo(b.DehOffset));
    for (var i = 1; i < entries.Count; i++)
      if (entries[i].DehOffset <= entries[i - 1].DehOffset)
        entries[i] = entries[i] with { DehOffset = entries[i - 1].DehOffset + 1 };
  }

  // Maximum byte size of one DIRENTRY item body (mirrors ReiserFsWriter): leaf
  // payload minus one item_head minus headroom for a neighbouring SD item.
  private const int MaxDirItemBody = BlockSize - BlockHeadSize - ItemHeaderSize - 256;

  /// <summary>
  /// Replaces every DIRENTRY item of directory (dirId, objId) in
  /// <paramref name="items"/> with freshly chunked DIRENTRY items built from
  /// <paramref name="entries"/> (entry offsets reassigned ascending, chunked so
  /// no item body exceeds <see cref="MaxDirItemBody"/>). Returns the change in
  /// total directory-item byte size (new minus old) for the SD size update.
  /// </summary>
  private static int ReplaceDirentItems(List<Item> items, uint dirId, uint objId, List<DirEntry> entries) {
    var oldBytes = 0;
    foreach (var it in items)
      if (it.ItemType == 3 && it.DirId == dirId && it.ObjectId == objId)
        oldBytes += it.Body.Length;
    items.RemoveAll(x => x.ItemType == 3 && x.DirId == dirId && x.ObjectId == objId);

    AssignAscendingOffsets(entries);
    var newBytes = 0;
    var start = 0;
    while (start < entries.Count) {
      var bodyLen = 0;
      var end = start;
      while (end < entries.Count) {
        var slot = DehSize + RoundUp8(Encoding.UTF8.GetByteCount(entries[end].Name));
        if (end > start && bodyLen + slot > MaxDirItemBody) break;
        bodyLen += slot;
        end++;
      }
      var chunk = entries.GetRange(start, end - start);
      var body = BuildDirEntryBody(chunk, out var firstOffset);
      newBytes += body.Length;
      var item = new Item {
        DirId = dirId, ObjectId = objId,
        OffsetV2 = TypeDirentryV2 | firstOffset, KeyFormat = 0,
        UField = (ushort)chunk.Count, Body = body, ItemType = 3,
      };
      var idx = items.FindIndex(x => CompareKeys(x, item) > 0);
      if (idx < 0) items.Add(item); else items.Insert(idx, item);
      start = end;
    }
    return newBytes - oldBytes;
  }

  /// <summary>Reads every entry of directory (dirId, objId) across all its DIRENTRY items, in key order.</summary>
  private static List<DirEntry> ReadAllDirEntries(List<Item> items, uint dirId, uint objId) {
    var result = new List<DirEntry>();
    foreach (var it in items)
      if (it.ItemType == 3 && it.DirId == dirId && it.ObjectId == objId)
        result.AddRange(ParseDirEntries(it));
    return result;
  }

  private static byte[] BuildDirEntryBody(List<DirEntry> sorted, out uint firstOffset) {
    var entryCount = sorted.Count;
    var slotLengths = new int[entryCount];
    var totalNamesLen = 0;
    for (var i = 0; i < entryCount; i++) {
      slotLengths[i] = RoundUp8(Encoding.UTF8.GetByteCount(sorted[i].Name));
      totalNamesLen += slotLengths[i];
    }
    var bodyLen = entryCount * DehSize + totalNamesLen;
    var body = new byte[bodyLen];
    var locations = new ushort[entryCount];
    var nameRunningEnd = bodyLen;
    for (var i = 0; i < entryCount; i++) {
      var slotStart = nameRunningEnd - slotLengths[i];
      Encoding.UTF8.GetBytes(sorted[i].Name).CopyTo(body.AsSpan(slotStart));
      locations[i] = (ushort)slotStart;
      nameRunningEnd = slotStart;
    }
    for (var i = 0; i < entryCount; i++) {
      var off = i * DehSize;
      BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(off + 0), sorted[i].DehOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(off + 4), sorted[i].PointedDirId);
      BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(off + 8), sorted[i].PointedObjId);
      BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(off + 12), locations[i]);
      BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(off + 14), 4); // DEH_Visible2
    }
    firstOffset = sorted[0].DehOffset;
    return body;
  }

  // ── Stat data ───────────────────────────────────────────────────────────────

  private static void WriteStatDataV2(Span<byte> sd, ushort mode, uint nlink, ulong size, uint blocks) {
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt16LittleEndian(sd[0..], mode);
    BinaryPrimitives.WriteUInt16LittleEndian(sd[2..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(sd[4..], nlink);
    BinaryPrimitives.WriteUInt64LittleEndian(sd[8..], size);
    BinaryPrimitives.WriteUInt32LittleEndian(sd[16..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(sd[20..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(sd[24..], now);
    BinaryPrimitives.WriteUInt32LittleEndian(sd[28..], now);
    BinaryPrimitives.WriteUInt32LittleEndian(sd[32..], now);
    BinaryPrimitives.WriteUInt32LittleEndian(sd[36..], blocks);
    BinaryPrimitives.WriteUInt32LittleEndian(sd[40..], 0);
  }

  // ── R5 hash (mirrors ReiserFsWriter) ──────────────────────────────────────────

  private static uint HashValueR5(string name) {
    var r5 = R5HashRaw(Encoding.UTF8.GetBytes(name)) & 0x7fffff80u;
    return r5 == 0 ? 128u : r5;
  }

  private static uint R5HashRaw(ReadOnlySpan<byte> msg) {
    uint a = 0;
    for (var i = 0; i < msg.Length; i++) {
      var b = (sbyte)msg[i];
      a += (uint)(b << 4);
      a += (uint)(b >> 4);
      a *= 11;
    }
    return a;
  }

  private static int RoundUp8(int v) => (v + 7) & ~7;
}
