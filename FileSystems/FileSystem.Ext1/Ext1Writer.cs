#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
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

  /// <summary>Fixed rev-0 inode size; GOOD_OLD_REV stores no s_inode_size field.</summary>
  private const int InodeSize = 128;

  /// <summary>A file's payload: held inline, or opened on demand when it is too large to hold.</summary>
  private readonly record struct FileEntry(string Name, long Size, byte[]? Data, Func<Stream>? Opener);

  private readonly List<FileEntry> _files = [];

  /// <summary>Adds a file to be packed into the next <see cref="Build"/> call.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add(new FileEntry(name, data.LongLength, data, null));
  }

  /// <summary>
  /// Adds a file whose bytes are produced on demand. <paramref name="size" /> must
  /// match what <paramref name="openStream" /> yields; the layout is settled from
  /// it before a byte is read, so a file larger than a byte[] can carry is placed
  /// like any other.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    ArgumentOutOfRangeException.ThrowIfNegative(size);
    this._files.Add(new FileEntry(name, size, null, openStream));
  }

  /// <summary>
  /// Materialises a complete ext1 image as a byte array. Default geometry produces a
  /// 4 MiB image with 1024-byte blocks (<c>s_log_block_size</c> = 0) and a single block
  /// group — the canonical layout for early-1990s small partitions.
  /// </summary>
  public byte[] Build(int blockSize = 1024, int totalBlocks = 4096) {
    var image = this.BuildCore(blockSize, totalBlocks, out var dataWrites);
    var full = image.Materialise();
    using var target = new MemoryStream(full, writable: true);
    WriteEntryData(target, dataWrites, blockSize);
    return full;
  }

  /// <summary>
  /// Writes the volume into a seekable stream: the metadata, then each file's
  /// bytes at the blocks it was allocated. Only the blocks the filesystem touches
  /// are held, so a volume larger than a byte[] can address is producible.
  /// </summary>
  public void BuildTo(Stream output, int blockSize, int totalBlocks) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildTo requires a writable, seekable stream.", nameof(output));

    var image = this.BuildCore(blockSize, totalBlocks, out var dataWrites);
    var basePosition = output.Position;
    image.WriteTo(output);
    WriteEntryData(output, dataWrites, blockSize, basePosition);
    output.Position = basePosition + image.TotalBytes;
    output.Flush();
  }

  /// <summary>
  /// Copies each entry's bytes into the blocks it was allocated. The allocator
  /// hands out ascending blocks, so a map is contiguous except where it steps
  /// over a group's metadata; writing it as runs costs one seek per boundary
  /// rather than assuming a single run, which placed everything past the first
  /// group at the wrong offset.
  /// </summary>
  private static void WriteEntryData(Stream output, List<(IReadOnlyList<int> Blocks, FileEntry Entry)> dataWrites,
      int blockSize, long basePosition = 0) {
    var buffer = new byte[Math.Max(blockSize, 1024 * 1024)];
    foreach (var (blocks, entry) in dataWrites) {
      if (entry.Size <= 0 || blocks.Count == 0) continue;
      using var src = entry.Data is { } inline ? new MemoryStream(inline, writable: false) : entry.Opener!();
      var remaining = entry.Size;

      var runStart = 0;
      while (runStart < blocks.Count && remaining > 0) {
        var runEnd = runStart;
        while (runEnd + 1 < blocks.Count && blocks[runEnd + 1] == blocks[runEnd] + 1) ++runEnd;

        var runBytes = Math.Min(remaining, (long)(runEnd - runStart + 1) * blockSize);
        output.Position = basePosition + (long)blocks[runStart] * blockSize;
        while (runBytes > 0) {
          var n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, runBytes));
          if (n <= 0) { remaining = 0; break; }
          output.Write(buffer, 0, n);
          runBytes -= n;
          remaining -= n;
        }
        runStart = runEnd + 1;
      }
    }
  }

  /// <summary>
  /// Block count a volume needs to hold the files added, at the given block size.
  /// The 4 MiB default footprint only fits a few megabytes of payload, so anything
  /// larger has to be sized from the file set.
  /// </summary>
  public int PlanTotalBlocks(int blockSize) {
    var pointersPerBlock = Math.Max(1, blockSize / 4);
    var dataBlocks = 0L;
    var pointerBlocks = 0L;
    foreach (var entry in this._files) {
      if (entry.Size <= 0) continue;
      var n = (entry.Size + blockSize - 1) / blockSize;
      dataBlocks += n;
      var past = n - 12;
      if (past <= 0) continue;
      var lvl1 = (past + pointersPerBlock - 1) / pointersPerBlock;
      var lvl2 = (lvl1 + pointersPerBlock - 1) / pointersPerBlock;
      pointerBlocks += lvl1 + lvl2 + 2; // + the double- and triple-indirect roots
    }

    // Root directory entries, generously estimated, plus its own indirect block.
    var perBlock = Math.Max(1, blockSize / 32);
    var rootBlocks = (this._files.Count + 2 + perBlock - 1) / perBlock;
    rootBlocks += rootBlocks > 12 ? 1 : 0;

    var payload = dataBlocks + pointerBlocks + rootBlocks;
    var totalBlocks = (int)Math.Max(4L * 1024 * 1024 / blockSize, payload * 11 / 10);

    // Each group carries its own metadata, so the volume has to grow to cover it.
    var neededInodes = 11 + this._files.Count;
    for (var pass = 0; pass < 3; ++pass) {
      var geo = ExtBlockGroupGeometry.Compute(blockSize, totalBlocks, InodeSize, neededInodes);
      var needed = payload + (long)geo.GroupCount * geo.PerGroupMetaBlocks;
      var want = (int)Math.Max(4L * 1024 * 1024 / blockSize, Math.Min(int.MaxValue / 2, needed * 11 / 10));
      if (want <= totalBlocks) break;
      totalBlocks = want;
    }
    return totalBlocks;
  }

  private SparseBlockImage BuildCore(int blockSize, int totalBlocks,
                                     out List<(IReadOnlyList<int> Blocks, FileEntry Entry)> dataWrites) {
    const ushort Ext1Magic = 0xEF51;
    // Inodes 1..10 are reserved by ext convention (bad-blocks, root, ACL slots,
    // boot-loader, undeleted-dir, etc.). User files start at inode 11.
    const uint FirstUserInode = 11;
    // GOOD_OLD_REV — original ext1/early-ext2 revision. No s_first_ino,
    // s_inode_size, or feature flags are stored or interpreted.
    const uint RevLevelGoodOld = 0;
    const int MaxDirectBlocks = 12;

    var neededInodes = (int)FirstUserInode + this._files.Count;
    var geo = ExtBlockGroupGeometry.Compute(blockSize, totalBlocks, InodeSize, neededInodes);
    totalBlocks = geo.TotalBlocks;
    var firstDataBlock = geo.FirstDataBlock;
    var blocksPerGroup = geo.BlocksPerGroup;
    var groupCount = geo.GroupCount;
    var gdtBlocks = geo.GdtBlocks;
    var inodesPerGroup = geo.InodesPerGroup;
    var perGroupMeta = geo.PerGroupMetaBlocks;

    var img = new SparseBlockImage(blockSize, (long)totalBlocks * blockSize);
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var sectorsPerBlock = blockSize / 512;

    int GroupStart(int g) => firstDataBlock + g * blocksPerGroup;
    int GroupBlocks(int g) => (int)Math.Min(blocksPerGroup, (long)totalBlocks - GroupStart(g));
    int BlockBitmapBlock(int g) => GroupStart(g) + 1 + gdtBlocks;
    int InodeBitmapBlock(int g) => BlockBitmapBlock(g) + 1;
    int InodeTableBlock(int g) => BlockBitmapBlock(g) + 2;

    void MarkBlockUsed(int block) {
      var g = (block - firstDataBlock) / blocksPerGroup;
      var bit = block - GroupStart(g);
      img.Block(BlockBitmapBlock(g))[bit / 8] |= (byte)(1 << (bit % 8));
    }

    long InodeOffset(uint inode) {
      var g = (int)((inode - 1) / (uint)inodesPerGroup);
      var idx = (int)((inode - 1) % (uint)inodesPerGroup);
      return (long)InodeTableBlock(g) * blockSize + (long)idx * InodeSize;
    }

    void MarkInodeUsed(uint inode) {
      var g = (int)((inode - 1) / (uint)inodesPerGroup);
      var idx = (int)((inode - 1) % (uint)inodesPerGroup);
      img.Block(InodeBitmapBlock(g))[idx / 8] |= (byte)(1 << (idx % 8));
    }

    var nextBlock = firstDataBlock + perGroupMeta;
    int AllocBlock() {
      while (nextBlock < totalBlocks) {
        var g = (nextBlock - firstDataBlock) / blocksPerGroup;
        var dataStart = GroupStart(g) + perGroupMeta;
        if (nextBlock < dataStart) { nextBlock = dataStart; continue; }
        var block = nextBlock++;
        MarkBlockUsed(block);
        return block;
      }
      throw new InvalidOperationException(
        $"ext1 writer: the {totalBlocks}-block volume has no free block left to allocate.");
    }

    // Every group's metadata is in use from the outset. Bit N of a group's block
    // bitmap tracks block GroupStart(g) + N.
    for (var g = 0; g < groupCount; ++g)
      for (var b = GroupStart(g); b < GroupStart(g) + perGroupMeta && b < totalBlocks; ++b)
        MarkBlockUsed(b);

    // Inodes 1..(FirstUserInode-1) are reserved and count as in use.
    for (var ino = 1u; ino < FirstUserInode; ++ino)
      MarkInodeUsed(ino);

    // Allocate the file inodes first, then assemble the full ordered list of
    // root-directory entries so they can be laid out across as many data blocks
    // as needed.
    var nextInode = FirstUserInode;
    var fileInodes = new List<(uint Inode, FileEntry Entry)>(this._files.Count);
    var rootEntries = new List<(uint Inode, string Name)> {
      (2, "."),
      (2, ".."),
    };
    foreach (var entry in this._files) {
      var fileInode = nextInode++;
      MarkInodeUsed(fileInode);
      fileInodes.Add((fileInode, entry));
      rootEntries.Add((fileInode, entry.Name));
    }

    if (nextInode > (uint)inodesPerGroup * (uint)groupCount)
      throw new InvalidOperationException(
        $"ext1 writer: {nextInode - 1} inodes needed but the volume holds only {(long)inodesPerGroup * groupCount}.");

    // Lay the entries into one or more root data blocks. Records never straddle
    // a block boundary; the last record in each block has its rec_len padded to
    // the block end. "." / ".." stay the first two records of the first block.
    var rootBlockList = new List<int>();
    var entryIdx = 0;
    while (entryIdx < rootEntries.Count) {
      var blockData = new byte[blockSize];
      var pos = 0;
      var firstInBlock = entryIdx;
      while (entryIdx < rootEntries.Count) {
        var nameLen = Encoding.UTF8.GetByteCount(rootEntries[entryIdx].Name);
        var size = 8 + nameLen + 3 & ~3;
        if (pos + size > blockSize) break;
        pos += size;
        ++entryIdx;
      }
      if (entryIdx == firstInBlock)
        throw new InvalidOperationException(
          $"ext1 writer: directory entry '{rootEntries[firstInBlock].Name}' exceeds a single {blockSize}-byte block.");

      var writePos = 0;
      for (var e = firstInBlock; e < entryIdx; ++e) {
        var isLast = e == entryIdx - 1;
        writePos = WriteRev0DirEntry(blockData, writePos, rootEntries[e].Inode, rootEntries[e].Name, blockSize, isLast);
      }

      var blockNum = AllocBlock();
      blockData.CopyTo(img.Block(blockNum));
      rootBlockList.Add(blockNum);
    }

    // Past 12 direct blocks, a singly-indirect block chains the rest.
    var pointersPerBlock = blockSize / 4;
    var maxRootBlocks = MaxDirectBlocks + pointersPerBlock;
    if (rootBlockList.Count > maxRootBlocks)
      throw new InvalidOperationException(
        $"ext1 writer: root directory needs {rootBlockList.Count} blocks but only direct + singly-indirect " +
        $"blocks are supported (max {maxRootBlocks} blocks at {blockSize}-byte blocks).");

    var rootIndirectBlock = 0;
    var rootAllocatedBlocks = rootBlockList.Count;
    if (rootBlockList.Count > MaxDirectBlocks) {
      rootIndirectBlock = AllocBlock();
      ++rootAllocatedBlocks;
      var indOff = (long)rootIndirectBlock * blockSize;
      for (var p = MaxDirectBlocks; p < rootBlockList.Count; ++p)
        BinaryPrimitives.WriteUInt32LittleEndian(img.At(indOff + (p - MaxDirectBlocks) * 4, 4), (uint)rootBlockList[p]);
    }

    // --- File inodes + data blocks ---
    dataWrites = [];
    foreach (var (fileInode, entry) in fileInodes) {
      var fileInodeOffset = InodeOffset(fileInode);
      var blocksNeeded = entry.Size == 0 ? 0 : (int)((entry.Size + blockSize - 1) / blockSize);

      var fileBlocks = new List<int>(blocksNeeded);
      for (var b = 0; b < blocksNeeded; ++b)
        fileBlocks.Add(AllocBlock());
      if (blocksNeeded > 0)
        dataWrites.Add((fileBlocks, entry));

      var allocated = fileBlocks.Count;
      var ino = img.At(fileInodeOffset, InodeSize);
      BinaryPrimitives.WriteUInt16LittleEndian(ino, 0x8000 | 0x01A4);            // i_mode: regular, 0644
      BinaryPrimitives.WriteUInt32LittleEndian(ino[4..], (uint)entry.Size);      // i_size
      BinaryPrimitives.WriteUInt32LittleEndian(ino[8..], now);                   // i_atime
      BinaryPrimitives.WriteUInt32LittleEndian(ino[12..], now);                  // i_ctime
      BinaryPrimitives.WriteUInt32LittleEndian(ino[16..], now);                  // i_mtime
      BinaryPrimitives.WriteUInt16LittleEndian(ino[26..], 1);                    // i_links_count
      allocated += WriteInodeBlockMap(img, fileInodeOffset, fileBlocks, blockSize, AllocBlock);
      BinaryPrimitives.WriteUInt32LittleEndian(img.At(fileInodeOffset + 28, 4),
        (uint)(allocated * sectorsPerBlock));                                    // i_blocks, incl. pointer blocks
    }

    // --- Root inode (inode 2) ---
    var rootInodeOffset = InodeOffset(2);
    var rootIno = img.At(rootInodeOffset, InodeSize);
    BinaryPrimitives.WriteUInt16LittleEndian(rootIno, 0x4000 | 0x01ED);          // i_mode: dir, 0755
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[4..], (uint)(rootBlockList.Count * blockSize)); // i_size
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[8..], now);
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[12..], now);
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[16..], now);
    BinaryPrimitives.WriteUInt16LittleEndian(rootIno[26..], 2);                  // i_links_count: "." + parent
    BinaryPrimitives.WriteUInt32LittleEndian(rootIno[28..], (uint)(rootAllocatedBlocks * sectorsPerBlock)); // i_blocks
    var rootDirectCount = Math.Min(MaxDirectBlocks, rootBlockList.Count);
    for (var b = 0; b < rootDirectCount; ++b)
      BinaryPrimitives.WriteUInt32LittleEndian(rootIno[(40 + b * 4)..], (uint)rootBlockList[b]); // direct blocks 0..11
    if (rootIndirectBlock != 0)
      BinaryPrimitives.WriteUInt32LittleEndian(rootIno[88..], (uint)rootIndirectBlock); // singly-indirect pointer

    // --- Per-group bitmap padding, free counts and group descriptors ---
    var totalFreeBlocks = 0L;
    var totalFreeInodes = 0L;
    var gdtBase = (long)(GroupStart(0) + 1) * blockSize;
    for (var g = 0; g < groupCount; ++g) {
      var validBlocks = GroupBlocks(g);
      var blockBitmap = img.Block(BlockBitmapBlock(g));
      for (var bit = validBlocks; bit < blockSize * 8; ++bit)
        blockBitmap[bit / 8] |= (byte)(1 << (bit % 8));
      var usedBlocks = 0;
      for (var bit = 0; bit < validBlocks; ++bit)
        if ((blockBitmap[bit / 8] & (1 << (bit % 8))) != 0) ++usedBlocks;
      var freeBlocksInGroup = validBlocks - usedBlocks;
      totalFreeBlocks += freeBlocksInGroup;

      var inodeBitmap = img.Block(InodeBitmapBlock(g));
      for (var bit = inodesPerGroup; bit < blockSize * 8; ++bit)
        inodeBitmap[bit / 8] |= (byte)(1 << (bit % 8));
      var usedInodesInGroup = 0;
      for (var bit = 0; bit < inodesPerGroup; ++bit)
        if ((inodeBitmap[bit / 8] & (1 << (bit % 8))) != 0) ++usedInodesInGroup;
      totalFreeInodes += inodesPerGroup - usedInodesInGroup;

      var bgd = img.At(gdtBase + (long)g * 32, 32);
      BinaryPrimitives.WriteUInt32LittleEndian(bgd, (uint)BlockBitmapBlock(g));      // bg_block_bitmap
      BinaryPrimitives.WriteUInt32LittleEndian(bgd[4..], (uint)InodeBitmapBlock(g)); // bg_inode_bitmap
      BinaryPrimitives.WriteUInt32LittleEndian(bgd[8..], (uint)InodeTableBlock(g));  // bg_inode_table
      BinaryPrimitives.WriteUInt16LittleEndian(bgd[12..], (ushort)freeBlocksInGroup); // bg_free_blocks_count
      BinaryPrimitives.WriteUInt16LittleEndian(bgd[14..], (ushort)(inodesPerGroup - usedInodesInGroup)); // bg_free_inodes_count
      BinaryPrimitives.WriteUInt16LittleEndian(bgd[16..], (ushort)(g == 0 ? 1 : 0)); // bg_used_dirs_count (root only)
    }

    // --- Superblock at offset 1024 (rev-0 layout) ---
    var sb = img.At(1024, 1024);
    BinaryPrimitives.WriteUInt32LittleEndian(sb, (uint)((long)inodesPerGroup * groupCount)); // s_inodes_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[4..], (uint)totalBlocks);          // s_blocks_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[8..], 0);                          // s_r_blocks_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[12..], (uint)totalFreeBlocks);     // s_free_blocks_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[16..], (uint)totalFreeInodes);     // s_free_inodes_count
    BinaryPrimitives.WriteUInt32LittleEndian(sb[20..], (uint)firstDataBlock);      // s_first_data_block
    var logBlockSize = blockSize == 1024 ? 0u : blockSize == 2048 ? 1u : 2u;
    BinaryPrimitives.WriteUInt32LittleEndian(sb[24..], logBlockSize);              // s_log_block_size
    BinaryPrimitives.WriteUInt32LittleEndian(sb[28..], logBlockSize);              // s_log_frag_size
    BinaryPrimitives.WriteUInt32LittleEndian(sb[32..], (uint)blocksPerGroup);      // s_blocks_per_group
    BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], (uint)blocksPerGroup);      // s_frags_per_group
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

    // --- Superblock and group-descriptor backups ---
    // Every group past the first opens with a copy of the superblock at its own
    // first block, followed by a copy of the descriptor table. Nothing else can
    // recover a volume whose primary superblock is lost.
    if (groupCount > 1) {
      var primarySuperblock = sb.ToArray();
      for (var g = 1; g < groupCount; ++g) {
        var start = GroupStart(g);
        primarySuperblock.CopyTo(img.Block(start));
        BinaryPrimitives.WriteUInt16LittleEndian(img.At((long)start * blockSize + 90, 2), (ushort)g); // s_block_group_nr
        for (var d = 0; d < gdtBlocks; ++d)
          img.Block(GroupStart(0) + 1 + d).CopyTo(img.Block(start + 1 + d));
      }
    }

    return img;
  }

  /// <summary>
  /// Fills in a file's block map: direct pointers 0..11 at inode offset 40, then
  /// the single (88), double (92) and triple (96) indirect pointers. Returns the
  /// number of pointer blocks allocated, which count toward <c>i_blocks</c>.
  /// </summary>
  private static int WriteInodeBlockMap(SparseBlockImage img, long inodeOffset, IReadOnlyList<int> dataBlocks,
      int blockSize, Func<int> allocBlock) {
    const int directCount = 12;
    var ptrs = blockSize / 4;
    var meta = 0;

    for (var i = 0; i < directCount && i < dataBlocks.Count; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(img.At(inodeOffset + 40 + i * 4, 4), (uint)dataBlocks[i]);
    if (dataBlocks.Count <= directCount) return meta;

    int AllocPointerBlock() {
      var b = allocBlock();
      ++meta;
      return b;
    }

    // Single indirect: i_block[12] at inode offset 88.
    var ind = AllocPointerBlock();
    var indOff = (long)ind * blockSize;
    for (var i = 0; i < ptrs && directCount + i < dataBlocks.Count; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(img.At(indOff + i * 4, 4), (uint)dataBlocks[directCount + i]);
    BinaryPrimitives.WriteUInt32LittleEndian(img.At(inodeOffset + 88, 4), (uint)ind);

    var dindFirst = directCount + ptrs;
    if (dataBlocks.Count <= dindFirst) return meta;

    // Double indirect: i_block[13] at inode offset 92.
    var dind = AllocPointerBlock();
    var dindOff = (long)dind * blockSize;
    var dindCapacity = (long)ptrs * ptrs;
    var dindBlocks = (int)Math.Min(dataBlocks.Count - dindFirst, dindCapacity);
    var dindGroups = (dindBlocks + ptrs - 1) / ptrs;
    for (var k = 0; k < dindGroups; ++k) {
      var lvl2 = AllocPointerBlock();
      BinaryPrimitives.WriteUInt32LittleEndian(img.At(dindOff + k * 4, 4), (uint)lvl2);
      var lvl2Off = (long)lvl2 * blockSize;
      for (var i = 0; i < ptrs; ++i) {
        var idx = dindFirst + k * ptrs + i;
        if (idx >= dindFirst + dindBlocks) break;
        BinaryPrimitives.WriteUInt32LittleEndian(img.At(lvl2Off + i * 4, 4), (uint)dataBlocks[idx]);
      }
    }
    BinaryPrimitives.WriteUInt32LittleEndian(img.At(inodeOffset + 92, 4), (uint)dind);

    var tindFirst = dindFirst + (int)dindCapacity;
    if (dataBlocks.Count <= tindFirst) return meta;

    // Triple indirect: i_block[14] at inode offset 96.
    var tind = AllocPointerBlock();
    var tindOff = (long)tind * blockSize;
    var remaining = dataBlocks.Count - tindFirst;
    var lvl2Groups = (int)(((long)remaining + (long)ptrs * ptrs - 1) / ((long)ptrs * ptrs));
    for (var g = 0; g < lvl2Groups; ++g) {
      var mid = AllocPointerBlock();
      BinaryPrimitives.WriteUInt32LittleEndian(img.At(tindOff + g * 4, 4), (uint)mid);
      var midOff = (long)mid * blockSize;
      for (var k = 0; k < ptrs; ++k) {
        var groupStart = tindFirst + (g * ptrs + k) * ptrs;
        if (groupStart >= dataBlocks.Count) break;
        var leaf = AllocPointerBlock();
        BinaryPrimitives.WriteUInt32LittleEndian(img.At(midOff + k * 4, 4), (uint)leaf);
        var leafOff = (long)leaf * blockSize;
        for (var i = 0; i < ptrs; ++i) {
          var idx = groupStart + i;
          if (idx >= dataBlocks.Count) break;
          BinaryPrimitives.WriteUInt32LittleEndian(img.At(leafOff + i * 4, 4), (uint)dataBlocks[idx]);
        }
      }
    }
    BinaryPrimitives.WriteUInt32LittleEndian(img.At(inodeOffset + 96, 4), (uint)tind);
    return meta;
  }

  /// <summary>
  /// Materialises the image and writes it to the given stream. The optional
  /// <paramref name="blockSize"/> selects the 1024/2048/4096-byte block size
  /// (<c>s_log_block_size</c>); the total image is sized to a constant 4 MiB so
  /// larger blocks mean fewer total blocks.
  /// </summary>
  public void WriteTo(Stream output, int blockSize = 1024) {
    ArgumentNullException.ThrowIfNull(output);
    if (blockSize is not (1024 or 2048 or 4096))
      throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "ext1 block size must be 1024, 2048, or 4096.");
    // Keep the canonical 4 MiB footprint across block sizes: totalBlocks scales
    // inversely with the block size (4096 blocks at 1 KiB → 1024 blocks at 4 KiB).
    var totalBlocks = 4 * 1024 * 1024 / blockSize;
    var data = this.Build(blockSize, totalBlocks);
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

}
