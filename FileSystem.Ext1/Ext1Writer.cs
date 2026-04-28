#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ext1;

/// <summary>
/// Builds minimal ext1 (1992) filesystem images from scratch — the predecessor of
/// ext2 by Rémy Card. The on-disk superblock layout is identical to GOOD_OLD-revision
/// ext2 byte-for-byte except for the magic value (<c>0xEF51</c> instead of ext2's
/// <c>0xEF53</c>) at offset 1080 of the file.
/// <para>
/// Differences from the ext2 writer:
/// </para>
/// <list type="bullet">
///   <item><description>Magic: <c>0xEF51</c>.</description></item>
///   <item><description><c>s_rev_level</c> = 0 (GOOD_OLD_REV) — no dynamic-rev fields
///   (<c>s_first_ino</c>, <c>s_inode_size</c>, feature flags) are honoured.</description></item>
///   <item><description>Inodes are a fixed 128 bytes (no <c>s_inode_size</c> field).</description></item>
///   <item><description>No journal, no extents, no FILETYPE feature.</description></item>
///   <item><description>Directory entries use the rev-0 layout: <c>inode(4) + rec_len(2) +
///   name_len(2) + name[]</c> — the 16-bit <c>name_len</c> is NOT split into
///   <c>name_len(8) + file_type(8)</c> as in rev-1 with FILETYPE.</description></item>
/// </list>
/// <para>
/// No <c>mkfs.ext1</c> exists — ext1's magic was retired in 1993, so no Linux validator
/// can mount or fsck the resulting images. Tests verify our reader can round-trip the
/// output.
/// </para>
/// </summary>
public sealed class Ext1Writer {
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to be packed into the next <see cref="Build"/> call.</summary>
  public void AddFile(string name, byte[] data) => this._files.Add((name, data));

  /// <summary>
  /// Materialises a complete ext1 image as a byte array. Default geometry produces a
  /// 4 MiB image with 1024-byte blocks (<c>s_log_block_size</c> = 0) and a single block
  /// group — the canonical layout for early-1990s small partitions.
  /// </summary>
  public byte[] Build(int blockSize = 1024, int totalBlocks = 4096) {
    const ushort Ext1Magic = 0xEF51;
    // Inodes 1..10 are reserved by ext convention (bad-blocks, root, ACL slots,
    // boot-loader, undeleted-dir, etc.). User files start at inode 11.
    const uint FirstUserInode = 11;
    // GOOD_OLD_REV — original ext1/early-ext2 revision. No s_first_ino,
    // s_inode_size, or feature flags are stored or interpreted.
    const uint RevLevelGoodOld = 0;

    var firstDataBlock = blockSize == 1024 ? 1u : 0u;
    const int inodeSize = 128;
    const int inodesPerGroup = 128;
    var inodeTableBlocks = (inodesPerGroup * inodeSize + blockSize - 1) / blockSize;
    var firstFreeBlock = (int)firstDataBlock + 4 + inodeTableBlocks;
    var disk = new byte[totalBlocks * blockSize];
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // --- Block bitmap: bit N tracks block (firstDataBlock + N). ---
    var blockBitmapOffset = (int)(firstDataBlock + 2) * blockSize;
    for (var b = (int)firstDataBlock; b < firstFreeBlock; ++b) {
      var bitIdx = b - (int)firstDataBlock;
      disk[blockBitmapOffset + bitIdx / 8] |= (byte)(1 << (bitIdx % 8));
    }

    // --- Inode bitmap: mark inodes 1..(FirstUserInode-1) as in-use. ---
    var inodeBitmapOffset = (int)(firstDataBlock + 3) * blockSize;
    for (var ino = 1u; ino < FirstUserInode; ++ino) {
      var idx = (int)(ino - 1);
      disk[inodeBitmapOffset + idx / 8] |= (byte)(1 << (idx % 8));
    }

    var inodeTableOffset = (int)(firstDataBlock + 4) * blockSize;
    var nextInode = FirstUserInode;
    var nextBlock = firstFreeBlock;

    var rootDirBlock = nextBlock++;
    MarkBlockUsed(disk, blockBitmapOffset, rootDirBlock, (int)firstDataBlock);

    var rootDirData = new byte[blockSize];
    var dirPos = 0;
    dirPos = WriteRev0DirEntry(rootDirData, dirPos, 2, ".", blockSize, isLast: false);
    dirPos = WriteRev0DirEntry(rootDirData, dirPos, 2, "..", blockSize, isLast: this._files.Count == 0);

    var fileInodes = new List<(uint Inode, byte[] Data)>(this._files.Count);
    for (var i = 0; i < this._files.Count; ++i) {
      var (name, data) = this._files[i];
      var fileInode = nextInode++;
      var inodeBitIndex = (int)(fileInode - 1);
      disk[inodeBitmapOffset + inodeBitIndex / 8] |= (byte)(1 << (inodeBitIndex % 8));
      fileInodes.Add((fileInode, data));
      dirPos = WriteRev0DirEntry(rootDirData, dirPos, fileInode, name, blockSize, isLast: i == this._files.Count - 1);
    }
    rootDirData.CopyTo(disk, rootDirBlock * blockSize);

    // --- File inodes + data blocks ---
    var sectorsPerBlock = blockSize / 512;
    foreach (var (fileInode, data) in fileInodes) {
      var fileInodeOffset = inodeTableOffset + (int)(fileInode - 1) * inodeSize;

      var blocksNeeded = data.Length == 0 ? 0 : (data.Length + blockSize - 1) / blockSize;
      if (blocksNeeded > 12)
        throw new InvalidOperationException(
          $"ext1 writer only supports direct blocks (max {12 * blockSize} bytes per file).");

      var fileBlocks = new List<int>(blocksNeeded);
      for (var b = 0; b < blocksNeeded; ++b) {
        fileBlocks.Add(nextBlock);
        MarkBlockUsed(disk, blockBitmapOffset, nextBlock, (int)firstDataBlock);
        ++nextBlock;
      }

      var written = 0;
      foreach (var fb in fileBlocks) {
        var toWrite = Math.Min(blockSize, data.Length - written);
        if (toWrite > 0) Array.Copy(data, written, disk, fb * blockSize, toWrite);
        written += toWrite;
      }

      var ino = disk.AsSpan(fileInodeOffset, inodeSize);
      BinaryPrimitives.WriteUInt16LittleEndian(ino, 0x8000 | 0x01A4);            // i_mode: regular, 0644
      BinaryPrimitives.WriteUInt32LittleEndian(ino[4..], (uint)data.Length);     // i_size
      BinaryPrimitives.WriteUInt32LittleEndian(ino[8..], now);                   // i_atime
      BinaryPrimitives.WriteUInt32LittleEndian(ino[12..], now);                  // i_ctime
      BinaryPrimitives.WriteUInt32LittleEndian(ino[16..], now);                  // i_mtime
      BinaryPrimitives.WriteUInt16LittleEndian(ino[26..], 1);                    // i_links_count
      BinaryPrimitives.WriteUInt32LittleEndian(ino[28..], (uint)(fileBlocks.Count * sectorsPerBlock));
      for (var b = 0; b < fileBlocks.Count; ++b)
        BinaryPrimitives.WriteUInt32LittleEndian(ino[(40 + b * 4)..], (uint)fileBlocks[b]);
    }

    // --- Root inode (inode 2) ---
    var rootInodeOffset = inodeTableOffset + 1 * inodeSize;
    var rootIno = disk.AsSpan(rootInodeOffset, inodeSize);
    BinaryPrimitives.WriteUInt16LittleEndian(rootIno, 0x4000 | 0x01ED);          // i_mode: dir, 0755
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[4..], (uint)blockSize);     // i_size
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[8..], now);
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[12..], now);
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[16..], now);
    BinaryPrimitives.WriteUInt16LittleEndian(rootIno[26..], 2);                  // i_links_count: "." + parent
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[28..], (uint)sectorsPerBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[40..], (uint)rootDirBlock);

    // --- Free-count accounting ---
    var usedInodes = (FirstUserInode - 1) + (uint)fileInodes.Count;
    var freeInodes = (uint)inodesPerGroup - usedInodes;
    var usedBlocks = (uint)nextBlock;
    var freeBlocks = (uint)totalBlocks - usedBlocks;
    var usedDirs = 1u;

    // --- Superblock at offset 1024 (rev-0 layout) ---
    var sb = disk.AsSpan(1024);
    BinaryPrimitives.WriteUInt32LittleEndian(sb, (uint)inodesPerGroup);            // s_inodes_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[4..], (uint)totalBlocks);          // s_blocks_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[8..], 0);                          // s_r_blocks_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[12..], freeBlocks);                // s_free_blocks_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[16..], freeInodes);                // s_free_inodes_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[20..], firstDataBlock);            // s_first_data_block
    var logBlockSize = blockSize == 1024 ? 0u : blockSize == 2048 ? 1u : 2u;
    BinaryPrimitives.WriteUInt32LittleEndian(sb[24..], logBlockSize);              // s_log_block_size
    BinaryPrimitives.WriteUInt32LittleEndian(sb[28..], logBlockSize);              // s_log_frag_size
    BinaryPrimitives.WriteUInt32LittleEndian(sb[32..], (uint)totalBlocks);         // s_blocks_per_group
    BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], (uint)totalBlocks);         // s_frags_per_group
    BinaryPrimitives.WriteUInt32LittleEndian(sb[40..], (uint)inodesPerGroup);      // s_inodes_per_group
    BinaryPrimitives.WriteUInt32LittleEndian(sb[44..], now);                       // s_mtime
    BinaryPrimitives.WriteUInt32LittleEndian(sb[48..], now);                       // s_wtime
    BinaryPrimitives.WriteUInt16LittleEndian(sb[52..], 0);                         // s_mnt_count
    BinaryPrimitives.WriteUInt16LittleEndian(sb[54..], 20);                        // s_max_mnt_count
    BinaryPrimitives.WriteUInt16LittleEndian(sb[56..], Ext1Magic);                 // s_magic = 0xEF51 ★
    BinaryPrimitives.WriteUInt16LittleEndian(sb[58..], 1);                         // s_state = CLEAN
    BinaryPrimitives.WriteUInt16LittleEndian(sb[60..], 1);                         // s_errors = CONTINUE
    BinaryPrimitives.WriteUInt16LittleEndian(sb[62..], 0);                         // s_minor_rev_level
    BinaryPrimitives.WriteUInt32LittleEndian(sb[64..], now);                       // s_lastcheck
    BinaryPrimitives.WriteUInt32LittleEndian(sb[68..], 0);                         // s_checkinterval
    BinaryPrimitives.WriteUInt32LittleEndian(sb[72..], 0);                         // s_creator_os = Linux
    BinaryPrimitives.WriteUInt32LittleEndian(sb[76..], RevLevelGoodOld);           // s_rev_level = 0 ★
    BinaryPrimitives.WriteUInt16LittleEndian(sb[80..], 0);                         // s_def_resuid
    BinaryPrimitives.WriteUInt16LittleEndian(sb[82..], 0);                         // s_def_resgid
    // NOTE: Rev-0 explicitly does NOT populate s_first_ino, s_inode_size, or
    // feature-flag fields at offsets 84/88/92/96. The kernel and tooling assume
    // FirstUserInode=11 and inode_size=128 for GOOD_OLD_REV images.

    // --- Bitmap-tail padding (mkfs convention: pad bits = 1) ---
    var blockBitmapBits = totalBlocks - (int)firstDataBlock;
    var blockBitmapBytes = blockSize;
    for (var bit = blockBitmapBits; bit < blockBitmapBytes * 8; ++bit)
      disk[blockBitmapOffset + bit / 8] |= (byte)(1 << (bit % 8));

    var inodeBitmapBits = inodesPerGroup;
    var inodeBitmapBytes = blockSize;
    for (var bit = inodeBitmapBits; bit < inodeBitmapBytes * 8; ++bit)
      disk[inodeBitmapOffset + bit / 8] |= (byte)(1 << (bit % 8));

    // --- Block Group Descriptor at block (firstDataBlock+1) — 32 bytes ---
    var bgdOffset = (int)(firstDataBlock + 1) * blockSize;
    var bgd = disk.AsSpan(bgdOffset, 32);
    BinaryPrimitives.WriteUInt32LittleEndian(bgd, (uint)(firstDataBlock + 2));     // bg_block_bitmap
    BinaryPrimitives.WriteUInt32LittleEndian(bgd[4..], (uint)(firstDataBlock + 3)); // bg_inode_bitmap
    BinaryPrimitives.WriteUInt32LittleEndian(bgd[8..], (uint)(firstDataBlock + 4)); // bg_inode_table
    BinaryPrimitives.WriteUInt16LittleEndian(bgd[12..], (ushort)freeBlocks);
    BinaryPrimitives.WriteUInt16LittleEndian(bgd[14..], (ushort)freeInodes);
    BinaryPrimitives.WriteUInt16LittleEndian(bgd[16..], (ushort)usedDirs);

    return disk;
  }

  /// <summary>Materialises the image and writes it to the given stream.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var data = this.Build();
    output.Write(data, 0, data.Length);
  }

  /// <summary>
  /// Writes a rev-0 (GOOD_OLD_REV) directory entry at <paramref name="pos"/>. Format:
  /// <c>inode(4) | rec_len(2) | name_len(2) | name[] | pad to 4-byte boundary</c>.
  /// The 16-bit <c>name_len</c> is NOT split into an 8-bit name_len + 8-bit file_type
  /// — that split only exists in rev-1 with FEATURE_INCOMPAT_FILETYPE.
  /// </summary>
  private static int WriteRev0DirEntry(byte[] dirData, int pos, uint inode, string name, int blockSize, bool isLast) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var entrySize = (8 + nameBytes.Length + 3) & ~3;
    var recLen = isLast ? blockSize - pos : entrySize;

    BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(pos), inode);
    BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(pos + 4), (ushort)recLen);
    BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(pos + 6), (ushort)nameBytes.Length);
    nameBytes.CopyTo(dirData, pos + 8);
    return pos + entrySize;
  }

  private static void MarkBlockUsed(byte[] disk, int bitmapOffset, int blockNum, int firstDataBlock) {
    var bit = blockNum - firstDataBlock;
    disk[bitmapOffset + bit / 8] |= (byte)(1 << (bit % 8));
  }
}
