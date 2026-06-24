#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ReiserFs;

// ─────────────────────────────────────────────────────────────────────────────
// Genuine in-place ReiserFS v3.6 add.
//
// Inserts a single file into the ROOT directory by editing ONLY the structures
// the change touches — it does NOT re-emit the whole image:
//   * allocates a fresh objectid past the superblock's objectid-map range,
//   * builds the file's STAT_DATA item and its body item (DIRECT for small
//     bodies tail-packed inside the formatted leaf, INDIRECT for large bodies
//     whose payload lives in dedicated data blocks appended past the tree),
//   * splices those items plus the new R5-hashed directory entry into the leaf
//     that holds the root directory, re-packing only that leaf's body (item
//     heads forward, bodies end-backward),
//   * grows the image (appends data blocks) and flips the corresponding bits in
//     the on-disk bitmap, bumps s_block_count / s_free_blocks accordingly,
//   * grows the root SD's sd_size (directory byte size) and the objectid map.
//
// Every OTHER leaf, every existing item body, and every existing file data
// block stays byte-identical at its original offset. reiserfsck --check must
// report the result clean.
//
// Cases this path does NOT handle (the caller falls back to the verified
// rebuild in ReiserFsModifier):
//   * tree_height > 2 (multi-leaf images with an internal root) — descent and
//     internal-node key/disk_child maintenance not implemented here,
//   * the target leaf would OVERFLOW (no room for the new items) — leaf split +
//     promotion not implemented here,
//   * nested sub-directory targets (path contains '/') — would require creating
//     intermediate directory objects.
// In all three the adder throws NotSupportedException and the modifier rebuilds.
//
// References: reiserfsprogs reiserfscore/node_formats.c (item head, dir entry,
// SD layout), reiserfslib.c (reiserfs_add_entry / hash_value), bitmap.c.
// Field offsets mirror ReiserFsWriter / ReiserFsReader exactly.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Genuine in-place add for ReiserFS v3.6 images — inserts a file into the root
/// directory without relocating any existing data. Throws
/// <see cref="NotSupportedException"/> for structural cases it does not handle so
/// the caller can fall back to the rebuild path.
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
    if (flat.Contains('/'))
      throw new NotSupportedException("ReiserFS in-place add: nested sub-directory targets use rebuild.");
    if (Encoding.UTF8.GetByteCount(flat) > 200)
      throw new NotSupportedException("ReiserFS in-place add: over-long name uses rebuild.");

    var sb = SuperblockOff;
    var treeHeight = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(sb + Off_TreeHeight));
    if (treeHeight != 2)
      throw new NotSupportedException(
        $"ReiserFS in-place add: tree_height={treeHeight} (multi-leaf) uses rebuild.");

    var rootBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sb + Off_RootBlock));
    var blockCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sb + Off_BlockCount));
    var leafOff = rootBlock * BlockSize;
    if (leafOff < 0 || leafOff + BlockSize > image.Length)
      throw new NotSupportedException("ReiserFS in-place add: root block out of range.");

    var level = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(leafOff));
    if (level != 1)
      throw new NotSupportedException("ReiserFS in-place add: root is not a leaf.");

    // Parse the root leaf's items into an editable list.
    var items = ParseLeaf(image, leafOff);

    // Replace-by-name: if the root directory already has an entry with this name,
    // this path can't cleanly free its blocks/items, so defer to rebuild.
    var rootDirent = FindRootDirentItem(items);
    if (rootDirent < 0)
      throw new NotSupportedException("ReiserFS in-place add: root DIRENTRY item not found.");
    var existingEntries = ParseDirEntries(items[rootDirent]);
    if (existingEntries.Any(e => string.Equals(e.Name, flat, StringComparison.Ordinal)))
      throw new NotSupportedException("ReiserFS in-place add: replace-by-name uses rebuild.");

    // Allocate a fresh objectid past the current map range.
    var newObjId = AllocateObjectId(image);

    // Decide DIRECT vs INDIRECT. INDIRECT needs dedicated data blocks appended
    // to the image; DIRECT bodies tail-pack inside the leaf.
    var useIndirect = data.Length > MaxDirectBody;
    var blockCountNeeded = useIndirect ? (data.Length + BlockSize - 1) / BlockSize : 0;

    // ── Build the new items ──────────────────────────────────────────────────
    // STAT_DATA for the new file. sd_blocks computed below once placement known.
    var sd = new byte[SdV2Size];
    WriteStatDataV2(sd, mode: 0x81A4, nlink: 1, size: (ulong)data.Length,
      blocks: useIndirect ? (uint)(blockCountNeeded * SectorsPerBlock) : 0u);
    var sdItem = new Item {
      DirId = RootObjectId, ObjectId = newObjId,
      OffsetV2 = TypeStatDataV2 | 0u, KeyFormat = 1, UField = 0, Body = sd, ItemType = 0,
    };

    Item? bodyItem = null;
    byte[]? indirectBody = null; // placeholder pointer array, patched after sizing
    if (data.Length > 0) {
      if (!useIndirect) {
        bodyItem = new Item {
          DirId = RootObjectId, ObjectId = newObjId,
          OffsetV2 = TypeDirectV2 | 1u, KeyFormat = 1, UField = 0, Body = data, ItemType = 2,
        };
      } else {
        indirectBody = new byte[blockCountNeeded * 4];
        bodyItem = new Item {
          DirId = RootObjectId, ObjectId = newObjId,
          OffsetV2 = TypeIndirectV2 | 1u, KeyFormat = 1, UField = 0, Body = indirectBody, ItemType = 1,
        };
      }
    }

    // DIRECT-only files store their tail inside the (shared) leaf — sd_blocks is
    // one filesystem block per distinct leaf holding the body. Here the body
    // rides the root leaf → 1 block.
    if (!useIndirect && data.Length > 0)
      BinaryPrimitives.WriteUInt32LittleEndian(sd.AsSpan(SdBlocksOffset), (uint)SectorsPerBlock);

    // ── New dirent into the root DIRENTRY item ─────────────────────────────────
    // deh points at the child's SD key (dir_id = root objid 2, objectid = new id).
    var newEntry = new DirEntry(flat, RootObjectId, newObjId, HashValueR5(flat));
    var mergedEntries = new List<DirEntry>(existingEntries) { newEntry };
    AssignAscendingOffsets(mergedEntries);
    var newDirentBody = BuildDirEntryBody(mergedEntries, out var firstOffset);

    // The dirent item must still be the directory's only DIRENTRY item (single
    // leaf, no spill). If the rebuilt body would exceed the leaf budget the
    // directory item would have to split — defer to rebuild.
    var rootDirentItem = items[rootDirent];
    var dirItemGrowth = newDirentBody.Length - rootDirentItem.Body.Length;
    items[rootDirent] = rootDirentItem with {
      Body = newDirentBody, UField = (ushort)mergedEntries.Count,
      OffsetV2 = TypeDirentryV2 | firstOffset,
    };

    // ── Insert the new items in key order ──────────────────────────────────────
    var insertList = new List<Item> { sdItem };
    if (bodyItem != null) insertList.Add(bodyItem);
    foreach (var it in insertList) {
      var idx = items.FindIndex(x => CompareKeys(x, it) > 0);
      if (idx < 0) items.Add(it); else items.Insert(idx, it);
    }

    // ── Grow the root SD's sd_size (directory byte count) ───────────────────────
    var rootSdIdx = items.FindIndex(x => x.ItemType == 0 && x.DirId == RootParentObjectId && x.ObjectId == RootObjectId);
    if (rootSdIdx >= 0) {
      var rsd = items[rootSdIdx].Body;
      if (rsd.Length >= SdSizeOffset + 8) {
        var oldSize = BinaryPrimitives.ReadUInt64LittleEndian(rsd.AsSpan(SdSizeOffset));
        var newDirSize = (ulong)((long)oldSize + dirItemGrowth);
        // sd_blocks for a directory = ceil(dir bytes / 512).
        var rsdCopy = (byte[])rsd.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(rsdCopy.AsSpan(SdSizeOffset), newDirSize);
        BinaryPrimitives.WriteUInt32LittleEndian(rsdCopy.AsSpan(SdBlocksOffset), (uint)((newDirSize + 511) / 512));
        items[rootSdIdx] = items[rootSdIdx] with { Body = rsdCopy };
      }
    }

    // ── Check the leaf still fits ──────────────────────────────────────────────
    var payload = 0;
    foreach (var it in items) payload += it.Body.Length + ItemHeaderSize;
    if (payload > BlockSize - BlockHeadSize)
      throw new NotSupportedException(
        $"ReiserFS in-place add: root leaf would overflow ({payload} > {BlockSize - BlockHeadSize}); split uses rebuild.");

    // ── Grow the image for INDIRECT data blocks, fill block pointers ────────────
    var working = image;
    if (useIndirect) {
      var firstDataBlock = blockCount; // append right after the current last block
      var newTotalBlocks = blockCount + blockCountNeeded;
      working = new byte[(long)newTotalBlocks * BlockSize];
      Buffer.BlockCopy(image, 0, working, 0, image.Length);

      for (var i = 0; i < blockCountNeeded; i++)
        BinaryPrimitives.WriteUInt32LittleEndian(indirectBody!.AsSpan(i * 4), (uint)(firstDataBlock + i));
      for (var i = 0; i < blockCountNeeded; i++) {
        var srcOff = i * BlockSize;
        var copyLen = Math.Min(BlockSize, data.Length - srcOff);
        if (copyLen > 0)
          Array.Copy(data, srcOff, working, (long)(firstDataBlock + i) * BlockSize, copyLen);
      }

      // Mark the appended data blocks used in the bitmap; bump counters.
      MarkBitmapRange(working, firstDataBlock, blockCountNeeded, newTotalBlocks);
      BinaryPrimitives.WriteUInt32LittleEndian(working.AsSpan(sb + Off_BlockCount), (uint)newTotalBlocks);
      BinaryPrimitives.WriteUInt32LittleEndian(working.AsSpan(sb + Off_FreeBlocks), 0u);
      // s_bmap_nr stays 1 as long as the image fits a single bitmap block.
      var bmapNr = (newTotalBlocks + (BlockSize * 8) - 1) / (BlockSize * 8);
      if (bmapNr > 1)
        throw new NotSupportedException("ReiserFS in-place add: image would need more than one bitmap block.");
    }

    // ── Re-pack the root leaf in place ──────────────────────────────────────────
    WriteLeaf(working.AsSpan(leafOff, BlockSize), items, MaxKey);

    return working;
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
    if (flat.Contains('/'))
      throw new NotSupportedException("ReiserFS in-place remove: nested targets use rebuild.");

    var sb = SuperblockOff;
    var treeHeight = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(sb + Off_TreeHeight));
    if (treeHeight != 2)
      throw new NotSupportedException("ReiserFS in-place remove: multi-leaf image uses rebuild.");

    var rootBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sb + Off_RootBlock));
    var leafOff = rootBlock * BlockSize;
    if (leafOff < 0 || leafOff + BlockSize > image.Length)
      throw new NotSupportedException("ReiserFS in-place remove: root block out of range.");
    if (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(leafOff)) != 1)
      throw new NotSupportedException("ReiserFS in-place remove: root is not a leaf.");

    var items = ParseLeaf(image, leafOff);
    var direntIdx = FindRootDirentItem(items);
    if (direntIdx < 0) throw new NotSupportedException("ReiserFS in-place remove: root DIRENTRY not found.");

    var entries = ParseDirEntries(items[direntIdx]);
    var target = entries.FirstOrDefault(e => string.Equals(e.Name, flat, StringComparison.Ordinal));
    if (target == null) throw new FileNotFoundException(flat);
    var objId = target.PointedObjId;

    // The entry must point at a FILE directly parented at root (dir_id == root
    // objid). A directory target would need recursive removal — defer.
    var sdIdx = items.FindIndex(x => x.ItemType == 0 && x.DirId == RootObjectId && x.ObjectId == objId);
    if (sdIdx < 0) throw new NotSupportedException("ReiserFS in-place remove: target SD not at root.");
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(items[sdIdx].Body.AsSpan(0));
    if ((mode & 0xF000) == 0x4000)
      throw new NotSupportedException("ReiserFS in-place remove: directory target uses rebuild.");

    // Collect this object's body items; free any INDIRECT data blocks.
    var working = image;
    var freedBlocks = new List<int>();
    foreach (var it in items) {
      if (it.DirId != RootObjectId || it.ObjectId != objId) continue;
      if (it.ItemType != 1) continue; // only INDIRECT carries off-tree data blocks
      for (var p = 0; p + 4 <= it.Body.Length; p += 4) {
        var ptr = (int)BinaryPrimitives.ReadUInt32LittleEndian(it.Body.AsSpan(p));
        if (ptr > 0) freedBlocks.Add(ptr);
      }
    }

    // Drop the object's SD + body items.
    items.RemoveAll(x => x.DirId == RootObjectId && x.ObjectId == objId);

    // Rebuild the dirent item without the target entry.
    var remaining = entries.Where(e => !ReferenceEquals(e, target)
      && !(e.PointedObjId == objId && e.PointedDirId == RootObjectId && e.Name == flat)).ToList();
    AssignAscendingOffsets(remaining);
    var newDirentBody = BuildDirEntryBody(remaining, out var firstOffset);
    var oldDirent = items[FindRootDirentItem(items)];
    var dirItemShrink = oldDirent.Body.Length - newDirentBody.Length;
    var direntIdx2 = FindRootDirentItem(items);
    items[direntIdx2] = oldDirent with {
      Body = newDirentBody, UField = (ushort)remaining.Count,
      OffsetV2 = TypeDirentryV2 | firstOffset,
    };

    // Shrink the root SD size / blocks.
    var rootSdIdx = items.FindIndex(x => x.ItemType == 0 && x.DirId == RootParentObjectId && x.ObjectId == RootObjectId);
    if (rootSdIdx >= 0) {
      var rsd = (byte[])items[rootSdIdx].Body.Clone();
      var oldSize = BinaryPrimitives.ReadUInt64LittleEndian(rsd.AsSpan(SdSizeOffset));
      var newDirSize = (ulong)((long)oldSize - dirItemShrink);
      BinaryPrimitives.WriteUInt64LittleEndian(rsd.AsSpan(SdSizeOffset), newDirSize);
      BinaryPrimitives.WriteUInt32LittleEndian(rsd.AsSpan(SdBlocksOffset), (uint)((newDirSize + 511) / 512));
      items[rootSdIdx] = items[rootSdIdx] with { Body = rsd };
    }

    // Free the INDIRECT data blocks in the bitmap (and optionally wipe them).
    // The image is NOT shrunk: the freed blocks become genuinely free space
    // (s_free_blocks grows), exactly as the kernel would leave them after a
    // delete. This keeps every surviving block at its original offset.
    if (freedBlocks.Count > 0) {
      var bmap = working.AsSpan(17 * BlockSize, BlockSize);
      foreach (var b in freedBlocks) {
        bmap[b >> 3] &= (byte)~(1 << (b & 7));
        var blkOff = (long)b * BlockSize;
        if (wipeData && blkOff >= 0 && blkOff + BlockSize <= working.Length)
          working.AsSpan((int)blkOff, BlockSize).Clear();
      }
      var free = BinaryPrimitives.ReadUInt32LittleEndian(working.AsSpan(sb + Off_FreeBlocks));
      BinaryPrimitives.WriteUInt32LittleEndian(working.AsSpan(sb + Off_FreeBlocks), free + (uint)freedBlocks.Count);
    }

    WriteLeaf(working.AsSpan(leafOff, BlockSize), items, MaxKey);
    return working;
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

  // ── Bitmap ────────────────────────────────────────────────────────────────

  private static void MarkBitmapRange(byte[] image, int firstBlock, int count, int totalBlocks) {
    var bmap = image.AsSpan(17 * BlockSize, BlockSize);
    for (var b = firstBlock; b < firstBlock + count; b++)
      bmap[b >> 3] |= (byte)(1 << (b & 7));
    // Tail-fill: bits from totalBlocks to the end of the partial byte, then full
    // trailing bytes, must all be 1 (reiserfsck "zero bit after last valid bit").
    for (var b = totalBlocks; (b & 7) != 0; b++)
      bmap[b >> 3] |= (byte)(1 << (b & 7));
    var lastValidByte = (totalBlocks + 7) / 8;
    for (var i = lastValidByte; i < BlockSize; i++)
      bmap[i] = 0xFF;
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

  private static int FindRootDirentItem(List<Item> items) =>
    items.FindIndex(x => x.ItemType == 3 && x.DirId == RootParentObjectId && x.ObjectId == RootObjectId);

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
