#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ext;

/// <summary>
/// Builds minimal ext2 filesystem images from scratch. Uses 1024-byte blocks by default
/// with a single block group. Files are stored using direct block pointers.
/// <para>
/// Produces fsck-clean output: free-block/free-inode counts, used-dirs count, inode
/// link counts, inode i_blocks (sector tally), and all three inode timestamps are
/// populated so that <c>dumpe2fs</c> / <c>e2fsck</c> do not report inconsistencies.
/// </para>
/// </summary>
public sealed class ExtWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  public byte[] Build(int blockSize = 1024, int totalBlocks = 4096) {
    const ushort ExtMagic = 0xEF53;

    var firstDataBlock = blockSize == 1024 ? 1u : 0u;
    const int inodeSize = 128;
    const int inodesPerGroup = 128;
    var inodeTableBlocks = (inodesPerGroup * inodeSize + blockSize - 1) / blockSize;
    var firstFreeBlock = (int)firstDataBlock + 5 + inodeTableBlocks;
    var disk = new byte[totalBlocks * blockSize];
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // --- Block bitmap: mark all metadata blocks up through the inode-table tail as used ---
    var blockBitmapOffset = (int)(firstDataBlock + 2) * blockSize;
    for (var b = 0; b < firstFreeBlock; ++b)
      disk[blockBitmapOffset + b / 8] |= (byte)(1 << (b % 8));

    // --- Inode bitmap: inodes 1 (bad-blocks-reserved) and 2 (root) are pre-allocated ---
    var inodeBitmapOffset = (int)(firstDataBlock + 3) * blockSize;
    disk[inodeBitmapOffset] = 0x03;

    // --- Root directory + file inode allocation ---
    var inodeTableOffset = (int)(firstDataBlock + 4) * blockSize;
    var nextInode = 3u;
    var nextBlock = firstFreeBlock;

    var rootDirBlock = nextBlock++;
    MarkBlockUsed(disk, blockBitmapOffset, rootDirBlock);

    var rootDirData = new byte[blockSize];
    var dirPos = 0;
    dirPos = WriteDirEntry(rootDirData, dirPos, 2, ".", fileType: 2, blockSize, isLast: false);
    dirPos = WriteDirEntry(rootDirData, dirPos, 2, "..", fileType: 2, blockSize, isLast: _files.Count == 0);

    var fileInodes = new List<(uint Inode, string Name, byte[] Data)>();
    for (var i = 0; i < _files.Count; ++i) {
      var (name, data) = _files[i];
      var fileInode = nextInode++;
      var inodeBitIndex = (int)(fileInode - 1);
      disk[inodeBitmapOffset + inodeBitIndex / 8] |= (byte)(1 << (inodeBitIndex % 8));
      fileInodes.Add((fileInode, name, data));
      dirPos = WriteDirEntry(rootDirData, dirPos, fileInode, name, fileType: 1, blockSize, isLast: i == _files.Count - 1);
    }
    rootDirData.CopyTo(disk, rootDirBlock * blockSize);

    // --- File inodes + data blocks ---
    var sectorsPerBlock = blockSize / 512;
    foreach (var (fileInode, _, data) in fileInodes) {
      var fileInodeOffset = inodeTableOffset + (int)(fileInode - 1) * inodeSize;

      var blocksNeeded = data.Length == 0 ? 0 : (data.Length + blockSize - 1) / blockSize;
      if (blocksNeeded > 12)
        throw new InvalidOperationException(
          $"ext2 writer only supports direct blocks (max {12 * blockSize} bytes per file).");

      var fileBlocks = new List<int>(blocksNeeded);
      for (var b = 0; b < blocksNeeded; ++b) {
        fileBlocks.Add(nextBlock);
        MarkBlockUsed(disk, blockBitmapOffset, nextBlock);
        ++nextBlock;
      }

      var written = 0;
      foreach (var fb in fileBlocks) {
        var toWrite = Math.Min(blockSize, data.Length - written);
        if (toWrite > 0) Array.Copy(data, written, disk, fb * blockSize, toWrite);
        written += toWrite;
      }

      var ino = disk.AsSpan(fileInodeOffset, inodeSize);
      BinaryPrimitives.WriteUInt16LittleEndian(ino, 0x8000 | 0x01A4);           // i_mode: regular file, 0644
      BinaryPrimitives.WriteUInt32LittleEndian(ino[4..], (uint)data.Length);    // i_size
      BinaryPrimitives.WriteUInt32LittleEndian(ino[8..], now);                  // i_atime
      BinaryPrimitives.WriteUInt32LittleEndian(ino[12..], now);                 // i_ctime
      BinaryPrimitives.WriteUInt32LittleEndian(ino[16..], now);                 // i_mtime
      BinaryPrimitives.WriteUInt16LittleEndian(ino[26..], 1);                   // i_links_count
      BinaryPrimitives.WriteUInt32LittleEndian(ino[28..], (uint)(fileBlocks.Count * sectorsPerBlock)); // i_blocks (512-byte sectors)
      for (var b = 0; b < fileBlocks.Count; ++b)
        BinaryPrimitives.WriteUInt32LittleEndian(ino[(40 + b * 4)..], (uint)fileBlocks[b]);
    }

    // --- Root inode (inode 2) ---
    var rootInodeOffset = inodeTableOffset + 1 * inodeSize;
    var rootIno = disk.AsSpan(rootInodeOffset, inodeSize);
    BinaryPrimitives.WriteUInt16LittleEndian(rootIno, 0x4000 | 0x01ED);          // i_mode: directory, 0755
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[4..], (uint)blockSize);     // i_size
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[8..], now);                 // i_atime
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[12..], now);                // i_ctime
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[16..], now);                // i_mtime
    BinaryPrimitives.WriteUInt16LittleEndian(rootIno[26..], 2);                  // i_links_count: "." + parent-of-mountpoint
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[28..], (uint)sectorsPerBlock); // i_blocks
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[40..], (uint)rootDirBlock); // direct block 0

    // --- Free-count accounting (what fsck scrutinises) ---
    // Total inodes = inodesPerGroup; used = 2 reserved + files added.
    var usedInodes = 2 + (uint)fileInodes.Count;
    var freeInodes = (uint)inodesPerGroup - usedInodes;
    // Used blocks = firstFreeBlock (metadata) + 1 (root dir) + sum of file blocks.
    var usedBlocks = (uint)nextBlock;
    var freeBlocks = (uint)totalBlocks - usedBlocks;
    var usedDirs = 1u; // root only

    // --- Superblock at offset 1024 ---
    var sb = disk.AsSpan(1024);
    BinaryPrimitives.WriteUInt32LittleEndian(sb, (uint)inodesPerGroup);            // s_inodes_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[4..], (uint)totalBlocks);          // s_blocks_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[8..], 0);                          // s_r_blocks_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[12..], freeBlocks);                // s_free_blocks_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[16..], freeInodes);                // s_free_inodes_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[20..], firstDataBlock);            // s_first_data_block
    var logBlockSize = blockSize == 1024 ? 0u : blockSize == 2048 ? 1u : 2u;
    BinaryPrimitives.WriteUInt32LittleEndian(sb[24..], logBlockSize);              // s_log_block_size
    BinaryPrimitives.WriteUInt32LittleEndian(sb[28..], logBlockSize);              // s_log_frag_size (same)
    BinaryPrimitives.WriteUInt32LittleEndian(sb[32..], (uint)totalBlocks);         // s_blocks_per_group
    BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], (uint)totalBlocks);         // s_frags_per_group
    BinaryPrimitives.WriteUInt32LittleEndian(sb[40..], (uint)inodesPerGroup);      // s_inodes_per_group
    BinaryPrimitives.WriteUInt32LittleEndian(sb[44..], now);                       // s_mtime
    BinaryPrimitives.WriteUInt32LittleEndian(sb[48..], now);                       // s_wtime
    BinaryPrimitives.WriteUInt16LittleEndian(sb[52..], 0);                         // s_mnt_count
    BinaryPrimitives.WriteUInt16LittleEndian(sb[54..], 20);                        // s_max_mnt_count
    BinaryPrimitives.WriteUInt16LittleEndian(sb[56..], ExtMagic);                  // s_magic
    BinaryPrimitives.WriteUInt16LittleEndian(sb[58..], 1);                         // s_state = CLEAN
    BinaryPrimitives.WriteUInt16LittleEndian(sb[60..], 1);                         // s_errors = CONTINUE
    BinaryPrimitives.WriteUInt16LittleEndian(sb[62..], 0);                         // s_minor_rev_level
    BinaryPrimitives.WriteUInt32LittleEndian(sb[64..], now);                       // s_lastcheck
    BinaryPrimitives.WriteUInt32LittleEndian(sb[68..], 0);                         // s_checkinterval
    BinaryPrimitives.WriteUInt32LittleEndian(sb[72..], 0);                         // s_creator_os = Linux
    BinaryPrimitives.WriteUInt32LittleEndian(sb[76..], 0);                         // s_rev_level = GOOD_OLD_REV
    BinaryPrimitives.WriteUInt16LittleEndian(sb[80..], 0);                         // s_def_resuid
    BinaryPrimitives.WriteUInt16LittleEndian(sb[82..], 0);                         // s_def_resgid
    // Inode size appears at 88 only when rev_level >= 1, but writing it is harmless.
    BinaryPrimitives.WriteUInt16LittleEndian(sb[88..], inodeSize);                 // s_inode_size

    // UUID at offset 104 (16 bytes) — blkid/dumpe2fs rely on this to identify
    // the filesystem. The kernel accepts any non-zero UUID at rev 0 (it becomes
    // mandatory at rev 1, which is harmless to set unconditionally).
    var uuid = Guid.NewGuid().ToByteArray();
    uuid.CopyTo(sb.Slice(104, 16));
    // Volume label at offset 120 (16 bytes) — optional, left empty. Last-mount
    // path at offset 136 (64 bytes) — also optional.

    // --- Block Group Descriptor at block (firstDataBlock+1) — 32 bytes, reserved area zeroed ---
    var bgdOffset = (int)(firstDataBlock + 1) * blockSize;
    var bgd = disk.AsSpan(bgdOffset, 32);
    BinaryPrimitives.WriteUInt32LittleEndian(bgd, (uint)(firstDataBlock + 2));     // bg_block_bitmap
    BinaryPrimitives.WriteUInt32LittleEndian(bgd[4..], (uint)(firstDataBlock + 3)); // bg_inode_bitmap
    BinaryPrimitives.WriteUInt32LittleEndian(bgd[8..], (uint)(firstDataBlock + 4)); // bg_inode_table
    BinaryPrimitives.WriteUInt16LittleEndian(bgd[12..], (ushort)freeBlocks);       // bg_free_blocks_count
    BinaryPrimitives.WriteUInt16LittleEndian(bgd[14..], (ushort)freeInodes);       // bg_free_inodes_count
    BinaryPrimitives.WriteUInt16LittleEndian(bgd[16..], (ushort)usedDirs);         // bg_used_dirs_count

    return disk;
  }

  private static int WriteDirEntry(byte[] dirData, int pos, uint inode, string name, byte fileType, int blockSize, bool isLast) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var entrySize = (8 + nameBytes.Length + 3) & ~3;
    var recLen = isLast ? blockSize - pos : entrySize;

    BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(pos), inode);
    BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(pos + 4), (ushort)recLen);
    dirData[pos + 6] = (byte)nameBytes.Length;
    dirData[pos + 7] = fileType;
    nameBytes.CopyTo(dirData, pos + 8);
    return pos + entrySize;
  }

  private static void MarkBlockUsed(byte[] disk, int bitmapOffset, int blockNum)
    => disk[bitmapOffset + blockNum / 8] |= (byte)(1 << (blockNum % 8));
}
