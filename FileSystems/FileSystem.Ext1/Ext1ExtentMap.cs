#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Ext1;

/// <summary>
/// Walks an ext1 image and yields its actual on-disk byte layout — per-file
/// block-pointer runs plus metadata regions (superblock, BGD table, block +
/// inode bitmaps, inode table). ext1 is rev-0 only: 128-byte inodes, no
/// extents, 8-byte directory header with 16-bit name_len. Used by the
/// defragment window's block-map preview.
/// <para>
/// Streaming: never loads the whole image. All reads flow through a
/// <see cref="SectorCache"/> so multi-GB ext1 images work without OOM.
/// </para>
/// </summary>
public static class Ext1ExtentMap {

  private const int SuperblockOffset = 1024;
  private const ushort Ext1Magic = 0xEF51;
  private const ushort InodeModeDir = 0x4000;
  private const int InodeSize = 128;
  private const uint RootInode = 2;

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < SuperblockOffset + 264) yield break;

    // Read only the superblock (1 KB at offset 1024).
    var sbBuf = new byte[1024];
    image.Position = SuperblockOffset;
    image.ReadExactly(sbBuf);

    var sb = sbBuf.AsSpan();
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(56));
    if (magic != Ext1Magic) yield break;

    var blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(4));
    var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(24));
    var blockSize = 1024 << (int)logBlockSize;
    var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(32));
    var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(40));
    var firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(20));
    if (blocksPerGroup == 0 || inodesPerGroup == 0) yield break;

    using var cache = new SectorCache(image);

    yield return new DefragBlockInfo(SuperblockOffset, 1024, DefragBlockKind.MetadataReserved,
      FileName: "ext1 superblock");

    var bgdtBlock = firstDataBlock + 1;
    var bgdtOffset = (long)bgdtBlock * blockSize;
    var groupCount = blocksPerGroup == 0 ? 1u : (blocksCount + blocksPerGroup - 1) / blocksPerGroup;
    var bgInodeTable = new uint[groupCount];
    var bgBlockBitmap = new uint[groupCount];
    var bgInodeBitmap = new uint[groupCount];
    var bgdEntry = new byte[32];
    for (uint g = 0; g < groupCount; g++) {
      var bgOffset = bgdtOffset + g * 32;
      if (bgOffset + 32 > cache.Length) yield break;
      cache.Read(bgOffset, bgdEntry);
      bgBlockBitmap[g] = BinaryPrimitives.ReadUInt32LittleEndian(bgdEntry);
      bgInodeBitmap[g] = BinaryPrimitives.ReadUInt32LittleEndian(bgdEntry.AsSpan(4));
      bgInodeTable[g] = BinaryPrimitives.ReadUInt32LittleEndian(bgdEntry.AsSpan(8));
    }

    // The descriptor table is as many blocks as the group count needs, not one.
    var gdtBlocks = (int)(((long)groupCount * 32 + blockSize - 1) / blockSize);
    yield return new DefragBlockInfo(bgdtOffset, (long)gdtBlocks * blockSize,
      DefragBlockKind.MetadataReserved, FileName: "ext1 group descriptor table");

    // Every group past the first opens with a superblock and descriptor-table
    // backup. They are not reachable from the directory tree, so anything
    // treating unreported space as free would overwrite them.
    for (uint g = 1; g < groupCount; g++) {
      var groupStart = (long)(firstDataBlock + g * blocksPerGroup) * blockSize;
      if (groupStart + (long)(1 + gdtBlocks) * blockSize > cache.Length) break;
      yield return new DefragBlockInfo(groupStart, (long)(1 + gdtBlocks) * blockSize,
        DefragBlockKind.MetadataReserved, FileName: $"ext1 superblock backup (group {g})");
    }

    var inodeTableBlocks = (int)((inodesPerGroup * (uint)InodeSize + (uint)blockSize - 1) / (uint)blockSize);
    for (uint g = 0; g < groupCount; g++) {
      yield return new DefragBlockInfo((long)bgBlockBitmap[g] * blockSize, blockSize,
        DefragBlockKind.MetadataReserved, FileName: $"ext1 block bitmap (group {g})");
      yield return new DefragBlockInfo((long)bgInodeBitmap[g] * blockSize, blockSize,
        DefragBlockKind.MetadataReserved, FileName: $"ext1 inode bitmap (group {g})");
      yield return new DefragBlockInfo((long)bgInodeTable[g] * blockSize,
        (long)inodeTableBlocks * blockSize, DefragBlockKind.MetadataReserved,
        FileName: $"ext1 inode table (group {g})");
    }

    var files = new List<(uint inode, string name, long size)>();
    var dirs = new List<(uint inode, string name, long size)>();
    // The root directory inode itself holds the top-level directory block(s); seed it
    // so its data blocks are protected from the unused-space wiper.
    var rootInode = ReadInode(cache, blockSize, inodesPerGroup, bgInodeTable, RootInode);
    if (rootInode != null)
      dirs.Add((RootInode, "", (long)BinaryPrimitives.ReadUInt32LittleEndian(rootInode.AsSpan(4))));
    WalkDir(cache, blockSize, inodesPerGroup, bgInodeTable, RootInode, "", files, dirs, new HashSet<uint>());

    // Directory data blocks are filesystem structure, not free space — surface
    // them as MetadataReserved so callers (defrag preview, unused-space wiper)
    // never treat them as reclaimable.
    foreach (var (inode, name, size) in dirs) {
      foreach (var ext in EnumerateFileExtents(cache, blockSize, inodesPerGroup, bgInodeTable,
                 inode, size, name.Length == 0 ? "ext1 root directory" : $"ext1 directory: {name}"))
        yield return ext with { Kind = DefragBlockKind.MetadataReserved };
    }

    foreach (var (inode, name, size) in files) {
      foreach (var ext in EnumerateFileExtents(cache, blockSize, inodesPerGroup, bgInodeTable,
                 inode, size, name))
        yield return ext;
    }
  }

  private static byte[]? ReadInode(SectorCache cache, int blockSize, uint inodesPerGroup,
      uint[] bgInodeTable, uint inodeNum) {
    if (inodeNum == 0) return null;
    var group = (inodeNum - 1) / inodesPerGroup;
    var index = (inodeNum - 1) % inodesPerGroup;
    if (group >= bgInodeTable.Length) return null;
    var off = (long)bgInodeTable[group] * blockSize + (long)index * InodeSize;
    if (off + InodeSize > cache.Length) return null;
    return cache.Read(off, InodeSize);
  }

  private static byte[] ReadInodeData(SectorCache cache, int blockSize, byte[] inode) {
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4));
    using var ms = new MemoryStream();
    var remaining = (long)size;
    for (var i = 0; i < 12 && remaining > 0; i++) {
      var bn = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4));
      if (bn == 0) break;
      var off = (long)bn * blockSize;
      var toRead = (int)Math.Min(remaining, blockSize);
      if (off + toRead > cache.Length) break;
      var block = cache.Read(off, toRead);
      ms.Write(block, 0, toRead);
      remaining -= toRead;
    }
    if (remaining > 0) {
      var ind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(88));
      if (ind != 0) ReadIndirect(cache, blockSize, ind, ms, ref remaining, 1);
    }
    if (remaining > 0) {
      var ind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(92));
      if (ind != 0) ReadIndirect(cache, blockSize, ind, ms, ref remaining, 2);
    }
    if (remaining > 0) {
      var ind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(96));
      if (ind != 0) ReadIndirect(cache, blockSize, ind, ms, ref remaining, 3);
    }
    return ms.ToArray();
  }

  private static void ReadIndirect(SectorCache cache, int blockSize, uint blockNum, MemoryStream ms,
      ref long remaining, int level) {
    if (blockNum == 0 || remaining <= 0) return;
    var off = (long)blockNum * blockSize;
    if (off + blockSize > cache.Length) return;
    var indBlock = cache.Read(off, blockSize);
    var per = blockSize / 4;
    for (var i = 0; i < per && remaining > 0; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(indBlock.AsSpan(i * 4));
      if (ptr == 0) break;
      if (level == 1) {
        var toRead = (int)Math.Min(remaining, blockSize);
        var dataOff = (long)ptr * blockSize;
        if (dataOff + toRead > cache.Length) break;
        var block = cache.Read(dataOff, toRead);
        ms.Write(block, 0, toRead);
        remaining -= toRead;
      } else
        ReadIndirect(cache, blockSize, ptr, ms, ref remaining, level - 1);
    }
  }

  private static void WalkDir(SectorCache cache, int blockSize, uint inodesPerGroup,
      uint[] bgInodeTable, uint dirInode, string path,
      List<(uint, string, long)> files, List<(uint, string, long)> dirs, HashSet<uint> seen) {
    if (!seen.Add(dirInode)) return;
    var inode = ReadInode(cache, blockSize, inodesPerGroup, bgInodeTable, dirInode);
    if (inode == null) return;
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    if ((mode & InodeModeDir) == 0) return;
    var dirBytes = ReadInodeData(cache, blockSize, inode);

    var off = 0;
    while (off + 8 <= dirBytes.Length) {
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(off));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(off + 4));
      // rev-0: name_len is 16-bit (no file_type byte).
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(off + 6));
      if (recLen == 0) break;
      if (off + 8 + nameLen > dirBytes.Length) break;
      if (ino != 0 && nameLen > 0) {
        var name = Encoding.UTF8.GetString(dirBytes, off + 8, nameLen);
        if (name is not ("." or "..")) {
          var full = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
          var inoData = ReadInode(cache, blockSize, inodesPerGroup, bgInodeTable, ino);
          if (inoData != null) {
            var m = BinaryPrimitives.ReadUInt16LittleEndian(inoData);
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(inoData.AsSpan(4));
            if ((m & InodeModeDir) != 0) {
              dirs.Add((ino, full, size));
              WalkDir(cache, blockSize, inodesPerGroup, bgInodeTable, ino, full, files, dirs, seen);
            } else
              files.Add((ino, full, size));
          }
        }
      }
      off += recLen;
    }
  }

  private static List<DefragBlockInfo> EnumerateFileExtents(SectorCache cache, int blockSize,
      uint inodesPerGroup, uint[] bgInodeTable, uint inodeNum, long size, string name) {
    var result = new List<DefragBlockInfo>();
    var inode = ReadInode(cache, blockSize, inodesPerGroup, bgInodeTable, inodeNum);
    if (inode == null) return result;

    var coalesce = new RunBuilder(blockSize, name);
    var remaining = size;
    for (var i = 0; i < 12 && remaining > 0; i++) {
      var bn = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4));
      if (bn == 0) break;
      result.AddRange(coalesce.Add(bn, Math.Min(remaining, blockSize)));
      remaining -= blockSize;
    }
    // The pointer blocks of the block map belong to the file as much as its data
    // does. Leaving them out marks them free, so a wipe zeroes the map and a
    // defrag relocates data on top of it -- either way the file past its twelfth
    // block is gone.
    for (var level = 1; level <= 3 && remaining > 0; ++level) {
      var ind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(84 + level * 4));
      if (ind == 0) continue;
      result.AddRange(coalesce.Flush());
      result.Add(new DefragBlockInfo((long)ind * blockSize, blockSize,
        DefragBlockKind.MetadataReserved, FileName: $"{name} (block map)"));
      result.AddRange(WalkIndirect(cache, blockSize, ind, coalesce, level, ref remaining, result));
    }
    result.AddRange(coalesce.Flush());
    return result;
  }

  /// <param name="pointerBlocks">Collects the pointer blocks met on the way down.</param>
  private static List<DefragBlockInfo> WalkIndirect(SectorCache cache, int blockSize, uint blockNum,
      RunBuilder coalesce, int level, ref long remaining, List<DefragBlockInfo> pointerBlocks) {
    var emitted = new List<DefragBlockInfo>();
    if (blockNum == 0 || remaining <= 0) return emitted;
    var off = (long)blockNum * blockSize;
    if (off + blockSize > cache.Length) return emitted;
    var indBlock = cache.Read(off, blockSize);
    var per = blockSize / 4;
    var local = remaining;
    for (var i = 0; i < per && local > 0; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(indBlock.AsSpan(i * 4));
      if (ptr == 0) break;
      if (level == 1) {
        emitted.AddRange(coalesce.Add(ptr, Math.Min(local, blockSize)));
        local -= blockSize;
      } else {
        emitted.AddRange(coalesce.Flush());
        pointerBlocks.Add(new DefragBlockInfo((long)ptr * blockSize, blockSize,
          DefragBlockKind.MetadataReserved, FileName: coalesce.Name + " (block map)"));
        emitted.AddRange(WalkIndirect(cache, blockSize, ptr, coalesce, level - 1, ref local, pointerBlocks));
      }
    }
    remaining = local;
    return emitted;
  }

  private sealed class RunBuilder {
    private readonly int _blockSize;
    private readonly string _name;
    private long _runStart = -1;
    private long _runEnd = -1;
    private long _runByteLen;

    /// <summary>The file these runs belong to.</summary>
    public string Name => this._name;

    public RunBuilder(int blockSize, string name) {
      this._blockSize = blockSize;
      this._name = name;
    }

    public IEnumerable<DefragBlockInfo> Add(uint blockNum, long byteLenInThisBlock) {
      if (this._runStart < 0) {
        this._runStart = blockNum;
        this._runEnd = blockNum;
        this._runByteLen = byteLenInThisBlock;
        yield break;
      }
      if (blockNum == this._runEnd + 1) {
        this._runEnd = blockNum;
        this._runByteLen += byteLenInThisBlock;
        yield break;
      }
      yield return new DefragBlockInfo(this._runStart * this._blockSize, this._runByteLen,
        DefragBlockKind.Used, this._name);
      this._runStart = blockNum;
      this._runEnd = blockNum;
      this._runByteLen = byteLenInThisBlock;
    }

    public IEnumerable<DefragBlockInfo> Flush() {
      if (this._runStart < 0) yield break;
      yield return new DefragBlockInfo(this._runStart * this._blockSize, this._runByteLen,
        DefragBlockKind.Used, this._name);
      this._runStart = -1;
    }
  }
}
