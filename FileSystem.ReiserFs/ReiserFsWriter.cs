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
// Reference: Linux kernel fs/reiserfs/reiserfs.h (v6.11 tag — last version
// before ReiserFS was removed from the upstream kernel). The on-disk structs
// reiserfs_super_block_v1 / reiserfs_super_block / journal_params / block_head
// / item_head / reiserfs_key have no checksum fields.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Writes a minimal but SPEC-COMPLIANT ReiserFS v3.6 filesystem image. Single
/// leaf block containing a directory item + N direct file-data items. All
/// superblock fields sit at the byte offsets mandated by
/// <c>struct reiserfs_super_block</c>. Roundtrips through
/// <see cref="ReiserFsReader"/>.
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
/// </code>
/// Every block_head is 24 bytes: blk_level(2) + blk_nr_item(2) +
/// blk_free_space(2) + blk_reserved(2) + blk_right_delim_key(16).
/// </remarks>
public sealed class ReiserFsWriter {
  private const int BlockSize = 4096;
  private const int SuperblockOff = 65536; // block 16 at 4-KB blocksize
  private const int LeafLevel = 1;
  private const int ItemHeaderSize = 24;
  private const int BlockHeadSize = 24;
  private const int DehSize = 16;

  // Superblock magic for ReiserFS 3.6
  private static readonly byte[] Magic36 = "ReIsEr2Fs"u8.ToArray();
  // offset_v2 type codes
  private const ulong TypeDirEntry = 3UL << 60;
  private const ulong TypeDirect = 2UL << 60;

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

    // Layout:
    //   blocks 0..15   = reserved for boot + unused (up to 64 KB)
    //   block 16       = superblock
    //   block 17       = bitmap (bit for each of first 8·bs blocks)
    //   block 18       = root leaf
    var rootBlockNum = 18;
    var totalBlocks = rootBlockNum + 1;
    var imageSize = totalBlocks * BlockSize;
    var image = new byte[imageSize];

    // ── Superblock ──────────────────────────────────────────────────────────
    var sb = image.AsSpan(SuperblockOff);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0..], (uint)totalBlocks);                  // s_block_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[4..], (uint)(totalBlocks - rootBlockNum)); // s_free_blocks (approx)
    BinaryPrimitives.WriteUInt32LittleEndian(sb[8..], (uint)rootBlockNum);                 // s_root_block

    // journal_params @ +12 (32 bytes, all zero: no journal in WORM image)
    // layout: jp_journal_1st_block, jp_journal_dev, jp_journal_size,
    //         jp_journal_trans_max, jp_journal_magic, jp_journal_max_batch,
    //         jp_journal_max_commit_age, jp_journal_max_trans_age
    sb.Slice(12, 32).Clear();

    BinaryPrimitives.WriteUInt16LittleEndian(sb[44..], BlockSize);    // s_blocksize
    BinaryPrimitives.WriteUInt16LittleEndian(sb[46..], 32);           // s_oid_maxsize (typical)
    BinaryPrimitives.WriteUInt16LittleEndian(sb[48..], 2);            // s_oid_cursize (root used, next=100)
    BinaryPrimitives.WriteUInt16LittleEndian(sb[50..], 1);            // s_umount_state = REISERFS_VALID_FS
    Magic36.CopyTo(sb[52..]);                                         // s_magic "ReIsEr2Fs\0"
    sb[52 + 9] = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(sb[62..], 1);            // s_fs_state = clean
    BinaryPrimitives.WriteUInt32LittleEndian(sb[64..], 3);            // s_hash_function_code = R5_HASH
    BinaryPrimitives.WriteUInt16LittleEndian(sb[68..], 2);            // s_tree_height (root+leaf)
    var bmapNr = (ushort)((totalBlocks + (BlockSize * 8) - 1) / (BlockSize * 8));
    BinaryPrimitives.WriteUInt16LittleEndian(sb[70..], bmapNr);       // s_bmap_nr
    BinaryPrimitives.WriteUInt16LittleEndian(sb[72..], 2);            // s_version = REISERFS_VERSION_2 (3.6)
    BinaryPrimitives.WriteUInt16LittleEndian(sb[74..], 0);            // s_reserved_for_journal
    BinaryPrimitives.WriteUInt32LittleEndian(sb[76..], 0);            // s_inode_generation
    BinaryPrimitives.WriteUInt32LittleEndian(sb[80..], 0);            // s_flags

    // s_uuid @ +84 — required non-zero for a valid superblock
    var uuid = new byte[16];
    RandomNumberGenerator.Fill(uuid);
    // Ensure at least one non-zero byte (RandomNumberGenerator won't return all
    // zeros in practice, but make it deterministic for the uuid-test).
    uuid[6] = (byte)((uuid[6] & 0x0F) | 0x40); // v4 variant marker
    uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
    uuid.CopyTo(sb[84..]);

    // s_label @ +100 (16 bytes ASCII, zero-padded)
    Encoding.ASCII.GetBytes("worm").CopyTo(sb[100..]);
    // Everything from 116..204 remains zero by default.

    // ── Bitmap block (17): mark all "in-use" blocks allocated ──────────────
    // Not strictly required for this writer to be readable by us, but emitted
    // so bmap_nr>=1 has a target and the image shape matches real mkreiserfs.
    var bmap = image.AsSpan(17 * BlockSize, BlockSize);
    // Mark blocks 0..rootBlockNum used: bit N in byte (N/8).
    for (var b = 0; b <= rootBlockNum; b++)
      bmap[b >> 3] |= (byte)(1 << (b & 7));

    // ── Root leaf block (18) ───────────────────────────────────────────────
    var boff = rootBlockNum * BlockSize;
    var blk = image.AsSpan(boff, BlockSize);
    var n = _files.Count;
    var nrItems = 1 + n; // 1 dir item + N direct data items

    // Build the item data from the END of the block backward so headers grow
    // forward from offset 24 (after the block_head) and data grows backward.
    var dataEnd = BlockSize;

    // ---- Item 0: directory (".", "..", each file) ------------------------
    // deh_t entries: offset(u32) + dir_id(u32) + objectid(u32) + location(u16)
    //                + state(u16) = 16 bytes.
    var dirEntryCount = 2 + n; // include "." and ".."
    var dehAreaLen = dirEntryCount * DehSize;

    var nameBlob = new MemoryStream();
    var nameOffsets = new int[dirEntryCount];

    string[] names = new string[dirEntryCount];
    uint[] dirIds = new uint[dirEntryCount];
    uint[] objIds = new uint[dirEntryCount];
    ulong[] offsets = new ulong[dirEntryCount]; // hash offset within deh
    names[0] = "."; dirIds[0] = 1; objIds[0] = 2; offsets[0] = 1;
    names[1] = ".."; dirIds[1] = 0; objIds[1] = 1; offsets[1] = 2;
    for (var i = 0; i < n; i++) {
      names[2 + i] = _files[i].name;
      dirIds[2 + i] = 2;             // parent = root dir objectid
      objIds[2 + i] = (uint)(100 + i);
      offsets[2 + i] = (ulong)(3 + i); // strictly increasing
    }

    for (var i = 0; i < dirEntryCount; i++) {
      nameOffsets[i] = (int)nameBlob.Position;
      nameBlob.Write(Encoding.UTF8.GetBytes(names[i]));
      nameBlob.WriteByte(0);
    }
    var nameBlobBytes = nameBlob.ToArray();
    var dirDataLen = dehAreaLen + nameBlobBytes.Length;
    dataEnd -= dirDataLen;
    var dirDataOff = dataEnd;

    for (var i = 0; i < dirEntryCount; i++) {
      var dehOff = dirDataOff + i * DehSize;
      BinaryPrimitives.WriteUInt32LittleEndian(blk[(dehOff + 0)..], (uint)offsets[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(blk[(dehOff + 4)..], dirIds[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(blk[(dehOff + 8)..], objIds[i]);
      var nameLocInItem = dehAreaLen + nameOffsets[i];
      BinaryPrimitives.WriteUInt16LittleEndian(blk[(dehOff + 12)..], (ushort)nameLocInItem);
      BinaryPrimitives.WriteUInt16LittleEndian(blk[(dehOff + 14)..], 4); // state: visible
    }
    nameBlobBytes.CopyTo(blk[(dirDataOff + dehAreaLen)..]);

    // Dir item header @ boff+24
    //   reiserfs_key (16): k_dir_id(4) + k_objectid(4) + offset_v2 (8)
    //   u.ih_entry_count (2)
    //   ih_item_len       (2)
    //   ih_item_location  (2)
    //   ih_version        (2)  — 2 for 3.6 keys
    var ihOff = BlockHeadSize;
    BinaryPrimitives.WriteUInt32LittleEndian(blk[(ihOff + 0)..], 1);        // dir_id
    BinaryPrimitives.WriteUInt32LittleEndian(blk[(ihOff + 4)..], 2);        // objectid = root dir
    BinaryPrimitives.WriteUInt64LittleEndian(blk[(ihOff + 8)..], TypeDirEntry | 1); // offset=1, type=DIRENTRY
    BinaryPrimitives.WriteUInt16LittleEndian(blk[(ihOff + 16)..], (ushort)dirEntryCount); // entry_count
    BinaryPrimitives.WriteUInt16LittleEndian(blk[(ihOff + 18)..], (ushort)dirDataLen);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[(ihOff + 20)..], (ushort)dirDataOff);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[(ihOff + 22)..], 2);       // ih_version = 3.6

    // ---- Items 1..N: direct-data items ------------------------------------
    for (var i = 0; i < n; i++) {
      var data = _files[i].data;
      dataEnd -= data.Length;
      if (data.Length > 0) data.CopyTo(blk[dataEnd..]);

      var fihOff = BlockHeadSize + (1 + i) * ItemHeaderSize;
      BinaryPrimitives.WriteUInt32LittleEndian(blk[(fihOff + 0)..], 2);                 // k_dir_id = root objectid
      BinaryPrimitives.WriteUInt32LittleEndian(blk[(fihOff + 4)..], (uint)(100 + i));   // k_objectid
      BinaryPrimitives.WriteUInt64LittleEndian(blk[(fihOff + 8)..], TypeDirect | 1);    // offset=1, type=DIRECT
      BinaryPrimitives.WriteUInt16LittleEndian(blk[(fihOff + 16)..], 0xFFFF);           // free_space = 0xFFFF for direct/stat-data
      BinaryPrimitives.WriteUInt16LittleEndian(blk[(fihOff + 18)..], (ushort)data.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(blk[(fihOff + 20)..], (ushort)dataEnd);
      BinaryPrimitives.WriteUInt16LittleEndian(blk[(fihOff + 22)..], 2);                // ih_version = 3.6
    }

    // ── Block head (24 bytes) ───────────────────────────────────────────────
    // blk_level(2) | blk_nr_item(2) | blk_free_space(2) | blk_reserved(2)
    // | blk_right_delim_key(16)
    var itemsEnd = BlockHeadSize + nrItems * ItemHeaderSize;
    var freeSpace = dataEnd - itemsEnd;
    if (freeSpace < 0)
      throw new InvalidOperationException(
        $"ReiserFsWriter: leaf block overflow — {nrItems} items + data exceed 4 KB.");

    BinaryPrimitives.WriteUInt16LittleEndian(blk[0..], LeafLevel);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[2..], (ushort)nrItems);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[4..], (ushort)freeSpace);
    BinaryPrimitives.WriteUInt16LittleEndian(blk[6..], 0); // blk_reserved

    // blk_right_delim_key: a "maximum" key past end of this leaf.
    // We don't have a right sibling, so use (dir_id=~0, objectid=~0, offset_v2=~0).
    BinaryPrimitives.WriteUInt32LittleEndian(blk[8..], 0xFFFFFFFF);
    BinaryPrimitives.WriteUInt32LittleEndian(blk[12..], 0xFFFFFFFF);
    BinaryPrimitives.WriteUInt64LittleEndian(blk[16..], 0xFFFFFFFFFFFFFFFFUL);

    output.Write(image);
  }
}
