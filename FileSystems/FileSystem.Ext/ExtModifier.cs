#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ext;

/// <summary>
/// In-place ext2/3/4 modifier — adapted from the ext1 in-flight modifier
/// for the rev-1 (DYNAMIC_REV) GOOD_OLD-without-FILETYPE-twist layout that
/// <see cref="ExtWriter"/> emits. Performs <b>O(touched bytes)</b> random-access I/O
/// against an ext2 image: only the superblock, the BGD entry, the block bitmap,
/// the inode bitmap, the affected inode slots, the root directory's data block,
/// and the file's own data blocks are read and written.
///
/// <para>Layout reminders (matching <see cref="ExtWriter"/>'s default geometry):
/// <list type="bullet">
///   <item>Block size 1024, single block group, direct block pointers only (max
///   12 blocks = 12 KiB per file at the 1 KiB default — same ceiling as the writer).</item>
///   <item>Superblock at file offset 1024, magic <c>0xEF53</c> at +56,
///   <c>s_inode_size</c> at +88 (ext2 honours 128 or 256), <c>s_first_ino</c>
///   at +84 (defaults to 11 on writer-emitted images).</item>
///   <item>BGD at block <c>(firstDataBlock + 1)</c>; bg_block_bitmap, bg_inode_bitmap
///   and bg_inode_table fields drive the actual on-disk offsets.</item>
///   <item>Root inode is inode 2; root dir is one direct block of rev-1 dirents
///   <c>(inode(4) | rec_len(2) | name_len(1) | file_type(1) | name[])</c>.</item>
///   <item>Inodes 1..(s_first_ino-1) reserved; user inodes start at <c>s_first_ino</c>
///   (typically 11).</item>
/// </list></para>
/// </summary>
public static class ExtModifier {

  // Constants mirror ExtWriter / ExtReader.
  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;
  private const ushort InodeModeRegular = 0x8000;
  private const ushort InodeModeDir = 0x4000;
  private const ushort DefaultMode = InodeModeRegular | 0x01A4; // 0644
  private const uint RootInode = 2;
  private const int MaxDirectBlocks = 12;
  private const byte FileTypeRegular = 1;

  // Cached superblock-derived geometry for a single Add/Remove call.
  private sealed record class Geometry(
    int BlockSize,
    uint FirstDataBlock,
    uint BlocksCount,
    uint InodesPerGroup,
    uint BlocksPerGroup,
    uint FirstUserInode,
    int InodeSize,
    long BgdOffset,
    long BlockBitmapOffset,
    long InodeBitmapOffset,
    long InodeTableOffset
  );

  // ── Rebuild-style API kept for callers that need atomic batch
  //    mutations (e.g. the WSL-validated mutate-then-fsck path). The
  //    in-flight AddFile/RemoveFile below is what the descriptor uses. ──

  /// <summary>
  /// Atomically applies the given mutations via read-then-rebuild: <paramref name="replacements"/>
  /// override matching entries by name, <paramref name="deletions"/> drop
  /// entries by name, and any remaining entries in <paramref name="replacements"/>
  /// that didn't match an existing name are added as new files. Use this when
  /// you need fsck-clean output with arbitrary indirect-block files; for tiny
  /// edits prefer <see cref="AddFile"/> / <see cref="RemoveFile"/>.
  /// </summary>
  public static void Mutate(
      Stream archive,
      IReadOnlyList<(string Name, byte[] Data)> replacements,
      IReadOnlyCollection<string> deletions) {
    archive.Position = 0;
    var reader = new ExtReader(archive);

    var delSet = new HashSet<string>(deletions, StringComparer.Ordinal);
    var replaceMap = replacements.ToDictionary(r => r.Name, r => r.Data, StringComparer.Ordinal);

    var final = new List<(string Name, byte[] Data)>();
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (delSet.Contains(entry.Name)) continue;
      if (replaceMap.TryGetValue(entry.Name, out var newData)) {
        final.Add((entry.Name, newData));
        replaceMap.Remove(entry.Name);
      } else {
        final.Add((entry.Name, reader.Extract(entry)));
      }
    }
    foreach (var (name, data) in replaceMap)
      final.Add((name, data));

    var w = new ExtWriter();
    foreach (var (name, data) in final)
      w.AddFile(name, data);
    var rebuilt = w.Build();
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  // ── In-flight API ────────────────────────────────────────────────────

  /// <summary>
  /// Adds (or fails if an entry of the same name already exists) a file inside an
  /// existing ext2/3/4 image. Touches only the superblock, BGD entry, block + inode
  /// bitmaps, the new inode slot, the root dir block, and the new data blocks.
  /// </summary>
  /// <exception cref="IOException">No free inode, no free blocks, no dir slot, or
  /// the file requires indirect blocks (>12 KiB at 1 KiB blocks).</exception>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (string.IsNullOrEmpty(name)) throw new ArgumentException("name is empty", nameof(name));

    var geom = ReadGeometry(image);
    var blocksNeeded = data.Length == 0 ? 0 : (data.Length + geom.BlockSize - 1) / geom.BlockSize;
    if (blocksNeeded > MaxDirectBlocks)
      throw new IOException(
        $"ext: file '{name}' needs {blocksNeeded} blocks; only direct pointers are supported "
        + $"(max {MaxDirectBlocks * geom.BlockSize} bytes).");

    // Locate root dir's first direct block — that's the dirent table.
    var rootInode = ReadInode(image, geom, RootInode);
    var rootDirBlock = BinaryPrimitives.ReadUInt32LittleEndian(rootInode.AsSpan(40, 4));
    if (rootDirBlock == 0)
      throw new IOException("ext: root directory has no data block.");

    // Read root dir block, ensure name is unique, find the last entry to split.
    var dirBlockBytes = ReadBlock(image, geom, (int)rootDirBlock);
    if (FindEntry(dirBlockBytes, name, out _, out _, out _))
      throw new IOException($"ext: entry '{name}' already exists; remove it first to replace.");

    // Compute room for a new dirent appended at end of dir block.
    var newEntrySize = ComputeDirEntrySize(name);
    if (!TrySplitLastEntryForAppend(dirBlockBytes, newEntrySize, out var insertOffset))
      throw new IOException(
        $"ext: root directory block has no room for entry '{name}' "
        + $"(only single-block root directories are supported).");

    // Allocate inode + data blocks via bitmaps.
    var blockBitmap = ReadBlockAt(image, geom.BlockBitmapOffset, geom.BlockSize);
    var inodeBitmap = ReadBlockAt(image, geom.InodeBitmapOffset, geom.BlockSize);

    var newInodeNum = AllocateInode(inodeBitmap, geom)
      ?? throw new IOException("ext: no free inodes available.");
    var allocatedBlocks = new List<uint>(blocksNeeded);
    for (var i = 0; i < blocksNeeded; ++i) {
      var b = AllocateBlock(blockBitmap, geom);
      if (b == null) {
        // Roll back any block allocations + inode allocation before throwing.
        foreach (var rb in allocatedBlocks) ClearBitmapBit(blockBitmap, (int)(rb - geom.FirstDataBlock));
        ClearBitmapBit(inodeBitmap, (int)(newInodeNum - 1));
        throw new IOException("ext: not enough free blocks for file.");
      }
      allocatedBlocks.Add(b.Value);
    }

    // Write file data blocks.
    var written = 0;
    foreach (var b in allocatedBlocks) {
      var toWrite = Math.Min(geom.BlockSize, data.Length - written);
      var blockBytes = new byte[geom.BlockSize];
      if (toWrite > 0) Array.Copy(data, written, blockBytes, 0, toWrite);
      WriteBlock(image, geom, (int)b, blockBytes);
      written += toWrite;
    }

    // Build + write the new inode. Inode size on rev-1 may be 128 or 256 —
    // we zero-fill the trailing extension area; only the GOOD_OLD layout
    // (first 128 bytes) carries semantic data we care about.
    var inodeBytes = new byte[geom.InodeSize];
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var sectorsPerBlock = geom.BlockSize / 512;
    BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(0, 2), DefaultMode);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(4, 4), (uint)data.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(8, 4), now);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(12, 4), now);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(16, 4), now);
    BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(26, 2), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(28, 4), (uint)(allocatedBlocks.Count * sectorsPerBlock));
    for (var i = 0; i < allocatedBlocks.Count; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(40 + i * 4, 4), allocatedBlocks[i]);
    WriteInode(image, geom, newInodeNum, inodeBytes);

    // Splice the new dirent into the root directory block at insertOffset.
    WriteRev1DirEntry(dirBlockBytes, insertOffset, newInodeNum, name, FileTypeRegular,
      isLast: true, blockEnd: dirBlockBytes.Length);
    WriteBlock(image, geom, (int)rootDirBlock, dirBlockBytes);

    // Persist bitmaps + free-count accounting.
    WriteBlockAt(image, geom.BlockBitmapOffset, blockBitmap);
    WriteBlockAt(image, geom.InodeBitmapOffset, inodeBitmap);
    AdjustFreeCounts(image, geom, freeBlocksDelta: -allocatedBlocks.Count, freeInodesDelta: -1);
  }

  /// <summary>
  /// Removes the named entry from an existing ext2/3/4 image. Touches only the
  /// metadata blocks plus (optionally) the data blocks of the removed file.
  /// Returns false if no entry with that name exists in the root directory.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var geom = ReadGeometry(image);
    var rootInode = ReadInode(image, geom, RootInode);
    var rootDirBlock = BinaryPrimitives.ReadUInt32LittleEndian(rootInode.AsSpan(40, 4));
    if (rootDirBlock == 0) return false;

    var dirBlockBytes = ReadBlock(image, geom, (int)rootDirBlock);
    if (!FindEntry(dirBlockBytes, name, out var entryOffset, out var prevOffset, out var inodeNum))
      return false;

    // Walk the inode's direct block pointers, free + (optionally) zero them.
    var inodeBytes = ReadInode(image, geom, inodeNum);
    var inodeMode = BinaryPrimitives.ReadUInt16LittleEndian(inodeBytes.AsSpan(0, 2));
    if ((inodeMode & InodeModeDir) != 0) return false; // refuse to remove directories.

    var blockBitmap = ReadBlockAt(image, geom.BlockBitmapOffset, geom.BlockSize);
    var inodeBitmap = ReadBlockAt(image, geom.InodeBitmapOffset, geom.BlockSize);

    var freedBlocks = 0;
    for (var i = 0; i < MaxDirectBlocks; ++i) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(inodeBytes.AsSpan(40 + i * 4, 4));
      if (ptr == 0) break;
      if (ptr < geom.FirstDataBlock || ptr >= geom.BlocksCount) continue; // sanity
      // Free in bitmap; (optionally) wipe contents.
      ClearBitmapBit(blockBitmap, (int)(ptr - geom.FirstDataBlock));
      if (wipeData) WriteBlock(image, geom, (int)ptr, new byte[geom.BlockSize]);
      ++freedBlocks;
    }

    // Refuse to follow indirect / double / triple pointers — writer never emits
    // them; clear the slots defensively in case a foreign image set them.

    // Free the inode in the inode bitmap and zero its slot in the inode table.
    ClearBitmapBit(inodeBitmap, (int)(inodeNum - 1));
    WriteInode(image, geom, inodeNum, new byte[geom.InodeSize]);

    // Splice dirent out: extend prev entry's rec_len to swallow this one
    // (or, if this entry is the first, just zero its inode field — readers
    // skip inode==0 entries).
    SpliceOutDirEntry(dirBlockBytes, entryOffset, prevOffset);
    WriteBlock(image, geom, (int)rootDirBlock, dirBlockBytes);

    WriteBlockAt(image, geom.BlockBitmapOffset, blockBitmap);
    WriteBlockAt(image, geom.InodeBitmapOffset, inodeBitmap);
    AdjustFreeCounts(image, geom, freeBlocksDelta: freedBlocks, freeInodesDelta: 1);
    return true;
  }

  // ── Geometry / superblock ──────────────────────────────────────────────

  private static Geometry ReadGeometry(Stream image) {
    var sb = new byte[264];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(56, 2));
    if (magic != ExtMagic)
      throw new InvalidDataException($"ext: invalid magic 0x{magic:X4}, expected 0xEF53.");

    var blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4, 4));
    var firstData = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(20, 4));
    var logBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(24, 4));
    var blockSize = 1024 << (int)logBlock;
    var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(32, 4));
    var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(40, 4));
    var revLevel = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(76, 4));
    // Rev-1+ extension fields: s_first_ino @ 84, s_inode_size @ 88.
    var firstUserInode = revLevel >= 1
      ? BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(84, 4))
      : 11u; // EXT2_GOOD_OLD_FIRST_INO
    var inodeSize = revLevel >= 1
      ? BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(88, 2))
      : (ushort)128; // EXT2_GOOD_OLD_INODE_SIZE
    if (inodeSize == 0) inodeSize = 128;
    if (firstUserInode == 0) firstUserInode = 11;

    // BGD block index = firstDataBlock + 1 (writer's single-group convention).
    var bgdOffset = (long)(firstData + 1) * blockSize;
    image.Position = bgdOffset;
    var bgd = new byte[12];
    image.ReadExactly(bgd);
    var blockBitmapBlock = BinaryPrimitives.ReadUInt32LittleEndian(bgd.AsSpan(0, 4));
    var inodeBitmapBlock = BinaryPrimitives.ReadUInt32LittleEndian(bgd.AsSpan(4, 4));
    var inodeTableBlock = BinaryPrimitives.ReadUInt32LittleEndian(bgd.AsSpan(8, 4));

    return new Geometry(
      BlockSize: blockSize,
      FirstDataBlock: firstData,
      BlocksCount: blocksCount,
      InodesPerGroup: inodesPerGroup,
      BlocksPerGroup: blocksPerGroup,
      FirstUserInode: firstUserInode,
      InodeSize: inodeSize,
      BgdOffset: bgdOffset,
      BlockBitmapOffset: (long)blockBitmapBlock * blockSize,
      InodeBitmapOffset: (long)inodeBitmapBlock * blockSize,
      InodeTableOffset: (long)inodeTableBlock * blockSize);
  }

  private static void AdjustFreeCounts(Stream image, Geometry geom, int freeBlocksDelta, int freeInodesDelta) {
    if (freeBlocksDelta == 0 && freeInodesDelta == 0) return;

    // Superblock free_blocks_count @ 12, free_inodes_count @ 16.
    var sbBuf = new byte[8];
    image.Position = SuperblockOffset + 12;
    image.ReadExactly(sbBuf);
    var freeBlocks = (int)BinaryPrimitives.ReadUInt32LittleEndian(sbBuf.AsSpan(0, 4));
    var freeInodes = (int)BinaryPrimitives.ReadUInt32LittleEndian(sbBuf.AsSpan(4, 4));
    freeBlocks += freeBlocksDelta;
    freeInodes += freeInodesDelta;
    if (freeBlocks < 0) freeBlocks = 0;
    if (freeInodes < 0) freeInodes = 0;
    BinaryPrimitives.WriteUInt32LittleEndian(sbBuf.AsSpan(0, 4), (uint)freeBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(sbBuf.AsSpan(4, 4), (uint)freeInodes);
    image.Position = SuperblockOffset + 12;
    image.Write(sbBuf);

    // BGD bg_free_blocks_count @ 12, bg_free_inodes_count @ 14 (16-bit each).
    var bgdBuf = new byte[4];
    image.Position = geom.BgdOffset + 12;
    image.ReadExactly(bgdBuf);
    var bgFreeBlocks = (int)BinaryPrimitives.ReadUInt16LittleEndian(bgdBuf.AsSpan(0, 2));
    var bgFreeInodes = (int)BinaryPrimitives.ReadUInt16LittleEndian(bgdBuf.AsSpan(2, 2));
    bgFreeBlocks = Math.Clamp(bgFreeBlocks + freeBlocksDelta, 0, ushort.MaxValue);
    bgFreeInodes = Math.Clamp(bgFreeInodes + freeInodesDelta, 0, ushort.MaxValue);
    BinaryPrimitives.WriteUInt16LittleEndian(bgdBuf.AsSpan(0, 2), (ushort)bgFreeBlocks);
    BinaryPrimitives.WriteUInt16LittleEndian(bgdBuf.AsSpan(2, 2), (ushort)bgFreeInodes);
    image.Position = geom.BgdOffset + 12;
    image.Write(bgdBuf);
  }

  // ── Block / inode IO ──────────────────────────────────────────────────

  private static byte[] ReadBlock(Stream image, Geometry geom, int blockNum) {
    var buf = new byte[geom.BlockSize];
    image.Position = (long)blockNum * geom.BlockSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteBlock(Stream image, Geometry geom, int blockNum, ReadOnlySpan<byte> data) {
    if (data.Length != geom.BlockSize)
      throw new ArgumentException("block payload size mismatch", nameof(data));
    image.Position = (long)blockNum * geom.BlockSize;
    image.Write(data);
  }

  private static byte[] ReadBlockAt(Stream image, long byteOffset, int blockSize) {
    var buf = new byte[blockSize];
    image.Position = byteOffset;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteBlockAt(Stream image, long byteOffset, ReadOnlySpan<byte> data) {
    image.Position = byteOffset;
    image.Write(data);
  }

  private static byte[] ReadInode(Stream image, Geometry geom, uint inodeNum) {
    if (inodeNum == 0) throw new ArgumentOutOfRangeException(nameof(inodeNum));
    var buf = new byte[geom.InodeSize];
    image.Position = geom.InodeTableOffset + (long)(inodeNum - 1) * geom.InodeSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteInode(Stream image, Geometry geom, uint inodeNum, ReadOnlySpan<byte> data) {
    if (data.Length != geom.InodeSize)
      throw new ArgumentException("inode size mismatch", nameof(data));
    image.Position = geom.InodeTableOffset + (long)(inodeNum - 1) * geom.InodeSize;
    image.Write(data);
  }

  // ── Bitmap helpers ────────────────────────────────────────────────────

  private static bool TestBitmapBit(byte[] bitmap, int bit) =>
    (bitmap[bit / 8] & (1 << (bit % 8))) != 0;

  private static void SetBitmapBit(byte[] bitmap, int bit) =>
    bitmap[bit / 8] |= (byte)(1 << (bit % 8));

  private static void ClearBitmapBit(byte[] bitmap, int bit) =>
    bitmap[bit / 8] &= (byte)~(1 << (bit % 8));

  /// <summary>Allocates the first free block in the block bitmap. Bit N tracks block (firstDataBlock+N).</summary>
  private static uint? AllocateBlock(byte[] blockBitmap, Geometry geom) {
    var maxBit = (int)(geom.BlocksCount - geom.FirstDataBlock);
    for (var bit = 0; bit < maxBit; ++bit) {
      if (TestBitmapBit(blockBitmap, bit)) continue;
      SetBitmapBit(blockBitmap, bit);
      return geom.FirstDataBlock + (uint)bit;
    }
    return null;
  }

  /// <summary>Allocates the first free user inode (inode &gt;= s_first_ino). Bit N tracks inode (N+1).</summary>
  private static uint? AllocateInode(byte[] inodeBitmap, Geometry geom) {
    // Reserve inodes 1..(s_first_ino-1) per ext convention.
    var firstUserBit = (int)(geom.FirstUserInode - 1);
    var maxBit = (int)geom.InodesPerGroup;
    for (var bit = firstUserBit; bit < maxBit; ++bit) {
      if (TestBitmapBit(inodeBitmap, bit)) continue;
      SetBitmapBit(inodeBitmap, bit);
      return (uint)bit + 1;
    }
    return null;
  }

  // ── Directory helpers ─────────────────────────────────────────────────

  /// <summary>
  /// Walks the rev-1 dirent stream looking for an entry whose name matches.
  /// Returns the entry's offset and the previous entry's offset (-1 for the
  /// first entry) so callers can splice it out. Rev-1 layout is
  /// <c>inode(4) | rec_len(2) | name_len(1) | file_type(1) | name[]</c>.
  /// </summary>
  private static bool FindEntry(byte[] dirData, string name, out int entryOffset, out int prevOffset, out uint inodeNum) {
    entryOffset = -1; prevOffset = -1; inodeNum = 0;
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var off = 0;
    var prev = -1;
    while (off + 8 <= dirData.Length) {
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(off, 4));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 4, 2));
      var nameLen = dirData[off + 6];
      if (recLen == 0 || off + recLen > dirData.Length) return false;
      if (ino != 0 && nameLen == nameBytes.Length &&
          dirData.AsSpan(off + 8, nameLen).SequenceEqual(nameBytes)) {
        entryOffset = off;
        prevOffset = prev;
        inodeNum = ino;
        return true;
      }
      prev = off;
      off += recLen;
    }
    return false;
  }

  private static int ComputeDirEntrySize(string name) {
    var nameBytes = Encoding.UTF8.GetByteCount(name);
    return (8 + nameBytes + 3) & ~3;
  }

  /// <summary>
  /// Tries to shrink the last in-use dirent's <c>rec_len</c> to its minimum size
  /// so that the trailing slack (at least <paramref name="newEntrySize"/> bytes)
  /// becomes available for a new entry. Returns the offset where the new entry
  /// should be written.
  /// </summary>
  private static bool TrySplitLastEntryForAppend(byte[] dirData, int newEntrySize, out int appendOffset) {
    appendOffset = -1;
    var off = 0;
    var lastOff = -1;
    while (off + 8 <= dirData.Length) {
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 4, 2));
      if (recLen == 0 || off + recLen > dirData.Length) return false;
      lastOff = off;
      off += recLen;
      if (off >= dirData.Length) break;
    }
    if (lastOff < 0) return false;

    var lastNameLen = dirData[lastOff + 6];
    var lastMin = (8 + lastNameLen + 3) & ~3;
    var lastRecLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(lastOff + 4, 2));
    var slack = lastRecLen - lastMin;
    if (slack < newEntrySize) return false;

    // Shrink last entry to its minimum, redirect surplus into the new tail entry.
    BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(lastOff + 4, 2), (ushort)lastMin);
    appendOffset = lastOff + lastMin;
    return true;
  }

  /// <summary>
  /// Removes a dirent: extend the previous entry's <c>rec_len</c> to absorb this
  /// one (or, if first, zero the inode and keep its rec_len so the chain stays
  /// walkable but readers skip the slot).
  /// </summary>
  private static void SpliceOutDirEntry(byte[] dirData, int entryOffset, int prevOffset) {
    var thisRecLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(entryOffset + 4, 2));
    if (prevOffset >= 0) {
      var prevRecLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(prevOffset + 4, 2));
      var combined = prevRecLen + thisRecLen;
      if (combined > ushort.MaxValue) combined = ushort.MaxValue;
      BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(prevOffset + 4, 2), (ushort)combined);
      // Wipe the now-unreachable slot so old name bytes don't leak forensically.
      Array.Clear(dirData, entryOffset, thisRecLen);
    } else {
      // First slot: zero inode field; readers skip ino==0 entries.
      BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(entryOffset, 4), 0);
      // Wipe the name + name_len + file_type so its bytes don't leak.
      Array.Clear(dirData, entryOffset + 6, thisRecLen - 6);
    }
  }

  /// <summary>
  /// Writes a rev-1 (DYNAMIC_REV with FILETYPE) directory entry at <paramref name="pos"/>:
  /// <c>inode(4) | rec_len(2) | name_len(1) | file_type(1) | name[] | pad to 4-byte boundary</c>.
  /// When <paramref name="isLast"/> is true the rec_len is extended to cover the
  /// trailing slack up to <paramref name="blockEnd"/>.
  /// </summary>
  private static void WriteRev1DirEntry(byte[] dirData, int pos, uint inode, string name, byte fileType, bool isLast, int blockEnd) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var entrySize = (8 + nameBytes.Length + 3) & ~3;
    var recLen = isLast ? blockEnd - pos : entrySize;
    if (recLen < entrySize)
      throw new IOException("ext: not enough room for new dirent.");

    BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(pos, 4), inode);
    BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(pos + 4, 2), (ushort)recLen);
    dirData[pos + 6] = (byte)nameBytes.Length;
    dirData[pos + 7] = fileType;
    nameBytes.CopyTo(dirData, pos + 8);
    // Zero any padding bytes between name end and entrySize.
    for (var i = pos + 8 + nameBytes.Length; i < pos + entrySize && i < dirData.Length; ++i)
      dirData[i] = 0;
  }
}
