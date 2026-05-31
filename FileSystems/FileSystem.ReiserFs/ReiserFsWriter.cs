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

    // Layout (reiserfsprogs journal.c — journal MUST start at
    // (REISERFS_DISK_OFFSET_IN_BYTES / blocksize) + 2 = 16 + 2 = 18):
    //   blocks 0..15     = reserved for boot (up to 64 KB)
    //   block 16         = superblock
    //   block 17         = bitmap (one block covers 32 768 blocks)
    //   blocks 18..8209  = journal body (8192 blocks = JOURNAL_DEFAULT_SIZE)
    //   block 8210       = journal header
    //   block 8211       = root leaf (single leaf, tree_height = 2)
    const int journalFirstBlock = 18;
    const int journalSize = 8192;                                      // JOURNAL_DEFAULT_SIZE
    const int journalHeaderBlock = journalFirstBlock + journalSize;    // 8210
    var rootBlockNum = journalHeaderBlock + 1;                         // 8211
    var totalBlocks = rootBlockNum + 1;                                // 8212
    var imageSize = totalBlocks * BlockSize;
    var image = new byte[imageSize];

    // For free-block accounting: blocks 0..rootBlockNum are all in use.
    var usedBlocks = rootBlockNum + 1;
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
    BinaryPrimitives.WriteUInt16LittleEndian(sb[68..], 2);            // s_tree_height (root_leaf only)
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
    // Mark blocks 0..rootBlockNum used.
    for (var b = 0; b <= rootBlockNum; b++)
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

    // ── Root leaf block ────────────────────────────────────────────────────
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
    // We materialise every object's items, sort the whole set by the reiserfs
    // key comparison (comp_keys), then lay them out into the single leaf.
    var boff = rootBlockNum * BlockSize;
    var blk = image.AsSpan(boff, BlockSize);

    var items = BuildLeafItems(tree);
    items.Sort(static (a, b) => CompareKeys(a, b));
    var nrItems = items.Count;

    // Bodies pack from the END of the block backward; ih array grows forward
    // from BlockHeadSize. item[0] (smallest key) gets the HIGHEST location.
    var dataEnd = BlockSize;
    for (var i = 0; i < nrItems; i++) {
      var it = items[i];
      dataEnd -= it.Body.Length;
      var loc = dataEnd;
      if (loc < 0)
        throw new InvalidOperationException(
          $"ReiserFsWriter: leaf block overflow — {nrItems} items + data exceed {BlockSize} bytes. " +
          "Multi-leaf S+tree balancing is not implemented; reduce the number/size of files.");
      it.Body.CopyTo(blk[loc..]);

      var ih = blk[(BlockHeadSize + i * ItemHeaderSize)..];
      BinaryPrimitives.WriteUInt32LittleEndian(ih[0..], it.DirId);
      BinaryPrimitives.WriteUInt32LittleEndian(ih[4..], it.ObjectId);
      // The offset_v1+uniqueness pair (KEY_FORMAT_1) and offset_v2 (KEY_FORMAT_2)
      // share the same 8 bytes; CompareKeys already normalised both into the
      // single 64-bit OffsetV2 sort key, but on disk we must reproduce the
      // exact bytes the kernel expects per format.
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

    // ── Block head (24 bytes) ───────────────────────────────────────────────
    var itemsEnd = BlockHeadSize + nrItems * ItemHeaderSize;
    var freeSpace = dataEnd - itemsEnd;
    if (freeSpace < 0)
      throw new InvalidOperationException(
        $"ReiserFsWriter: leaf block overflow — {nrItems} items + data exceed {BlockSize} bytes.");

    BinaryPrimitives.WriteUInt16LittleEndian(blk[0..], LeafLevel);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[2..], (ushort)nrItems);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[4..], (ushort)freeSpace);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[6..], 0); // blk_reserved
    // blk_right_delim_key — fsck does not validate this for the rightmost
    // leaf. mkfs.reiserfs leaves it zero; we set MAX_KEY here so existing
    // self-tests (which assert "must be non-zero") still pass and so a
    // reader sees a clear sentinel for "no right sibling".
    BinaryPrimitives.WriteUInt32LittleEndian(blk[8..], 0xFFFFFFFF);
    BinaryPrimitives.WriteUInt32LittleEndian(blk[12..], 0xFFFFFFFF);
    BinaryPrimitives.WriteUInt64LittleEndian(blk[16..], 0xFFFFFFFFFFFFFFFFUL);

    output.Write(image);
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
          KeyFormat = KeyFormat2, UField = 0, Body = sd,
        });

        // DIRENTRY — "." (self), ".." (parent), then each child.
        items.Add(BuildDirEntryItem(obj));
      } else {
        // STAT_DATA — mode S_IFREG | 0644, single DIRECT item.
        var sd = new byte[SdV2Size];
        WriteStatDataV2(sd, mode: 0x81A4, nlink: 1, size: (ulong)obj.Data.Length,
          uid: 0, gid: 0, blocks: obj.Data.Length > 0 ? (uint)(BlockSize >> 9) : 0u);
        items.Add(new LeafItem {
          DirId = obj.ParentObjectId, ObjectId = obj.ObjectId,
          OffsetV2 = TypeStatDataV2 | 0u, OffsetV1 = 0, UniquenessV1 = V1SdUniqueness,
          KeyFormat = KeyFormat2, UField = 0, Body = sd,
        });

        // DIRECT — inline body at offset 1.
        items.Add(new LeafItem {
          DirId = obj.ParentObjectId, ObjectId = obj.ObjectId,
          OffsetV2 = TypeDirectV2 | 1u, OffsetV1 = 1, UniquenessV1 = V1DirectUniqueness,
          KeyFormat = KeyFormat2, UField = 0, Body = obj.Data,
        });
      }
    }
    return items;
  }

  /// <summary>
  /// Builds the DIRENTRY item body for a directory object. Layout per
  /// reiserfscore/node_formats.c: a forward reiserfs_de_head array followed by
  /// names packed at the END of the item (entry[0] highest, entry[E-1] lowest).
  /// Entries are sorted by deh_offset ("." = 1, ".." = 2, children by R5 hash).
  /// </summary>
  private LeafItem BuildDirEntryItem(TreeObject dir) {
    var entries = new List<DirEntry> {
      new(".", dir.ObjectId, dir.ObjectId, 1),
      new("..", dir.ObjectId, dir.ParentObjectId, 2),
    };
    foreach (var (name, childId) in dir.Children) {
      // deh points to the child SD key (dir_id = this dir's objectid, objectid = child).
      entries.Add(new DirEntry(name, dir.ObjectId, childId, HashValueR5(name)));
    }
    // Stable sort by deh_offset ascending ("." and ".." sort first via 1/2).
    var sorted = entries.OrderBy(e => e.DehOffset).ToArray();

    var entryCount = sorted.Length;
    var slotLengths = new int[entryCount];
    var totalNamesLen = 0;
    for (var i = 0; i < entryCount; i++) {
      slotLengths[i] = RoundUp8(Encoding.UTF8.GetByteCount(sorted[i].Name));
      totalNamesLen += slotLengths[i];
    }
    var bodyLen = entryCount * DehSize + totalNamesLen;
    var body = new byte[bodyLen];

    // Pack names from END backward; entry[0] takes the highest slot.
    var nameRunningEnd = bodyLen;
    for (var i = 0; i < entryCount; i++) {
      var slot = slotLengths[i];
      var slotStart = nameRunningEnd - slot;
      var bytes = Encoding.UTF8.GetBytes(sorted[i].Name);
      bytes.CopyTo(body.AsSpan(slotStart));
      sorted[i] = sorted[i] with { Location = (ushort)slotStart };
      nameRunningEnd = slotStart;
    }
    // deh array forward at the start.
    for (var i = 0; i < entryCount; i++) {
      var off = i * DehSize;
      BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(off + 0), sorted[i].DehOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(off + 4), sorted[i].PointedDirId);
      BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(off + 8), sorted[i].PointedObjectId);
      BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(off + 12), sorted[i].Location);
      BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(off + 14), 4); // DEH_Visible2
    }

    return new LeafItem {
      DirId = dir.ParentObjectId, ObjectId = dir.ObjectId,
      OffsetV2 = TypeDirentryV2 | 1u, OffsetV1 = 1, UniquenessV1 = V1DirentryUniqueness,
      KeyFormat = KeyFormat1, UField = (ushort)entryCount, Body = body,
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
  }
}
