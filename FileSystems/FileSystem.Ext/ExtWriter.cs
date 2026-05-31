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

  /// <summary>
  /// Builds the image with the block size chosen by
  /// <see cref="Compression.Core.Layout.FilesystemLayoutOptimizer"/> to minimise
  /// slack + metadata overhead, and the block count sized to exactly hold the files.
  /// </summary>
  /// <param name="requestedBlockSize">Block size in bytes (0 = auto-select).</param>
  public byte[] BuildAutoSized(int requestedBlockSize = 0) {
    var fileSizes = _files.Select(f => (long)f.Data.Length).ToList();
    var estimatedInodes = ChooseInodeCount(_files.Count + 1);

    // ext block sizes: 1 KB, 2 KB, 4 KB (this minimal writer supports up to 4 KB).
    int[] candidates = [1024, 2048, 4096];
    var blockSize = requestedBlockSize > 0
      ? requestedBlockSize
      : Compression.Core.Layout.FilesystemLayoutOptimizer.SelectClusterSize(
          candidates,
          bs => {
            var clusters = Compression.Core.Layout.FilesystemLayoutOptimizer.DataClusters(fileSizes, bs);
            var slack    = Compression.Core.Layout.FilesystemLayoutOptimizer.Slack(fileSizes, bs);
            // ext metadata: superblock + group desc + 2 bitmaps + inode table.
            var inodeTableBytes = estimatedInodes * 128; // inodesPerGroup × inodeSize
            var metaBytes = 4L * bs + inodeTableBytes;
            return slack + metaBytes;
          });

    // Distinct directories implied by the nested paths (the root plus every
    // path prefix), and the number of entries (children) each holds — a
    // directory's entries can span several data blocks once they overflow one.
    var dirPaths = new HashSet<string> { "" };
    var dirEntryCount = new Dictionary<string, int>();
    foreach (var (name, _) in _files) {
      var segments = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      var prefix = "";
      for (var s = 0; s < segments.Length - 1; ++s) {
        var parent = prefix;
        prefix = prefix.Length == 0 ? segments[s] : prefix + "/" + segments[s];
        if (dirPaths.Add(prefix))
          dirEntryCount[parent] = dirEntryCount.GetValueOrDefault(parent) + 1; // subdir adds an entry to its parent
      }
      // The leaf file adds an entry to its immediate parent directory.
      dirEntryCount[prefix] = dirEntryCount.GetValueOrDefault(prefix) + 1;
    }

    // Each directory needs enough data blocks to hold its entries (its own
    // "."/".." plus children), so estimate per-directory block usage and add a
    // singly-indirect block whenever a directory needs more than 12 blocks.
    var dirBlocks = 0L;
    foreach (var dir in dirPaths) {
      var entries = dirEntryCount.GetValueOrDefault(dir) + 2; // + "." + ".."
      // Worst-case entry size for the short names this tool produces is small,
      // but reserve a generous fixed slot so the estimate never under-shoots.
      var perBlock = Math.Max(1, blockSize / 32);
      var blocks = (entries + perBlock - 1) / perBlock;
      dirBlocks += blocks + (blocks > 12 ? 1 : 0); // + singly-indirect block
    }

    // Inode table must hold the reserved inodes, every directory and every file.
    var inodeCount = ChooseInodeCount(dirPaths.Count + _files.Count);
    var inodeTableBlocks = (inodeCount * 128 + blockSize - 1) / blockSize;
    var dataBlocks = fileSizes.Sum(s => s <= 0 ? 0L : (s + blockSize - 1) / blockSize);
    var totalBlocks = (int)Math.Max(4096, (4 + dirBlocks + inodeTableBlocks + dataBlocks) * 11 / 10);
    return Build(blockSize, totalBlocks);
  }

  public byte[] Build(int blockSize = 1024, int totalBlocks = 4096) {
    const ushort ExtMagic = 0xEF53;
    // EXT2_GOOD_OLD_FIRST_INO — first inode available for user files on a
    // revision-0 (GOOD_OLD_REV) filesystem. Inodes 1..10 are reserved:
    //   1=bad-blocks, 2=root, 3=ACL-idx (obsolete), 4=ACL-data (obsolete),
    //   5=boot-loader, 6=undeleted-dir, 7=resize, 8=journal, 9=exclude,
    //   10=replica.  e2fsck refuses to accept dirents pointing at 3..10.
    const uint FirstUserInode = 11;
    // EXT4 feature flags (fs/ext4/ext4.h).
    const uint FeatureIncompatFiletype = 0x0002;
    // Dynamic revision — required so s_inode_size / s_first_ino / feature
    // flags are honoured by the kernel and fsck.
    const uint RevLevelDynamic = 1;

    var firstDataBlock = blockSize == 1024 ? 1u : 0u;
    const int inodeSize = 128;
    // The inode table must hold the reserved inodes plus one inode per directory
    // and per file; a single block group's inode count is sized to fit them all.
    var inodesPerGroup = ChooseInodeCount((int)FirstUserInode + CountDirectories() + _files.Count);
    var inodeTableBlocks = (inodesPerGroup * inodeSize + blockSize - 1) / blockSize;
    // Metadata layout: SB(1) + BGD(1) + block_bitmap(1) + inode_bitmap(1) +
    // inode_table(inodeTableBlocks). First free block = after all metadata.
    var firstFreeBlock = (int)firstDataBlock + 4 + inodeTableBlocks;
    var disk = new byte[totalBlocks * blockSize];
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // --- Block bitmap: mark all metadata blocks up through the inode-table
    //     tail as used. Bit N in the bitmap refers to block (firstDataBlock
    //     + N), so blocks 0..firstDataBlock-1 (the boot-area slot on 1 KiB
    //     filesystems) are implicit and not tracked by any bit. ---
    var blockBitmapOffset = (int)(firstDataBlock + 2) * blockSize;
    for (var b = (int)firstDataBlock; b < firstFreeBlock; ++b) {
      var bitIdx = b - (int)firstDataBlock;
      disk[blockBitmapOffset + bitIdx / 8] |= (byte)(1 << (bitIdx % 8));
    }

    // --- Inode bitmap: inodes 1..(FirstUserInode-1) are all reserved;
    // inode 2 (root) is actually in use. Bitmap bit i corresponds to
    // inode (i+1). Set bits for inodes 1..10 so fsck doesn't flag "reserved
    // inode in use but empty", which is the default mkfs.ext4 behaviour. ---
    var inodeBitmapOffset = (int)(firstDataBlock + 3) * blockSize;
    for (var ino = 1u; ino < FirstUserInode; ++ino) {
      var idx = (int)(ino - 1);
      disk[inodeBitmapOffset + idx / 8] |= (byte)(1 << (idx % 8));
    }

    // --- Inode/block allocation cursors ---
    var inodeTableOffset = (int)(firstDataBlock + 4) * blockSize;
    var nextInode = FirstUserInode;
    var nextBlock = firstFreeBlock;

    // --- Build the directory tree from the (possibly nested) file paths. ---
    // The root directory is inode 2. Every path segment before the final name
    // becomes a real subdirectory inode; the final segment is the regular file.
    const uint RootInode = 2;
    var root = new DirNode { Inode = RootInode, Parent = RootInode };

    var fileInodes = new List<(uint Inode, DirNode Parent, string LeafName, byte[] Data)>();
    foreach (var (name, data) in _files) {
      var segments = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segments.Length == 0) continue;

      var dir = root;
      for (var s = 0; s < segments.Length - 1; ++s) {
        var segment = segments[s];
        if (!dir.Subdirs.TryGetValue(segment, out var child)) {
          child = new DirNode { Inode = nextInode++, Parent = dir.Inode, Name = segment };
          dir.Subdirs.Add(segment, child);
        }
        dir = child;
      }

      var leaf = segments[^1];
      var fileInode = nextInode++;
      fileInodes.Add((fileInode, dir, leaf, data));
      dir.Files.Add((leaf, fileInode));
    }

    // --- Mark every allocated inode (directories + files) as used. ---
    var allDirs = new List<DirNode>();
    CollectDirs(root, allDirs);
    foreach (var node in allDirs) {
      if (node.Inode < FirstUserInode) continue; // root (inode 2) reserved-bit is set above
      var bit = (int)(node.Inode - 1);
      disk[inodeBitmapOffset + bit / 8] |= (byte)(1 << (bit % 8));
    }
    foreach (var (fileInode, _, _, _) in fileInodes) {
      var bit = (int)(fileInode - 1);
      disk[inodeBitmapOffset + bit / 8] |= (byte)(1 << (bit % 8));
    }

    // --- Lay out each directory's entries across one or more data blocks. ---
    // Records never straddle a block boundary: when the next record would not
    // fit, the current block's last record has its rec_len padded to the block
    // end and the next record opens a fresh block. "." / ".." remain the first
    // two records of the first block. Up to 12 direct blocks are used, after
    // which a singly-indirect block chains the rest.
    var sectorsPerBlock = blockSize / 512;
    const int MaxDirEntryBytes = 8 + 255 + 3 & ~3; // largest possible single dirent
    var dirBlockLists = new Dictionary<uint, List<int>>();
    foreach (var node in allDirs) {
      // Assemble the ordered list of entries this directory holds.
      var entries = new List<(uint Inode, string Name, byte FileType)> {
        (node.Inode, ".", 2),
        (node.Parent, "..", 2),
      };
      foreach (var child in node.Subdirs.Values)
        entries.Add((child.Inode, child.Name, 2));
      foreach (var (leaf, fileInode) in node.Files)
        entries.Add((fileInode, leaf, 1));

      // Split the entries into per-block runs, then emit each block, padding the
      // final record of every block to the block end.
      var blockList = new List<int>();
      var idx = 0;
      while (idx < entries.Count) {
        var blockData = new byte[blockSize];
        var pos = 0;
        var firstInBlock = idx;
        while (idx < entries.Count) {
          var nameLen = Encoding.UTF8.GetByteCount(entries[idx].Name);
          var size = 8 + nameLen + 3 & ~3;
          if (pos + size > blockSize) break; // does not fit; close this block
          pos += size;
          ++idx;
        }
        pos = 0; // reset for the write pass below
        if (idx == firstInBlock)
          throw new InvalidOperationException(
            $"ext2 writer: directory entry '{entries[firstInBlock].Name}' exceeds a single {blockSize}-byte block.");

        for (var e = firstInBlock; e < idx; ++e) {
          var (inode, name, fileType) = entries[e];
          var isLast = e == idx - 1; // last record in this block gets padded rec_len
          pos = WriteDirEntry(blockData, pos, inode, name, fileType, blockSize, isLast);
        }

        var blockNum = nextBlock++;
        MarkBlockUsed(disk, blockBitmapOffset, blockNum, (int)firstDataBlock);
        blockData.CopyTo(disk, blockNum * blockSize);
        blockList.Add(blockNum);
      }

      node.Block = blockList[0];
      dirBlockLists[node.Inode] = blockList;

      const int MaxDirectBlocks = 12;
      var pointersPerBlock = blockSize / 4;
      var maxDirBlocks = MaxDirectBlocks + pointersPerBlock;
      if (blockList.Count > maxDirBlocks)
        throw new InvalidOperationException(
          $"ext2 writer: directory '{(node.Inode == RootInode ? "/" : node.Name)}' needs {blockList.Count} blocks " +
          $"but only direct + singly-indirect blocks are supported (max {maxDirBlocks} blocks, " +
          $"≈ {maxDirBlocks * (blockSize / MaxDirEntryBytes)} entries at {blockSize}-byte blocks).");
    }

    // --- Directory inodes (root + every subdirectory). ---
    // i_links_count for a directory = 2 (its own "." and the parent's entry for
    // it) + one per child subdirectory (each child's ".." links back here).
    foreach (var node in allDirs) {
      var blockList = dirBlockLists[node.Inode];
      const int MaxDirectBlocks = 12;

      // A singly-indirect block holds the block pointers past the first 12.
      var indirectBlockNum = 0;
      var allocatedBlocks = blockList.Count;
      if (blockList.Count > MaxDirectBlocks) {
        indirectBlockNum = nextBlock++;
        MarkBlockUsed(disk, blockBitmapOffset, indirectBlockNum, (int)firstDataBlock);
        ++allocatedBlocks; // the indirect block itself counts toward i_blocks
        var indOff = indirectBlockNum * blockSize;
        for (var p = MaxDirectBlocks; p < blockList.Count; ++p)
          BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(indOff + (p - MaxDirectBlocks) * 4), (uint)blockList[p]);
      }

      var dirInodeOffset = inodeTableOffset + (int)(node.Inode - 1) * inodeSize;
      var dirIno = disk.AsSpan(dirInodeOffset, inodeSize);
      BinaryPrimitives.WriteUInt16LittleEndian(dirIno, 0x4000 | 0x01ED);             // i_mode: directory, 0755
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[4..], (uint)(blockList.Count * blockSize)); // i_size
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[8..], now);                    // i_atime
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[12..], now);                   // i_ctime
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[16..], now);                   // i_mtime
      BinaryPrimitives.WriteUInt16LittleEndian(dirIno[26..], (ushort)(2 + node.Subdirs.Count)); // i_links_count
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[28..], (uint)(allocatedBlocks * sectorsPerBlock)); // i_blocks
      var directCount = Math.Min(MaxDirectBlocks, blockList.Count);
      for (var b = 0; b < directCount; ++b)
        BinaryPrimitives.WriteUInt32LittleEndian(dirIno[(40 + b * 4)..], (uint)blockList[b]); // direct blocks 0..11
      if (indirectBlockNum != 0)
        BinaryPrimitives.WriteUInt32LittleEndian(dirIno[88..], (uint)indirectBlockNum); // singly-indirect pointer
    }

    // --- File inodes + data blocks ---
    // Files use up to 12 direct block pointers, then a singly-indirect block
    // (blockSize/4 further pointers). The indirect block itself counts toward
    // i_blocks (e2fsck tallies it in the 512-byte sector count).
    const int MaxDirectFileBlocks = 12;
    var filerPointersPerBlock = blockSize / 4;
    var maxFileBlocks = MaxDirectFileBlocks + filerPointersPerBlock;
    foreach (var (fileInode, _, _, data) in fileInodes) {
      var fileInodeOffset = inodeTableOffset + (int)(fileInode - 1) * inodeSize;

      var blocksNeeded = data.Length == 0 ? 0 : (data.Length + blockSize - 1) / blockSize;
      if (blocksNeeded > maxFileBlocks)
        throw new InvalidOperationException(
          $"ext2 writer supports direct + singly-indirect blocks only " +
          $"(max {maxFileBlocks * blockSize} bytes per file at {blockSize}-byte blocks).");

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

      // Past the first 12 data blocks, a singly-indirect block holds the rest.
      var fileIndirectBlockNum = 0;
      var fileAllocatedBlocks = fileBlocks.Count;
      if (fileBlocks.Count > MaxDirectFileBlocks) {
        fileIndirectBlockNum = nextBlock++;
        MarkBlockUsed(disk, blockBitmapOffset, fileIndirectBlockNum, (int)firstDataBlock);
        ++fileAllocatedBlocks; // the indirect block itself counts toward i_blocks
        var indOff = fileIndirectBlockNum * blockSize;
        for (var p = MaxDirectFileBlocks; p < fileBlocks.Count; ++p)
          BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(indOff + (p - MaxDirectFileBlocks) * 4), (uint)fileBlocks[p]);
      }

      var ino = disk.AsSpan(fileInodeOffset, inodeSize);
      BinaryPrimitives.WriteUInt16LittleEndian(ino, 0x8000 | 0x01A4);           // i_mode: regular file, 0644
      BinaryPrimitives.WriteUInt32LittleEndian(ino[4..], (uint)data.Length);    // i_size
      BinaryPrimitives.WriteUInt32LittleEndian(ino[8..], now);                  // i_atime
      BinaryPrimitives.WriteUInt32LittleEndian(ino[12..], now);                 // i_ctime
      BinaryPrimitives.WriteUInt32LittleEndian(ino[16..], now);                 // i_mtime
      BinaryPrimitives.WriteUInt16LittleEndian(ino[26..], 1);                   // i_links_count
      BinaryPrimitives.WriteUInt32LittleEndian(ino[28..], (uint)(fileAllocatedBlocks * sectorsPerBlock)); // i_blocks (512-byte sectors)
      var fileDirectCount = Math.Min(MaxDirectFileBlocks, fileBlocks.Count);
      for (var b = 0; b < fileDirectCount; ++b)
        BinaryPrimitives.WriteUInt32LittleEndian(ino[(40 + b * 4)..], (uint)fileBlocks[b]); // direct blocks 0..11
      if (fileIndirectBlockNum != 0)
        BinaryPrimitives.WriteUInt32LittleEndian(ino[88..], (uint)fileIndirectBlockNum);    // singly-indirect pointer
    }

    // --- Free-count accounting (what fsck scrutinises) ---
    // Total inodes = inodesPerGroup; used = (FirstUserInode-1) reserved
    // inodes + subdirectory inodes (root inode 2 is already among the reserved
    // slots) + file inodes. The reserved slots are "in use" as far as the inode
    // bitmap is concerned — their bits are set above.
    var subdirCount = (uint)(allDirs.Count - 1); // exclude root (inode 2)
    var usedInodes = (FirstUserInode - 1) + subdirCount + (uint)fileInodes.Count;
    var freeInodes = (uint)inodesPerGroup - usedInodes;
    // Used blocks = firstFreeBlock (metadata) + one block per directory + sum of file blocks.
    var usedBlocks = (uint)nextBlock;
    var freeBlocks = (uint)totalBlocks - usedBlocks;
    var usedDirs = (uint)allDirs.Count; // root + every subdirectory only

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
    BinaryPrimitives.WriteUInt32LittleEndian(sb[76..], RevLevelDynamic);           // s_rev_level = DYNAMIC_REV
    BinaryPrimitives.WriteUInt16LittleEndian(sb[80..], 0);                         // s_def_resuid
    BinaryPrimitives.WriteUInt16LittleEndian(sb[82..], 0);                         // s_def_resgid
    // Dynamic-rev extension fields start at offset 84. s_first_ino tells
    // fsck which inode number user files may start at — without this set
    // (default 11 for GOOD_OLD_REV), any dirent pointing at inodes 3..10
    // is flagged as "invalid inode # reserved".
    BinaryPrimitives.WriteUInt32LittleEndian(sb[84..], FirstUserInode);            // s_first_ino
    BinaryPrimitives.WriteUInt16LittleEndian(sb[88..], inodeSize);                 // s_inode_size
    // s_feature_incompat at offset 96. FILETYPE (0x0002) tells fsck that
    // the dirent's file_type byte is authoritative; without this flag, any
    // non-zero file_type is reported as corruption.
    BinaryPrimitives.WriteUInt32LittleEndian(sb[96..], FeatureIncompatFiletype);   // s_feature_incompat

    // UUID at offset 104 (16 bytes) — blkid/dumpe2fs rely on this to identify
    // the filesystem. The kernel accepts any non-zero UUID at rev 0 (it becomes
    // mandatory at rev 1, which is harmless to set unconditionally).
    var uuid = Guid.NewGuid().ToByteArray();
    uuid.CopyTo(sb.Slice(104, 16));
    // Volume label at offset 120 (16 bytes) — optional, left empty. Last-mount
    // path at offset 136 (64 bytes) — also optional.

    // --- Padding at the tail of each bitmap block must be set to 1 per
    //     mkfs convention; fsck flags unset padding as a corruption hint. ---
    var blockBitmapBits = totalBlocks - (int)firstDataBlock;
    var blockBitmapBytes = blockSize;
    for (var bit = blockBitmapBits; bit < blockBitmapBytes * 8; ++bit)
      disk[blockBitmapOffset + bit / 8] |= (byte)(1 << (bit % 8));

    var inodeBitmapBits = inodesPerGroup;
    var inodeBitmapBytes = blockSize;
    for (var bit = inodeBitmapBits; bit < inodeBitmapBytes * 8; ++bit)
      disk[inodeBitmapOffset + bit / 8] |= (byte)(1 << (bit % 8));

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

  // Rounds the required inode count up to a sensible group size. The minimal
  // writer keeps a single block group, so the inode count is simply sized to
  // hold every reserved/dir/file inode with headroom, never below the classic 128.
  private static int ChooseInodeCount(int needed) {
    var withHeadroom = Math.Max(128, needed + needed / 10 + 16);
    // Round up to a multiple of 8 so the inode bitmap byte boundaries stay tidy.
    return withHeadroom + 7 & ~7;
  }

  // Counts the distinct directories (root + every path prefix) the added files imply.
  private int CountDirectories() {
    var dirs = new HashSet<string> { "" };
    foreach (var (name, _) in _files) {
      var segments = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      var prefix = "";
      for (var s = 0; s < segments.Length - 1; ++s) {
        prefix = prefix.Length == 0 ? segments[s] : prefix + "/" + segments[s];
        dirs.Add(prefix);
      }
    }
    return dirs.Count;
  }

  // A node in the directory tree assembled from the added file paths. Each node
  // becomes one directory inode whose entries span one or more data blocks.
  private sealed class DirNode {
    public uint Inode;
    public uint Parent;
    public string Name = "";
    public int Block;
    public readonly Dictionary<string, DirNode> Subdirs = [];
    public readonly List<(string Name, uint Inode)> Files = [];
  }

  // Depth-first walk that yields the root first, then each subdirectory, so the
  // root is laid out before its children (mirroring how mkfs orders inodes).
  private static void CollectDirs(DirNode node, List<DirNode> into) {
    into.Add(node);
    foreach (var child in node.Subdirs.Values)
      CollectDirs(child, into);
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

  // Marks a block as "used" in the block bitmap. The bitmap's bit 0 refers to
  // block s_first_data_block (1 on 1 KiB filesystems, 0 otherwise), so the
  // caller-supplied absolute block number must be biased by firstDataBlock
  // before indexing — otherwise every bit is off by one and e2fsck reports a
  // block-bitmap difference plus a wrong free-block count.
  private static void MarkBlockUsed(byte[] disk, int bitmapOffset, int blockNum, int firstDataBlock) {
    var bit = blockNum - firstDataBlock;
    disk[bitmapOffset + bit / 8] |= (byte)(1 << (bit % 8));
  }
}
