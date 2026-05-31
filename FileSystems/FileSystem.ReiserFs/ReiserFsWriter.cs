#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileSystem.ReiserFs;

// ─────────────────────────────────────────────────────────────────────────────
// ReiserFS 3.6 has NO block-level checksums by design.
//
// Unlike ext4 (crc32c metadata csums since Linux 3.6), btrfs (crc32c default),
// or ReiserFS v4 (Fletcher-4), the shipped v3.6 on-disk format stores no CRC
// in superblocks, block-heads, item headers, or stat-data — consistency is
// guaranteed only via the journal.
//
// References (kdave/reiserfsprogs mirror — Hans-Reiser-era authoritative code):
//   reiserfscore/node_formats.c   (item head, dir entry, hashes, SD layout)
//   reiserfscore/stree.c          (comp_keys, comp_short_keys)
//   reiserfscore/reiserfslib.c    (make_sure_root_dir_exists, create_dir_sd,
//                                  reiserfs_add_entry)
//   reiserfscore/bitmap.c         (reiserfs_fetch_ondisk_bitmap)
//   fsck/check_tree.c             (bad_pair, leaf_structure_check)
//   fsck/pass0.c                  (is_bad_item, is_bad_sd, is_bad_directory,
//                                  is_leaf_bad, leaf_structure_check)
//   include/reiserfs_fs.h         (key formats, EMPTY_DIR_SIZE, ROUND_UP,
//                                  uniqueness constants)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Writes a minimal but SPEC-COMPLIANT ReiserFS v3.6 filesystem image. Single
/// leaf block containing root SD + root DIRENTRY + per-file (SD + DIRECT)
/// items. Layout matches what reiserfsprogs' make_sure_root_dir_exists +
/// reiserfs_add_entry would produce.
/// </summary>
/// <remarks>
/// Kernel-reference offsets inside the 65536-byte-aligned superblock:
/// <code>
///   0  +  4  s_block_count
///   4  +  4  s_free_blocks
///   8  +  4  s_root_block
///  12  + 32  s_journal (journal_params, 8 × __le32)
///  44  +  2  s_blocksize
///  46  +  2  s_oid_maxsize
///  48  +  2  s_oid_cursize
///  50  +  2  s_umount_state
///  52  + 10  s_magic
///  62  +  2  s_fs_state
///  64  +  4  s_hash_function_code
///  68  +  2  s_tree_height
///  70  +  2  s_bmap_nr
///  72  +  2  s_version
///  74  +  2  s_reserved_for_journal
///  76  +  4  s_inode_generation
///  80  +  4  s_flags
///  84  + 16  s_uuid
/// 100  + 16  s_label
/// 116  +  2  s_mnt_count
/// 118  +  2  s_max_mnt_count
/// 120  +  4  s_lastcheck
/// 124  +  4  s_check_interval
/// 128  + 76  s_unused
/// 204  + .. objectid_map (packed pairs, cursize × 4 bytes)
/// </code>
/// Every block_head is 24 bytes: blk_level(2) + blk_nr_item(2) +
/// blk_free_space(2) + blk_reserved(2) + blk_right_delim_key(16).
/// </remarks>
public sealed class ReiserFsWriter {
  private const int BlockSize = 4096;
  private const int SuperblockOff = 65536; // block 16 at 4-KB blocksize
  private const int SuperblockSize = 204;  // SB_SIZE for v3.6
  private const int LeafLevel = 1;
  private const int ItemHeaderSize = 24;
  private const int BlockHeadSize = 24;
  private const int DehSize = 16;

  // SD sizes
  private const int SdV2Size = 44;

  // Key format codes (item_head.ih_key_format).
  private const ushort KeyFormat1 = 0; // v3.5 keys
  private const ushort KeyFormat2 = 1; // v3.6 keys

  // Uniqueness values for KEY_FORMAT_1 (offset_v1.k_uniqueness).
  private const uint V1SdUniqueness = 0u;
  private const uint V1DirentryUniqueness = 500u;
  private const uint V1DirectUniqueness = 0xFFFFFFFFu;

  // Type codes embedded in offset_v2 (top 4 bits) for KEY_FORMAT_2.
  private const ulong TypeStatDataV2 = 0UL << 60;     // type=0
  private const ulong TypeIndirectV2 = 1UL << 60;
  private const ulong TypeDirectV2 = 2UL << 60;
  private const ulong TypeDirentryV2 = 3UL << 60;

  // Special object IDs (kernel reiserfs_fs.h).
  private const uint RootParentObjectId = 1; // dir_id of "/" — used as parent
  private const uint RootObjectId = 2;       // objectid of "/" itself
  private const uint FirstUserObjectId = 100;

  // Hash codes (s_hash_function_code).
  private const uint R5Hash = 3;

  // Superblock magic for ReiserFS 3.6.
  private static readonly byte[] Magic36 = "ReIsEr2Fs"u8.ToArray();

  private readonly List<(string path, byte[] data)> _files = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    // Preserve the FULL relative path. Path components may carry '/' or '\'
    // separators; the tree builder in WriteTo materialises a real directory
    // object (SD with mode S_IFDIR + DIRENTRY item) for each intermediate
    // component, exactly as reiserfs_add_entry would.
    var normalised = name.Replace('\\', '/').Trim('/');
    _files.Add((normalised, data));
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    // Materialise the directory tree (root + intermediate dirs + files) into a
    // flat list of OBJECTS, each with a stable objectid. The root is object 2
    // (parent dir_id 1); every other object is assigned a user objectid from
    // FirstUserObjectId upward in the order it is first encountered.
    var tree = BuildTree();

    // Materialise every leaf item, sorted by reiserfs key (comp_keys). Large
    // directory items are already split across DIRENTRY-item boundaries by
    // BuildLeafItems so that no single item exceeds the leaf payload budget.
    var items = BuildLeafItems(tree);
    items.Sort(static (a, b) => CompareKeys(a, b));

    // Pack the sorted items into one or more formatted LEAF blocks. When more
    // than one leaf is needed an INTERNAL block (blk_level >= 2) is added above
    // them; its key array + disk_child pointers index the leaves and the
    // super_block root points at it (tree_height grows from 2 to 3).
    var leaves = PackLeaves(items);

    // Patch each object's sd_blocks now that the final leaf placement is known.
    // reiserfsck (vpf-10680 / vpf-10690) requires the StatData block count to
    // equal the number of distinct FORMATTED leaf blocks that hold the object's
    // BODY items (DIRENTRY for directories, DIRECT/INDIRECT for files); the SD
    // item itself does not count. Objects whose body fits in a single leaf keep
    // blocks = 1, matching mkreiserfs' freshly-created root directory.
    PatchStatDataBlockCounts(leaves);

    // Layout (reiserfsprogs journal.c — journal MUST start at
    // (REISERFS_DISK_OFFSET_IN_BYTES / blocksize) + 2 = 16 + 2 = 18):
    //   blocks 0..15     = reserved for boot (up to 64 KB)
    //   block 16         = superblock
    //   block 17         = bitmap (one block covers 32 768 blocks)
    //   blocks 18..8209  = journal body (8192 blocks = JOURNAL_DEFAULT_SIZE)
    //   block 8210       = journal header
    //   block 8211..     = S+tree blocks (leaves, then internal block if any)
    // With a single leaf the tree is just that leaf (tree_height = 2). With
    // several leaves they occupy 8211..8211+L-1 and the internal block sits at
    // 8211+L (tree_height = 3, root_block = internal block).
    const int journalFirstBlock = 18;
    const int journalSize = 8192;                                      // JOURNAL_DEFAULT_SIZE
    const int journalHeaderBlock = journalFirstBlock + journalSize;    // 8210
    var firstTreeBlock = journalHeaderBlock + 1;                       // 8211
    var leafCount = leaves.Count;
    var hasInternal = leafCount > 1;
    // Leaves occupy [firstTreeBlock .. firstTreeBlock+leafCount-1]; the internal
    // block (when present) follows them and becomes the tree root.
    var internalBlockNum = firstTreeBlock + leafCount;                 // valid only if hasInternal
    var rootBlockNum = hasInternal ? internalBlockNum : firstTreeBlock;
    var treeHeight = (ushort)(hasInternal ? 3 : 2);
    var lastTreeBlock = hasInternal ? internalBlockNum : firstTreeBlock + leafCount - 1;
    var totalBlocks = lastTreeBlock + 1;
    var imageSize = totalBlocks * BlockSize;
    var image = new byte[imageSize];

    // For free-block accounting: blocks 0..lastTreeBlock are all in use.
    var usedBlocks = lastTreeBlock + 1;
    var freeBlocks = totalBlocks - usedBlocks;

    // ── Superblock ──────────────────────────────────────────────────────────
    var sb = image.AsSpan(SuperblockOff);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0..], (uint)totalBlocks);   // s_block_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[4..], (uint)freeBlocks);    // s_free_blocks
    BinaryPrimitives.WriteUInt32LittleEndian(sb[8..], (uint)rootBlockNum);  // s_root_block

    // journal_params @ +12 (32 bytes). Kernel struct journal_params:
    //   +0   jp_journal_1st_block     (must be 18 for 4 KB-block new-style sb)
    //   +4   jp_journal_dev           (0 = on-filesystem journal)
    //   +8   jp_journal_size          (>= JOURNAL_MIN_SIZE=512)
    //   +12  jp_journal_trans_max     (JOURNAL_TRANS_MAX_DEFAULT=1024)
    //   +16  jp_journal_magic         (random, non-zero)
    //   +20  jp_journal_max_batch     (JOURNAL_MAX_BATCH_DEFAULT=900)
    //   +24  jp_journal_max_commit_age(JOURNAL_MAX_COMMIT_AGE=30)
    //   +28  jp_journal_max_trans_age (JOURNAL_MAX_TRANS_AGE=30)
    BinaryPrimitives.WriteUInt32LittleEndian(sb[12..], journalFirstBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[16..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[20..], journalSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[24..], 1024);
    var magicBuf = new byte[4];
    RandomNumberGenerator.Fill(magicBuf);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[28..], BinaryPrimitives.ReadUInt32LittleEndian(magicBuf));
    BinaryPrimitives.WriteUInt32LittleEndian(sb[32..], 900);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], 30);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[40..], 30);

    BinaryPrimitives.WriteUInt16LittleEndian(sb[44..], BlockSize);    // s_blocksize
    // s_oid_maxsize per reiserfslib.c:reiserfs_create_journal — for 3.6:
    //   (block_size - SB_SIZE) / sizeof(__u32) / 2 * 2.
    var oidMaxsize = (ushort)(((BlockSize - SuperblockSize) / 4 / 2) * 2); // 972 for 4K
    BinaryPrimitives.WriteUInt16LittleEndian(sb[46..], oidMaxsize);   // s_oid_maxsize
    // We use objectids: 1 (root parent), 2 (root), then 100..100+m-1, where m
    // is the number of NON-root objects (intermediate directories + files).
    // The objectid map encodes used ranges as ascending pairs
    // [start, end_exclusive, start, end_exclusive, ...].
    //   marker1: 1..3 (id 1 and 2 used)             → {1, 3}
    //   marker2: 100..100+m (m IDs used)            → {100, 100+m}
    //   final entry must be > all used (acts as sentinel "everything else free").
    // cursize = number of __le32 entries in the map.
    var userObjectCount = tree.Objects.Count - 1; // exclude root (objid 2)
    var oidMap = new uint[userObjectCount > 0 ? 4 : 2];
    oidMap[0] = 1;
    oidMap[1] = 3; // ids 1, 2 used
    if (userObjectCount > 0) {
      oidMap[2] = FirstUserObjectId;
      oidMap[3] = (uint)(FirstUserObjectId + userObjectCount);
    }
    BinaryPrimitives.WriteUInt16LittleEndian(sb[48..], (ushort)oidMap.Length); // s_oid_cursize
    BinaryPrimitives.WriteUInt16LittleEndian(sb[50..], 1);            // s_umount_state = REISERFS_VALID_FS
    Magic36.CopyTo(sb[52..]);                                         // s_magic "ReIsEr2Fs\0"
    sb[52 + 9] = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(sb[62..], 0);            // s_fs_state = 0 (consistent)
    BinaryPrimitives.WriteUInt32LittleEndian(sb[64..], R5Hash);       // s_hash_function_code = R5
    BinaryPrimitives.WriteUInt16LittleEndian(sb[68..], treeHeight);   // s_tree_height (2 = single leaf, 3 = +internal)
    var bmapNr = (ushort)((totalBlocks + (BlockSize * 8) - 1) / (BlockSize * 8));
    BinaryPrimitives.WriteUInt16LittleEndian(sb[70..], bmapNr);       // s_bmap_nr
    BinaryPrimitives.WriteUInt16LittleEndian(sb[72..], 2);            // s_version = REISERFS_VERSION_2 (3.6)
    BinaryPrimitives.WriteUInt16LittleEndian(sb[74..], 0);            // s_reserved_for_journal
    BinaryPrimitives.WriteUInt32LittleEndian(sb[76..], 0);            // s_inode_generation
    BinaryPrimitives.WriteUInt32LittleEndian(sb[80..], 0);            // s_flags

    // s_uuid @ +84 — required non-zero for a valid superblock.
    var uuid = new byte[16];
    RandomNumberGenerator.Fill(uuid);
    uuid[6] = (byte)((uuid[6] & 0x0F) | 0x40); // v4 variant marker
    uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
    uuid.CopyTo(sb[84..]);

    // s_label @ +100 (16 bytes ASCII, zero-padded).
    Encoding.ASCII.GetBytes("worm").CopyTo(sb[100..]);
    // 116..204 zero by default; objectid map at +204.
    for (var i = 0; i < oidMap.Length; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(sb[(SuperblockSize + i * 4)..], oidMap[i]);

    // ── Bitmap block (17): mark all "in-use" blocks allocated ──────────────
    // Kernel reiserfscore/bitmap.c:reiserfs_fetch_ondisk_bitmap reads
    // (block_count + 7) / 8 bytes. For the trailing bytes within the bitmap
    // BLOCK that lie OUTSIDE the filesystem, the kernel requires every byte
    // == 0xFF (otherwise "Zero bit found... after the last valid bit").
    // Even within the last byte that contains valid bits, the bits beyond
    // s_block_count must be set to 1.
    var bmap = image.AsSpan(17 * BlockSize, BlockSize);
    // Mark blocks 0..lastTreeBlock used (boot + sb + bitmap + journal + tree).
    for (var b = 0; b <= lastTreeBlock; b++)
      bmap[b >> 3] |= (byte)(1 << (b & 7));
    // Tail-fill: from totalBlocks bit through end of bitmap block, every bit
    // must be 1. Set the remainder of the partial last byte.
    for (var b = totalBlocks; (b & 7) != 0; b++)
      bmap[b >> 3] |= (byte)(1 << (b & 7));
    // Then fill any whole trailing bytes inside the bitmap block.
    var lastValidByte = (totalBlocks + 7) / 8;
    for (var i = lastValidByte; i < BlockSize; i++)
      bmap[i] = 0xFF;

    // ── Journal header (block journalHeaderBlock) ─────────────────────────
    // Kernel struct reiserfs_journal_header:
    //   +0  __le32 j_last_flush_trans_id
    //   +4  __le32 j_first_unflushed_offset
    //   +8  __le32 j_mount_id
    //   +12 struct journal_params jh_journal   (32 bytes, mirrors sb copy)
    var jh = image.AsSpan(journalHeaderBlock * BlockSize, BlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(jh[0..], 0);        // last flushed trans id
    BinaryPrimitives.WriteUInt32LittleEndian(jh[4..], 0);        // first unflushed offset
    BinaryPrimitives.WriteUInt32LittleEndian(jh[8..], 0);        // mount id
    sb.Slice(12, 32).CopyTo(jh[12..]);

    // ── Leaf blocks ──────────────────────────────────────────────────────────
    // Per fsck/pass0.c:leaf_structure_check + reiserfscore/node_formats.c,
    // items inside a leaf are ordered by key (comp_keys ascending) and their
    // BODIES are packed from the END of the block toward the beginning, with
    // item[0]'s body at the highest location and item[N-1]'s at the lowest.
    // leaf_count_ih requires:  ih[i].location + ih[i].length == ih[i-1].location
    // and ih[0].location + ih[0].length == blocksize.
    //
    // Per fsck/check_tree.c:bad_pair the per-object items must appear in
    // (dir_id, objectid, offset, type) ascending order — for a directory that
    // is SD (offset 0) then DIRENTRY (offset 1); for a file SD then DIRECT.
    // PackLeaves already split the global sorted item list onto leaves keeping
    // that order; here we just serialise each leaf into its block.
    for (var li = 0; li < leafCount; li++) {
      var leafItems = leaves[li];
      var blockNum = firstTreeBlock + li;
      // Right-delimiting key: the key of the FIRST item of the next leaf, or
      // MAX_KEY for the rightmost leaf (no right sibling).
      var rdk = li + 1 < leafCount ? KeyOf(leaves[li + 1][0]) : MaxKey;
      WriteLeaf(image.AsSpan(blockNum * BlockSize, BlockSize), leafItems, rdk);
    }

    // ── Internal block ────────────────────────────────────────────────────────
    // reiserfscore/node_formats.c: an internal node holds blk_nr_item KEYS
    // (16 bytes each) followed by (blk_nr_item + 1) disk_child pointers
    // (struct disk_child = le32 dc_block_number + le16 dc_size + le16 reserved).
    // key[i] is the left-delimiting key of child[i+1]; comp_keys ordering across
    // the children is therefore preserved by the leaf order from PackLeaves.
    if (hasInternal)
      WriteInternal(image.AsSpan(internalBlockNum * BlockSize, BlockSize), leaves, firstTreeBlock);

    output.Write(image);
  }

  /// <summary>
  /// Serialises a single leaf block: block_head (blk_level = 1) + forward
  /// item_head array + bodies packed from the block end backward.
  /// <paramref name="rightDelimKey"/> is the 16-byte right-delimiting key.
  /// </summary>
  private static void WriteLeaf(Span<byte> blk, List<LeafItem> leafItems, ReadOnlySpan<byte> rightDelimKey) {
    var nrItems = leafItems.Count;
    var dataEnd = BlockSize;
    for (var i = 0; i < nrItems; i++) {
      var it = leafItems[i];
      dataEnd -= it.Body.Length;
      var loc = dataEnd;
      var ihEnd = BlockHeadSize + (i + 1) * ItemHeaderSize;
      if (loc < ihEnd)
        throw new InvalidOperationException(
          $"ReiserFsWriter: leaf block overflow — item {i} would collide with the item-head array.");
      it.Body.CopyTo(blk[loc..]);

      var ih = blk[(BlockHeadSize + i * ItemHeaderSize)..];
      BinaryPrimitives.WriteUInt32LittleEndian(ih[0..], it.DirId);
      BinaryPrimitives.WriteUInt32LittleEndian(ih[4..], it.ObjectId);
      // The offset_v1+uniqueness pair (KEY_FORMAT_1) and offset_v2 (KEY_FORMAT_2)
      // share the same 8 bytes; on disk we reproduce the exact bytes per format.
      if (it.KeyFormat == KeyFormat1) {
        BinaryPrimitives.WriteUInt32LittleEndian(ih[8..], it.OffsetV1);
        BinaryPrimitives.WriteUInt32LittleEndian(ih[12..], it.UniquenessV1);
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

    BinaryPrimitives.WriteUInt16LittleEndian(blk[0..], LeafLevel);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[2..], (ushort)nrItems);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[4..], (ushort)freeSpace);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[6..], 0); // blk_reserved
    rightDelimKey[..16].CopyTo(blk[8..]);
  }

  /// <summary>
  /// Serialises the internal block above the leaves: block_head (blk_level = 2)
  /// + a key for every child after the first + one disk_child per child.
  /// </summary>
  private static void WriteInternal(Span<byte> blk, List<List<LeafItem>> leaves, int firstLeafBlock) {
    var childCount = leaves.Count;
    var keyCount = childCount - 1; // blk_nr_item for an internal node

    BinaryPrimitives.WriteUInt16LittleEndian(blk[0..], 2);              // blk_level (internal)
    BinaryPrimitives.WriteUInt16LittleEndian(blk[2..], (ushort)keyCount);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[4..], 0);              // blk_free_space (unused by reader)
    BinaryPrimitives.WriteUInt16LittleEndian(blk[6..], 0);             // blk_reserved
    MaxKey.CopyTo(blk[8..]);                                            // right-delim key (tree root)

    // Keys: key[i] = left-delimiting key of child[i+1] = first item of leaf i+1.
    var keysOff = BlockHeadSize;
    for (var i = 0; i < keyCount; i++)
      KeyOf(leaves[i + 1][0]).CopyTo(blk[(keysOff + i * 16)..]);

    // disk_child pointers (8 bytes each) follow the keys. dc_size is the child's
    // USED space in bytes — reiserfsprogs B_CHILD_SIZE = MAX_CHILD_SIZE - free =
    // (blocksize - BLKH_SIZE) - blk_free_space = sum of all item bodies and their
    // item_heads in the child leaf. reiserfsck's bad_path test rejects any other
    // value.
    var ptrsOff = keysOff + keyCount * 16;
    for (var i = 0; i < childCount; i++) {
      var usedSpace = leaves[i].Sum(it => it.Body.Length + ItemHeaderSize);
      var dc = blk[(ptrsOff + i * 8)..];
      BinaryPrimitives.WriteUInt32LittleEndian(dc[0..], (uint)(firstLeafBlock + i)); // dc_block_number
      BinaryPrimitives.WriteUInt16LittleEndian(dc[4..], (ushort)usedSpace);          // dc_size (used bytes)
      BinaryPrimitives.WriteUInt16LittleEndian(dc[6..], 0);                          // dc reserved/padding
    }
  }

  // 16-byte MAX_KEY sentinel (all 0xFF) — "no right sibling".
  private static ReadOnlySpan<byte> MaxKey => [
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
  ];

  /// <summary>Returns the 16-byte on-disk key of a leaf item (dir_id, objectid, offset).</summary>
  private static byte[] KeyOf(LeafItem it) {
    var key = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(0), it.DirId);
    BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(4), it.ObjectId);
    if (it.KeyFormat == KeyFormat1) {
      BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(8), it.OffsetV1);
      BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(12), it.UniquenessV1);
    } else {
      BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8), it.OffsetV2);
    }
    return key;
  }

  /// <summary>
  /// Distributes the globally key-sorted item list across leaf blocks using a
  /// greedy first-fit: items are appended to the current leaf until the next
  /// one would overflow the leaf payload (block minus block_head, accounting
  /// for each item's item_head). A single item never spans leaves — large
  /// directory items were already split on entry boundaries by BuildLeafItems.
  /// </summary>
  private static List<List<LeafItem>> PackLeaves(List<LeafItem> items) {
    // Usable payload per leaf = block - block_head; each item costs its body
    // length + one item_head.
    const int leafPayload = BlockSize - BlockHeadSize;
    var leaves = new List<List<LeafItem>>();
    var current = new List<LeafItem>();
    var used = 0;
    foreach (var it in items) {
      var cost = it.Body.Length + ItemHeaderSize;
      if (cost > leafPayload)
        throw new InvalidOperationException(
          $"ReiserFsWriter: single item of {it.Body.Length} bytes exceeds the leaf payload; " +
          "indirect items for large file bodies are not implemented.");
      if (current.Count > 0 && used + cost > leafPayload) {
        leaves.Add(current);
        current = [];
        used = 0;
      }
      current.Add(it);
      used += cost;
    }
    if (current.Count > 0 || leaves.Count == 0)
      leaves.Add(current);
    return leaves;
  }

  // Offset of sd_blocks within a stat_data_v2 body.
  private const int SdBlocksOffset = 36;

  // 512-byte sectors per filesystem block — the unit of sd_blocks (st_blocks).
  private const int SectorsPerBlock = BlockSize / 512;

  /// <summary>
  /// Rewrites every object's <c>sd_blocks</c> in place once the leaf packing is
  /// fixed. ReiserFS records sd_blocks in 512-byte sectors and computes it
  /// differently per object type (matching reiserfsck vpf-10680 / vpf-10690):
  /// <list type="bullet">
  ///   <item>Regular files store tails (DIRECT items) inside shared formatted
  ///   leaves; each leaf that holds a piece of the body is charged one whole
  ///   filesystem block, i.e. <c>distinctLeaves × (blocksize / 512)</c>.</item>
  ///   <item>Directories charge their on-disk directory-item byte count rounded
  ///   up to 512-byte sectors, <c>ceil(totalDirEntryBytes / 512)</c>.</item>
  /// </list>
  /// Objects with no body item (empty files) get 0.
  /// </summary>
  private static void PatchStatDataBlockCounts(List<List<LeafItem>> leaves) {
    // Per object key: the SD body handle, the set of distinct leaves its DIRECT/
    // INDIRECT items occupy, the total DIRENTRY byte count, and whether the
    // object is a directory.
    var sdBodies = new Dictionary<(uint, uint), byte[]>();
    var fileLeaves = new Dictionary<(uint, uint), HashSet<int>>();
    var dirBytes = new Dictionary<(uint, uint), int>();
    for (var li = 0; li < leaves.Count; li++) {
      foreach (var it in leaves[li]) {
        var key = (it.DirId, it.ObjectId);
        switch (it.ItemType) {
          case 0: // STAT_DATA
            sdBodies[key] = it.Body;
            break;
          case 3: // DIRENTRY — directory body, counted by byte size
            dirBytes[key] = dirBytes.GetValueOrDefault(key) + it.Body.Length;
            break;
          default: // DIRECT (2) / INDIRECT (1) — file body, counted by leaf
            if (!fileLeaves.TryGetValue(key, out var set))
              fileLeaves[key] = set = [];
            set.Add(li);
            break;
        }
      }
    }

    foreach (var (key, sd) in sdBodies) {
      if (sd.Length < SdBlocksOffset + 4) continue;
      uint blocks;
      if (dirBytes.TryGetValue(key, out var bytes))
        blocks = (uint)((bytes + 511) / 512);
      else if (fileLeaves.TryGetValue(key, out var set))
        blocks = (uint)(set.Count * SectorsPerBlock);
      else
        blocks = 0u;
      BinaryPrimitives.WriteUInt32LittleEndian(sd.AsSpan(SdBlocksOffset), blocks);
    }
  }

  // ── Tree model ──────────────────────────────────────────────────────────

  /// <summary>
  /// A filesystem object materialised from the input paths: the root directory,
  /// every intermediate directory, and every file. Directories carry their
  /// child entries; files carry their inline body.
  /// </summary>
  private sealed class TreeObject {
    public required uint ObjectId;        // own objectid
    public required uint ParentObjectId;  // parent's objectid (== this object's key dir_id)
    public required bool IsDirectory;
    public required string Name;          // leaf name within its parent ("" for root)
    public byte[] Data = [];              // file body (files only)
    public readonly List<(string Name, uint ChildObjectId)> Children = []; // dirs only
  }

  private sealed class TreeModel {
    public required TreeObject Root;
    public required List<TreeObject> Objects; // Root first, then children in allocation order
  }

  /// <summary>
  /// Builds the object tree from the recorded full paths. Each path component
  /// before the final segment becomes a directory object (created once, shared
  /// by all files beneath it); the final segment becomes a file object. Object
  /// IDs are allocated densely from <see cref="FirstUserObjectId"/> in
  /// first-encounter order; the root keeps the reserved id 2.
  /// </summary>
  private TreeModel BuildTree() {
    var root = new TreeObject {
      ObjectId = RootObjectId, ParentObjectId = RootParentObjectId,
      IsDirectory = true, Name = "",
    };
    var objects = new List<TreeObject> { root };
    var nextId = FirstUserObjectId;

    // Maps a directory's full path ("a/b") to its object. "" == root.
    var dirByPath = new Dictionary<string, TreeObject>(StringComparer.Ordinal) { [""] = root };

    foreach (var (path, data) in _files) {
      if (path.Length == 0) continue;
      var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segments.Length == 0) continue;

      // Resolve / create every intermediate directory.
      var parent = root;
      var accumulated = "";
      for (var s = 0; s < segments.Length - 1; s++) {
        accumulated = accumulated.Length == 0 ? segments[s] : $"{accumulated}/{segments[s]}";
        if (!dirByPath.TryGetValue(accumulated, out var dir)) {
          dir = new TreeObject {
            ObjectId = nextId++, ParentObjectId = parent.ObjectId,
            IsDirectory = true, Name = segments[s],
          };
          objects.Add(dir);
          parent.Children.Add((segments[s], dir.ObjectId));
          dirByPath[accumulated] = dir;
        }
        parent = dir;
      }

      var leaf = segments[^1];
      if (leaf.Length > 200) leaf = leaf[..200];
      var file = new TreeObject {
        ObjectId = nextId++, ParentObjectId = parent.ObjectId,
        IsDirectory = false, Name = leaf, Data = data,
      };
      objects.Add(file);
      parent.Children.Add((leaf, file.ObjectId));
    }

    return new TreeModel { Root = root, Objects = objects };
  }

  /// <summary>
  /// Produces every leaf item (SD + DIRENTRY for directories, SD + DIRECT for
  /// files) for the whole tree. The caller sorts the result by the reiserfs
  /// key comparison before laying it out.
  /// </summary>
  private List<LeafItem> BuildLeafItems(TreeModel tree) {
    var items = new List<LeafItem>(tree.Objects.Count * 2);
    foreach (var obj in tree.Objects) {
      if (obj.IsDirectory) {
        // STAT_DATA — mode S_IFDIR | 0755. nlink = 2 + (number of child dirs)
        // because each subdirectory's ".." links back to this directory.
        var childDirCount = 0;
        foreach (var c in obj.Children) {
          // child object id → is it a directory? Look it up.
          if (tree.Objects.Exists(o => o.ObjectId == c.ChildObjectId && o.IsDirectory))
            childDirCount++;
        }
        var sd = new byte[SdV2Size];
        WriteStatDataV2(sd, mode: 0x41ED, nlink: (uint)(2 + childDirCount),
          size: ComputeDirItemSize(obj), uid: 0, gid: 0, blocks: 1);
        items.Add(new LeafItem {
          DirId = obj.ParentObjectId, ObjectId = obj.ObjectId,
          OffsetV2 = TypeStatDataV2 | 0u, OffsetV1 = 0, UniquenessV1 = V1SdUniqueness,
          KeyFormat = KeyFormat2, UField = 0, Body = sd, ItemType = 0,
        });

        // DIRENTRY — "." (self), ".." (parent), then each child. A directory
        // with many entries overflows one leaf, so the entries are split across
        // several DIRENTRY items (each keyed by its first entry's deh_offset).
        items.AddRange(BuildDirEntryItems(tree, obj));
      } else {
        // STAT_DATA — mode S_IFREG | 0644, single DIRECT item.
        var sd = new byte[SdV2Size];
        WriteStatDataV2(sd, mode: 0x81A4, nlink: 1, size: (ulong)obj.Data.Length,
          uid: 0, gid: 0, blocks: obj.Data.Length > 0 ? (uint)(BlockSize >> 9) : 0u);
        items.Add(new LeafItem {
          DirId = obj.ParentObjectId, ObjectId = obj.ObjectId,
          OffsetV2 = TypeStatDataV2 | 0u, OffsetV1 = 0, UniquenessV1 = V1SdUniqueness,
          KeyFormat = KeyFormat2, UField = 0, Body = sd, ItemType = 0,
        });

        // DIRECT — inline body at offset 1.
        items.Add(new LeafItem {
          DirId = obj.ParentObjectId, ObjectId = obj.ObjectId,
          OffsetV2 = TypeDirectV2 | 1u, OffsetV1 = 1, UniquenessV1 = V1DirectUniqueness,
          KeyFormat = KeyFormat2, UField = 0, Body = obj.Data, ItemType = 2,
        });
      }
    }
    return items;
  }

  /// <summary>
  /// Maximum byte size of one DIRENTRY item body. A directory with many entries
  /// is split into several items each no larger than this, so each fits inside a
  /// leaf with room for its item_head and a neighbouring stat-data item.
  /// </summary>
  private const int MaxDirItemBody = BlockSize - BlockHeadSize - ItemHeaderSize - 256;

  /// <summary>
  /// Builds one or more DIRENTRY items for a directory object. Layout per
  /// reiserfscore/node_formats.c: each item is a forward reiserfs_de_head array
  /// followed by names packed at the END of the item (entry[0] highest,
  /// entry[E-1] lowest). Entries are globally sorted by deh_offset ("." = 1,
  /// ".." = 2, children by R5 hash, with a generation counter in the low 7 bits
  /// to disambiguate hash collisions) and then chunked so that no item body
  /// exceeds <see cref="MaxDirItemBody"/>. Each item's reiserfs key offset is the
  /// deh_offset of its FIRST entry, keeping the directory's items strictly
  /// ascending; the reader merges all items of one directory by (dir_id,
  /// objectid).
  /// </summary>
  private List<LeafItem> BuildDirEntryItems(TreeModel tree, TreeObject dir) {
    // Each directory entry's (deh_dir_id, deh_objectid) is the KEY of the object
    // the entry points at — i.e. (parent's objectid, target's objectid):
    //   "."  → this directory's own key      = (dir.ParentObjectId, dir.ObjectId)
    //   ".." → the parent directory's own key = (parent.ParentObjectId, parent.ObjectId)
    //          For the root the parent is the reserved key (0, 1)
    //          (reiserfsprogs parent_root_dir_key).
    //   child → (dir.ObjectId, child.ObjectId)
    // This matches reiserfsprogs (reiserfslib.c make_sure_root_dir_exists /
    // reiserfs_add_entry) and what reiserfsck's get_next_directory_item verifies.
    uint dotDirId = dir.ParentObjectId, dotObjId = dir.ObjectId;
    uint dotDotDirId, dotDotObjId;
    if (dir.ObjectId == RootObjectId) {
      dotDotDirId = 0;
      dotDotObjId = RootParentObjectId; // (0, 1)
    } else {
      var parent = tree.Objects.Find(o => o.ObjectId == dir.ParentObjectId)!;
      dotDotDirId = parent.ParentObjectId;
      dotDotObjId = parent.ObjectId;
    }
    var entries = new List<DirEntry> {
      new(".", dotDirId, dotObjId, 1),
      new("..", dotDotDirId, dotDotObjId, 2),
    };
    foreach (var (name, childId) in dir.Children) {
      // deh points to the child SD key (dir_id = this dir's objectid, objectid = child).
      entries.Add(new DirEntry(name, dir.ObjectId, childId, HashValueR5(name)));
    }
    // Sort by deh_offset ascending ("." and ".." sort first via 1/2). Names that
    // hash to the same masked value collide; assign a generation counter in the
    // low 7 bits so every entry — and thus every item key — stays unique and
    // ascending, exactly as the kernel does via set_deh_offset / generation.
    var sorted = entries.OrderBy(e => e.DehOffset).ToList();
    for (var i = 1; i < sorted.Count; i++) {
      if (sorted[i].DehOffset <= sorted[i - 1].DehOffset)
        sorted[i] = sorted[i] with { DehOffset = sorted[i - 1].DehOffset + 1 };
    }

    var result = new List<LeafItem>();
    var start = 0;
    while (start < sorted.Count) {
      // Greedily grow this chunk while its body stays within budget.
      var bodyLen = 0;
      var end = start;
      while (end < sorted.Count) {
        var slot = DehSize + RoundUp8(Encoding.UTF8.GetByteCount(sorted[end].Name));
        if (end > start && bodyLen + slot > MaxDirItemBody) break;
        bodyLen += slot;
        end++;
      }
      result.Add(BuildDirEntryChunk(dir, sorted, start, end));
      start = end;
    }
    return result;
  }

  /// <summary>
  /// Serialises entries [start, end) of a directory into a single DIRENTRY
  /// item, packing names from the body end backward and pointing each
  /// reiserfs_de_head at its name slot.
  /// </summary>
  private static LeafItem BuildDirEntryChunk(TreeObject dir, List<DirEntry> sorted, int start, int end) {
    var entryCount = end - start;
    var slotLengths = new int[entryCount];
    var totalNamesLen = 0;
    for (var i = 0; i < entryCount; i++) {
      slotLengths[i] = RoundUp8(Encoding.UTF8.GetByteCount(sorted[start + i].Name));
      totalNamesLen += slotLengths[i];
    }
    var bodyLen = entryCount * DehSize + totalNamesLen;
    var body = new byte[bodyLen];
    var slice = new DirEntry[entryCount];

    // Pack names from END backward; entry[0] takes the highest slot.
    var nameRunningEnd = bodyLen;
    for (var i = 0; i < entryCount; i++) {
      var slot = slotLengths[i];
      var slotStart = nameRunningEnd - slot;
      Encoding.UTF8.GetBytes(sorted[start + i].Name).CopyTo(body.AsSpan(slotStart));
      slice[i] = sorted[start + i] with { Location = (ushort)slotStart };
      nameRunningEnd = slotStart;
    }
    // deh array forward at the start.
    for (var i = 0; i < entryCount; i++) {
      var off = i * DehSize;
      BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(off + 0), slice[i].DehOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(off + 4), slice[i].PointedDirId);
      BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(off + 8), slice[i].PointedObjectId);
      BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(off + 12), slice[i].Location);
      BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(off + 14), 4); // DEH_Visible2
    }

    var firstOffset = slice[0].DehOffset;
    return new LeafItem {
      DirId = dir.ParentObjectId, ObjectId = dir.ObjectId,
      OffsetV2 = TypeDirentryV2 | firstOffset, OffsetV1 = firstOffset,
      UniquenessV1 = V1DirentryUniqueness,
      KeyFormat = KeyFormat1, UField = (ushort)entryCount, Body = body, ItemType = 3,
    };
  }

  /// <summary>Directory item byte size = 16*entryCount + sum(ROUND_UP8(name)).</summary>
  private static ulong ComputeDirItemSize(TreeObject dir) {
    var size = (2 + dir.Children.Count) * DehSize;
    size += RoundUp8(1) + RoundUp8(2); // "." and ".."
    foreach (var (name, _) in dir.Children)
      size += RoundUp8(Encoding.UTF8.GetByteCount(name));
    return (ulong)size;
  }

  /// <summary>
  /// reiserfs key comparison (reiserfscore/stree.c:comp_keys): compare dir_id,
  /// then objectid, then the 64-bit offset (which embeds the type in the top
  /// 4 bits so SD &lt; INDIRECT &lt; DIRECT &lt; DIRENTRY at equal offset).
  /// </summary>
  private static int CompareKeys(LeafItem a, LeafItem b) {
    if (a.DirId != b.DirId) return a.DirId.CompareTo(b.DirId);
    if (a.ObjectId != b.ObjectId) return a.ObjectId.CompareTo(b.ObjectId);
    return a.OffsetV2.CompareTo(b.OffsetV2);
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private static int RoundUp8(int v) => (v + 7) & ~7;

  /// <summary>
  /// Writes a 44-byte stat_data_v2 (kernel struct stat_data):
  /// le16 sd_mode, le16 sd_attrs, le32 sd_nlink, le64 sd_size,
  /// le32 sd_uid, le32 sd_gid, le32 sd_atime, le32 sd_mtime, le32 sd_ctime,
  /// le32 sd_blocks, le32 sd_rdev (union with sd_generation).
  /// </summary>
  private static void WriteStatDataV2(
    Span<byte> sd, ushort mode, uint nlink, ulong size,
    uint uid, uint gid, uint blocks) {
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt16LittleEndian(sd[0..], mode);
    BinaryPrimitives.WriteUInt16LittleEndian(sd[2..], 0);    // sd_attrs
    BinaryPrimitives.WriteUInt32LittleEndian(sd[4..], nlink);
    BinaryPrimitives.WriteUInt64LittleEndian(sd[8..], size);
    BinaryPrimitives.WriteUInt32LittleEndian(sd[16..], uid);
    BinaryPrimitives.WriteUInt32LittleEndian(sd[20..], gid);
    BinaryPrimitives.WriteUInt32LittleEndian(sd[24..], now); // atime
    BinaryPrimitives.WriteUInt32LittleEndian(sd[28..], now); // mtime
    BinaryPrimitives.WriteUInt32LittleEndian(sd[32..], now); // ctime
    BinaryPrimitives.WriteUInt32LittleEndian(sd[36..], blocks);
    BinaryPrimitives.WriteUInt32LittleEndian(sd[40..], 0);   // sd_rdev
  }

  /// <summary>
  /// ReiserFS R5 directory-entry hash, masked + zero-recovered like
  /// reiserfslib.c:hash_value: <c>(r5(name) &amp; 0x7fffff80) | (==0 ? 128 : x)</c>.
  /// </summary>
  private static uint HashValueR5(string name) {
    var bytes = Encoding.UTF8.GetBytes(name);
    var r5 = R5HashRaw(bytes) & 0x7fffff80u;
    return r5 == 0 ? 128u : r5;
  }

  /// <summary>
  /// ReiserFS R5 hash. Reference: reiserfsprogs/reiserfscore/hashes.c:r5_hash.
  /// The kernel treats input bytes as <c>signed char</c>; sign-extension
  /// matters for high-bit characters.
  /// </summary>
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

  private readonly record struct DirEntry(
    string Name, uint PointedDirId, uint PointedObjectId, uint DehOffset, ushort Location = 0);

  /// <summary>
  /// A single S+tree leaf item awaiting layout: its full reiserfs key (dir_id,
  /// objectid, offset) in both v1 and v2 forms, on-disk key format, the
  /// item_head u-field (entry count for DIRENTRY, else 0) and the packed body.
  /// </summary>
  private sealed class LeafItem {
    public required uint DirId;
    public required uint ObjectId;
    public required ulong OffsetV2;     // sort key + on-disk bytes for KEY_FORMAT_2
    public required uint OffsetV1;      // on-disk offset for KEY_FORMAT_1
    public required uint UniquenessV1;  // on-disk uniqueness for KEY_FORMAT_1
    public required ushort KeyFormat;
    public required ushort UField;
    public required byte[] Body;

    // Item type (0=SD, 2=DIRECT, 3=DIRENTRY) and the owning object's id, used to
    // compute each object's sd_blocks (count of distinct leaves its BODY items —
    // DIRENTRY / DIRECT / INDIRECT — occupy) once the final packing is known.
    public int ItemType;
  }
}
