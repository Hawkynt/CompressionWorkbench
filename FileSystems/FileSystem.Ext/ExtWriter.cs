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
            const int inodeTableBytes = 128 * 128; // inodesPerGroup × inodeSize
            var metaBytes = 4L * bs + inodeTableBytes;
            return slack + metaBytes;
          });

    // Distinct directories implied by the nested paths (the root plus every
    // path prefix) — each consumes one directory data block.
    var dirPaths = new HashSet<string> { "" };
    foreach (var (name, _) in _files) {
      var segments = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      var prefix = "";
      for (var s = 0; s < segments.Length - 1; ++s) {
        prefix = prefix.Length == 0 ? segments[s] : prefix + "/" + segments[s];
        dirPaths.Add(prefix);
      }
    }

    // Size block count: metadata (≈ 4 blocks + inode table) + one block per
    // directory + file data blocks + 10 % headroom.
    var inodeTableBlocks = (128 * 128 + blockSize - 1) / blockSize;
    var dataBlocks = fileSizes.Sum(s => s <= 0 ? 0L : (s + blockSize - 1) / blockSize);
    var totalBlocks = (int)Math.Max(4096, (4 + dirPaths.Count + inodeTableBlocks + dataBlocks) * 11 / 10);
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
    const int inodesPerGroup = 128;
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

    // --- Allocate one data block per directory and emit its entries. ---
    // Each directory's entries (its "." / ".." plus children) must fit a single
    // block; a deeper writer would chain blocks, but a single block holds tens
    // of entries which is ample for the images this tool builds.
    var sectorsPerBlock = blockSize / 512;
    foreach (var node in allDirs) {
      node.Block = nextBlock++;
      MarkBlockUsed(disk, blockBitmapOffset, node.Block);

      var dirData = new byte[blockSize];
      var dirPos = 0;
      dirPos = WriteDirEntry(dirData, dirPos, node.Inode, ".", fileType: 2, blockSize, isLast: false);

      var childDirs = node.Subdirs.Values.ToList();
      var lastIsFile = node.Files.Count > 0;
      var dotDotIsLast = childDirs.Count == 0 && node.Files.Count == 0;
      dirPos = WriteDirEntry(dirData, dirPos, node.Parent, "..", fileType: 2, blockSize, isLast: dotDotIsLast);

      for (var i = 0; i < childDirs.Count; ++i) {
        var isLast = !lastIsFile && i == childDirs.Count - 1;
        dirPos = WriteDirEntry(dirData, dirPos, childDirs[i].Inode, childDirs[i].Name, fileType: 2, blockSize, isLast);
      }
      for (var i = 0; i < node.Files.Count; ++i) {
        var (leaf, fileInode) = node.Files[i];
        var isLast = i == node.Files.Count - 1;
        dirPos = WriteDirEntry(dirData, dirPos, fileInode, leaf, fileType: 1, blockSize, isLast);
      }

      if (dirPos > blockSize)
        throw new InvalidOperationException(
          $"ext2 writer: directory '{(node.Inode == RootInode ? "/" : node.Name)}' has too many entries for a single {blockSize}-byte block.");

      dirData.CopyTo(disk, node.Block * blockSize);
    }

    // --- Directory inodes (root + every subdirectory). ---
    // i_links_count for a directory = 2 (its own "." and the parent's entry for
    // it) + one per child subdirectory (each child's ".." links back here).
    foreach (var node in allDirs) {
      var dirInodeOffset = inodeTableOffset + (int)(node.Inode - 1) * inodeSize;
      var dirIno = disk.AsSpan(dirInodeOffset, inodeSize);
      BinaryPrimitives.WriteUInt16LittleEndian(dirIno, 0x4000 | 0x01ED);             // i_mode: directory, 0755
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[4..], (uint)blockSize);        // i_size
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[8..], now);                    // i_atime
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[12..], now);                   // i_ctime
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[16..], now);                   // i_mtime
      BinaryPrimitives.WriteUInt16LittleEndian(dirIno[26..], (ushort)(2 + node.Subdirs.Count)); // i_links_count
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[28..], (uint)sectorsPerBlock); // i_blocks
      BinaryPrimitives.WriteUInt32LittleEndian(dirIno[40..], (uint)node.Block);      // direct block 0
    }

    // --- File inodes + data blocks ---
    foreach (var (fileInode, _, _, data) in fileInodes) {
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

  // A node in the directory tree assembled from the added file paths. Each node
  // becomes one directory inode with a single data block holding its entries.
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

  // Marks a block as "used" in the block bitmap. The bitmap's bit 0 refers
  // to block (firstDataBlock), so the caller-supplied block number must be
  // adjusted by firstDataBlock before indexing. Callers in this file always
  // pass firstDataBlock=1 (1 KiB-block default) so we hard-code the bias.
  private static void MarkBlockUsed(byte[] disk, int bitmapOffset, int blockNum, int firstDataBlock = 1) {
    var bit = blockNum - firstDataBlock;
    disk[bitmapOffset + bit / 8] |= (byte)(1 << (bit % 8));
  }
}
