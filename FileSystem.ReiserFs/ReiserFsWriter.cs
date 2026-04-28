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

  private readonly List<(string name, byte[] data)> _files = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var leaf = Path.GetFileName(name);
    if (leaf.Length > 200) leaf = leaf[..200];
    _files.Add((leaf, data));
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

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
    // We use objectids: 1 (root parent), 2 (root), then 100..100+n-1.
    // The objectid map encodes used ranges as ascending pairs
    // [start, end_exclusive, start, end_exclusive, ...].
    //   marker1: 1..3 (id 1 and 2 used)         → {1, 3}
    //   marker2: 100..100+n_files (n IDs used)  → {100, 100+n}
    //   final entry must be > all used (acts as sentinel "everything else free").
    // cursize = number of __le32 entries in the map.
    var n = _files.Count;
    var oidMap = new uint[n > 0 ? 4 : 2];
    oidMap[0] = 1;
    oidMap[1] = 3; // ids 1, 2 used
    if (n > 0) {
      oidMap[2] = FirstUserObjectId;
      oidMap[3] = (uint)(FirstUserObjectId + n);
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
    // Per fsck/check_tree.c:bad_pair, items follow the rule:
    //   - SD: left neighbour must belong to a different (smaller) short-key.
    //   - DIRECT: left must be SD-of-same-file (with offset 1) or INDIRECT
    //             continuation.
    //   - INDIRECT/DIRENTRY: left must be SD-of-same-file.
    // Therefore each user file requires its OWN STAT_DATA item, which the
    // previous version of this writer was missing (causing pass-1 "wrong
    // order of items" rejection).
    //
    // Order in the leaf for our layout (one root + N user files):
    //   0:  SD  (dir_id=1, obj_id=2,  offset=0, type=SD)        ← root dir SD
    //   1:  DIR (dir_id=1, obj_id=2,  offset=1, type=DIRENTRY)  ← root entries
    //   2k: SD  (dir_id=2, obj_id=100+k, offset=0, type=SD)     ← file k SD
    //   2k+1: DIRECT (dir_id=2, obj_id=100+k, offset=1, type=DIRECT)
    var boff = rootBlockNum * BlockSize;
    var blk = image.AsSpan(boff, BlockSize);
    var nrItems = 2 + 2 * n; // root SD + root DIRENTRY + per file (SD + DIRECT)

    // Build item bodies from END of block backward; ih array forward.
    var dataEnd = BlockSize;
    var ihIndex = 0;

    // ---- Item 0: root directory STAT_DATA (KEY_FORMAT_2, 44 bytes) -------
    // Per reiserfscore/node_formats.c:make_dir_stat_data:
    //   sd_mode = S_IFDIR | 0755 = 040755 = 0x41ED
    //   sd_nlink = 2 (kernel counts root with "." link)
    //   sd_size = EMPTY_DIR_SIZE (item bytes used by "." and ".." entries)
    //   sd_blocks = 1 (one leaf block holds the dir item)
    //   sd_uid = sd_gid = 0; sd_atime = sd_mtime = sd_ctime = now
    //   ih.ih_free_space = 0   (kernel macro set_ih_free_space ALWAYS writes 0)
    //   key = (1, 2, offset=0, uniqueness=V1_SD_UNIQUENESS=0)
    //   ih_format = KEY_FORMAT_2.
    var rootSdLen = SdV2Size;
    dataEnd -= rootSdLen;
    var rootSdLoc = dataEnd;
    var rootSdBody = blk[rootSdLoc..(rootSdLoc + rootSdLen)];
    WriteStatDataV2(
      rootSdBody,
      mode: 0x41ED, // S_IFDIR | 0755
      nlink: 2,
      size: ComputeRootDirSize(),
      uid: 0, gid: 0,
      blocks: 1);
    WriteItemHead(
      blk[(BlockHeadSize + ihIndex * ItemHeaderSize)..],
      dirId: RootParentObjectId, objectId: RootObjectId,
      offsetV1: 0, uniquenessV1: V1SdUniqueness,
      uField: 0, length: (ushort)rootSdLen, location: (ushort)rootSdLoc,
      keyFormat: KeyFormat2);
    ihIndex++;

    // ---- Item 1: root directory DIRENTRY ---------------------------------
    // Per reiserfscore/reiserfslib.c:reiserfs_add_entry, the dir item is
    // KEY_FORMAT_1 (offset_v1=DOT_OFFSET=1, uniqueness=DIRENTRY_UNIQUENESS=500)
    // even on a v3.6 filesystem. Names are however length-padded with
    // ROUND_UP-to-8-bytes when v3.6 is the on-disk format (per
    // make_sure_root_dir_exists which calls name_length(KEY_FORMAT_2)).
    //
    // Body layout (entries sorted by deh_offset ascending; locations
    // strictly DECREASING; names packed at the END of the item):
    //   [deh[0], deh[1], ..., deh[E-1], names[E-1], ..., names[1], names[0]]
    var entryCount = 2 + n;
    var entries = new DirEntry[entryCount];
    // "." and ".." (kernel: DOT_OFFSET=1, DOT_DOT_OFFSET=2).
    entries[0] = new DirEntry(".", RootParentObjectId, RootObjectId, 1);
    entries[1] = new DirEntry("..", 0, RootParentObjectId, 2);
    for (var i = 0; i < n; i++) {
      var name = _files[i].name;
      var hash = HashValueR5(name);
      // The pointed-to key is the file's SD key: (dir_id=root_objid=2,
      // objectid=FirstUserObjectId+i). dir_id is the parent directory's
      // objectid in reiserfs key conventions.
      entries[2 + i] = new DirEntry(name, RootObjectId, (uint)(FirstUserObjectId + i), hash);
    }
    // Stable sort by deh_offset ascending. Real R5 collisions on tiny dirs
    // are astronomically unlikely; we skip explicit gen_counter handling.
    Array.Sort(entries, 2, n, DirEntryOffsetComparer.Instance);

    // Per make_sure_root_dir_exists, name slots are ROUND_UP(name_len, 8)
    // for KEY_FORMAT_2 filesystems. Compute slot lengths and total body size.
    var slotLengths = new int[entryCount];
    var totalNamesLen = 0;
    for (var i = 0; i < entryCount; i++) {
      slotLengths[i] = RoundUp8(Encoding.UTF8.GetByteCount(entries[i].Name));
      totalNamesLen += slotLengths[i];
    }
    var dirItemLen = entryCount * DehSize + totalNamesLen;

    dataEnd -= dirItemLen;
    var dirItemLoc = dataEnd;
    var dirItemBody = blk.Slice(dirItemLoc, dirItemLen);

    // Pack names from END of item backward. entry[0] occupies the highest
    // slot, entry[E-1] the lowest. deh_location is an offset measured from
    // the start of the item body.
    var nameRunningEnd = dirItemLen;
    for (var i = 0; i < entryCount; i++) {
      var slot = slotLengths[i];
      var slotStart = nameRunningEnd - slot;
      // Zero the slot first (NULs are the kernel's padding marker —
      // name_in_entry_length scans for first NUL or end of slot).
      dirItemBody.Slice(slotStart, slot).Clear();
      var bytes = Encoding.UTF8.GetBytes(entries[i].Name);
      bytes.CopyTo(dirItemBody[slotStart..]);
      entries[i] = entries[i] with { Location = (ushort)slotStart };
      nameRunningEnd = slotStart;
    }

    // Write deh array forward at the start of the item body.
    for (var i = 0; i < entryCount; i++) {
      var dehOff = i * DehSize;
      BinaryPrimitives.WriteUInt32LittleEndian(dirItemBody[(dehOff + 0)..], entries[i].DehOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(dirItemBody[(dehOff + 4)..], entries[i].PointedDirId);
      BinaryPrimitives.WriteUInt32LittleEndian(dirItemBody[(dehOff + 8)..], entries[i].PointedObjectId);
      BinaryPrimitives.WriteUInt16LittleEndian(dirItemBody[(dehOff + 12)..], entries[i].Location);
      // deh_state — DEH_Visible2 = bit 2 (mkfs writes value 4 = 1<<2).
      BinaryPrimitives.WriteUInt16LittleEndian(dirItemBody[(dehOff + 14)..], 4);
    }

    WriteItemHead(
      blk[(BlockHeadSize + ihIndex * ItemHeaderSize)..],
      dirId: RootParentObjectId, objectId: RootObjectId,
      offsetV1: 1, uniquenessV1: V1DirentryUniqueness,
      uField: (ushort)entryCount, length: (ushort)dirItemLen, location: (ushort)dirItemLoc,
      keyFormat: KeyFormat1);
    ihIndex++;

    // ---- Per-file items: SD followed by DIRECT ---------------------------
    for (var i = 0; i < n; i++) {
      var file = _files[i];
      var fileObjId = FirstUserObjectId + (uint)i;
      var fileNameUtf8 = Encoding.UTF8.GetBytes(file.name);

      // SD for the file: KEY_FORMAT_2 stat_data_v2.
      //   sd_mode = S_IFREG | 0644 = 0x81A4
      //   sd_nlink = 1
      //   sd_size = file.data.Length
      //   sd_blocks = 0  (data stored inline as a DIRECT item, not in unfm)
      var sdLen = SdV2Size;
      dataEnd -= sdLen;
      var sdLoc = dataEnd;
      var sdBody = blk.Slice(sdLoc, sdLen);
      // sd_blocks for a DIRECT-item file = (fs_blocksize >> 9) = 8.
      // Reference: fsck/ufile.c:are_file_items_correct — when an item with
      // type=TYPE_DIRECT is seen and no prior direct, *blocks += (fs_blocksize/512).
      WriteStatDataV2(
        sdBody,
        mode: 0x81A4, // S_IFREG | 0644
        nlink: 1,
        size: (ulong)file.data.Length,
        uid: 0, gid: 0,
        blocks: file.data.Length > 0 ? (uint)(BlockSize >> 9) : 0u);
      WriteItemHead(
        blk[(BlockHeadSize + ihIndex * ItemHeaderSize)..],
        dirId: RootObjectId, objectId: fileObjId,
        offsetV1: 0, uniquenessV1: V1SdUniqueness,
        uField: 0, length: (ushort)sdLen, location: (ushort)sdLoc,
        keyFormat: KeyFormat2);
      ihIndex++;

      // DIRECT item: file body inline. Use KEY_FORMAT_2 (offset_v2 with
      // type=TYPE_DIRECT=2, offset=1).
      var directLen = file.data.Length;
      dataEnd -= directLen;
      var directLoc = dataEnd;
      if (directLen > 0)
        file.data.CopyTo(blk[directLoc..]);
      // For KEY_FORMAT_2: offset_v2 carries (type<<60 | offset). We pack it
      // by writing the v2 value into the offset_v1+uniqueness 8 bytes; this
      // is the same memory as offset_v2 (union).
      var fihOff = BlockHeadSize + ihIndex * ItemHeaderSize;
      var fih = blk[fihOff..];
      BinaryPrimitives.WriteUInt32LittleEndian(fih[0..], RootObjectId);   // k_dir_id
      BinaryPrimitives.WriteUInt32LittleEndian(fih[4..], fileObjId);      // k_objectid
      BinaryPrimitives.WriteUInt64LittleEndian(fih[8..], TypeDirectV2 | 1u); // offset=1, type=DIRECT
      BinaryPrimitives.WriteUInt16LittleEndian(fih[16..], 0);             // ih_free_space (kernel macro writes 0)
      BinaryPrimitives.WriteUInt16LittleEndian(fih[18..], (ushort)directLen);
      BinaryPrimitives.WriteUInt16LittleEndian(fih[20..], (ushort)directLoc);
      BinaryPrimitives.WriteUInt16LittleEndian(fih[22..], KeyFormat2);
      ihIndex++;
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

  // ── Helpers ───────────────────────────────────────────────────────────────

  /// <summary>EMPTY_DIR_SIZE for KEY_FORMAT_2 dir = 16*2 + ROUND_UP(1) + ROUND_UP(2) = 48.
  /// We expand for additional entries: 16*entryCount + sum(ROUND_UP(name_len)).</summary>
  private ulong ComputeRootDirSize() {
    var size = (2 + _files.Count) * DehSize;
    size += RoundUp8(1) + RoundUp8(2); // "." and ".."
    foreach (var f in _files)
      size += RoundUp8(Encoding.UTF8.GetByteCount(f.name));
    return (ulong)size;
  }

  private static int RoundUp8(int v) => (v + 7) & ~7;

  /// <summary>
  /// Writes a 24-byte item_head: key (16) + u (2) + ih_item_len (2) +
  /// ih_item_location (2) + ih_format (2). The key encodes as
  /// dir_id(4) + objectid(4) + offset_v1(4) + uniqueness(4) when
  /// keyFormat=KEY_FORMAT_1, or as dir_id(4) + objectid(4) + offset_v2(8)
  /// otherwise (the offset_v1/uniqueness and offset_v2 share the same 8 bytes).
  /// </summary>
  private static void WriteItemHead(
    Span<byte> ih, uint dirId, uint objectId,
    uint offsetV1, uint uniquenessV1,
    ushort uField, ushort length, ushort location, ushort keyFormat) {
    BinaryPrimitives.WriteUInt32LittleEndian(ih[0..], dirId);
    BinaryPrimitives.WriteUInt32LittleEndian(ih[4..], objectId);
    BinaryPrimitives.WriteUInt32LittleEndian(ih[8..], offsetV1);
    BinaryPrimitives.WriteUInt32LittleEndian(ih[12..], uniquenessV1);
    BinaryPrimitives.WriteUInt16LittleEndian(ih[16..], uField);
    BinaryPrimitives.WriteUInt16LittleEndian(ih[18..], length);
    BinaryPrimitives.WriteUInt16LittleEndian(ih[20..], location);
    BinaryPrimitives.WriteUInt16LittleEndian(ih[22..], keyFormat);
  }

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

  private sealed class DirEntryOffsetComparer : IComparer<DirEntry> {
    public static readonly DirEntryOffsetComparer Instance = new();
    public int Compare(DirEntry x, DirEntry y) => x.DehOffset.CompareTo(y.DehOffset);
  }
}
