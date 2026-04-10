#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Ext;

/// <summary>
/// Builds minimal ext2 filesystem images from scratch. Uses 1024-byte blocks by default
/// with a single block group. Files are stored using direct block pointers.
/// </summary>
public sealed class ExtWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the image.</summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>
  /// Builds the ext2 filesystem image.
  /// </summary>
  /// <param name="blockSize">Block size in bytes (1024 or 4096). Default 1024.</param>
  /// <param name="totalBlocks">Total blocks in the image. Default 4096 (4MB with 1K blocks).</param>
  /// <returns>Complete disk image as byte array.</returns>
  public byte[] Build(int blockSize = 1024, int totalBlocks = 4096) {
    const ushort extMagic = 0xEF53;

    // First data block: 1 for 1K blocks, 0 for larger
    var firstDataBlock = blockSize == 1024 ? 1u : 0u;

    // Layout:
    // Block 0 (1K blocks) or first 1024 bytes of block 0 (4K blocks): boot block (unused)
    // Block firstDataBlock: superblock (at byte offset 1024 always)
    // Block firstDataBlock+1: block group descriptor table
    // Block firstDataBlock+2: block bitmap
    // Block firstDataBlock+3: inode bitmap
    // Block firstDataBlock+4 .. firstDataBlock+4+inodeTableBlocks-1: inode table
    // Remaining: data blocks

    var inodeSize = 128;
    var inodesPerGroup = 128; // reasonable for small image
    var inodeTableBlocks = (inodesPerGroup * inodeSize + blockSize - 1) / blockSize;

    var firstFreeBlock = (int)firstDataBlock + 5 + inodeTableBlocks;
    var totalSize = totalBlocks * blockSize;
    var disk = new byte[totalSize];

    // --- Superblock at offset 1024 ---
    var sb = disk.AsSpan(1024);
    BinaryPrimitives.WriteUInt32LittleEndian(sb, (uint)inodesPerGroup);          // s_inodes_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(4), (uint)totalBlocks);     // s_blocks_count
    // s_log_block_size at offset 24
    var logBlockSize = blockSize == 1024 ? 0u : blockSize == 2048 ? 1u : 2u;
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(24), logBlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(20), firstDataBlock);       // s_first_data_block
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(32), (uint)totalBlocks);    // s_blocks_per_group
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(40), (uint)inodesPerGroup); // s_inodes_per_group
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(56), extMagic);             // s_magic
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(88), (ushort)inodeSize);    // s_inode_size
    // s_state = 1 (clean)
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(58), 1);

    // --- Block Group Descriptor at block (firstDataBlock+1) ---
    var bgdOffset = (int)(firstDataBlock + 1) * blockSize;
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(bgdOffset), (uint)(firstDataBlock + 2));     // block bitmap
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(bgdOffset + 4), (uint)(firstDataBlock + 3)); // inode bitmap
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(bgdOffset + 8), (uint)(firstDataBlock + 4)); // inode table

    // --- Inode bitmap: mark inodes 1 and 2 as used (root inode = 2) ---
    var inodeBitmapOffset = (int)(firstDataBlock + 3) * blockSize;
    disk[inodeBitmapOffset] = 0x03; // bits 0,1 = inodes 1,2

    // --- Block bitmap: mark metadata blocks as used ---
    var blockBitmapOffset = (int)(firstDataBlock + 2) * blockSize;
    // Mark blocks 0..firstFreeBlock-1 as used
    for (var b = 0; b < firstFreeBlock; b++) {
      disk[blockBitmapOffset + b / 8] |= (byte)(1 << (b % 8));
    }

    // --- Inode table starts at block (firstDataBlock+4) ---
    var inodeTableOffset = (int)(firstDataBlock + 4) * blockSize;

    // Reserve inode 1 (bad blocks inode - unused, just skip)
    // Inode 2 = root directory
    var rootInodeOffset = inodeTableOffset + 1 * inodeSize; // inode 2 is at index 1

    var nextInode = 3u; // next available inode
    var nextBlock = firstFreeBlock;

    // Allocate a block for root directory data
    var rootDirBlock = nextBlock++;
    MarkBlockUsed(disk, blockBitmapOffset, rootDirBlock);

    // Build root directory entries
    var rootDirData = new byte[blockSize];
    var dirPos = 0;

    // "." entry (self)
    dirPos = WriteDirEntry(rootDirData, dirPos, 2, ".", 2, blockSize, false);
    // ".." entry (parent, same as self for root)
    dirPos = WriteDirEntry(rootDirData, dirPos, 2, "..", 2, blockSize, false);

    // Add file entries
    var fileInodes = new List<(uint Inode, string Name, byte[] Data)>();
    foreach (var (name, data) in _files) {
      var fileInode = nextInode++;
      // Mark inode as used
      var inodeBitIndex = (int)(fileInode - 1);
      disk[inodeBitmapOffset + inodeBitIndex / 8] |= (byte)(1 << (inodeBitIndex % 8));

      fileInodes.Add((fileInode, name, data));
      dirPos = WriteDirEntry(rootDirData, dirPos, fileInode, name, 1, blockSize,
        fileInode == nextInode - 1 && _files.IndexOf((name, data)) == _files.Count - 1);
    }

    // If no files were added, adjust the last ".." entry to fill remaining space
    if (_files.Count == 0) {
      // Recalculate: ".." should be the last entry and consume remaining space
      dirPos = 0;
      dirPos = WriteDirEntry(rootDirData, dirPos, 2, ".", 2, blockSize, false);
      WriteDirEntry(rootDirData, dirPos, 2, "..", 2, blockSize, true);
    }

    // Write root directory data to its block
    rootDirData.CopyTo(disk, rootDirBlock * blockSize);

    // Write file data and inodes
    foreach (var (fileInode, name, data) in fileInodes) {
      var fileInodeOffset = inodeTableOffset + (int)(fileInode - 1) * inodeSize;

      // Allocate blocks for file data
      var blocksNeeded = data.Length == 0 ? 0 : (data.Length + blockSize - 1) / blockSize;
      var fileBlocks = new List<int>();
      for (var b = 0; b < blocksNeeded && b < 12; b++) { // max 12 direct blocks
        fileBlocks.Add(nextBlock);
        MarkBlockUsed(disk, blockBitmapOffset, nextBlock);
        nextBlock++;
      }

      // Write file data to blocks
      var written = 0;
      foreach (var fb in fileBlocks) {
        var toWrite = Math.Min(blockSize, data.Length - written);
        if (toWrite > 0)
          Array.Copy(data, written, disk, fb * blockSize, toWrite);
        written += toWrite;
      }

      // Write file inode
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(fileInodeOffset), 0x8000 | 0x01A4); // regular file, 0644
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fileInodeOffset + 4), (uint)data.Length); // size
      // Block pointers at offset 40
      for (var b = 0; b < fileBlocks.Count; b++) {
        BinaryPrimitives.WriteUInt32LittleEndian(
          disk.AsSpan(fileInodeOffset + 40 + b * 4), (uint)fileBlocks[b]);
      }
    }

    // Write root inode (inode 2)
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(rootInodeOffset), 0x4000 | 0x01ED); // directory, 0755
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(rootInodeOffset + 4), (uint)blockSize); // size = 1 block
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(rootInodeOffset + 40), (uint)rootDirBlock); // direct block 0

    return disk;
  }

  private static int WriteDirEntry(byte[] dirData, int pos, uint inode, string name, byte fileType, int blockSize, bool isLast) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var entrySize = 8 + nameBytes.Length;
    // Align to 4 bytes
    entrySize = (entrySize + 3) & ~3;

    var recLen = isLast ? blockSize - pos : entrySize;

    BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(pos), inode);
    BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(pos + 4), (ushort)recLen);
    dirData[pos + 6] = (byte)nameBytes.Length;
    dirData[pos + 7] = fileType;
    nameBytes.CopyTo(dirData, pos + 8);

    return pos + entrySize;
  }

  private static void MarkBlockUsed(byte[] disk, int bitmapOffset, int blockNum) {
    disk[bitmapOffset + blockNum / 8] |= (byte)(1 << (blockNum % 8));
  }
}
