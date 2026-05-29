#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Ext;

/// <summary>
/// Walks an ext2/3/4 image and yields its actual on-disk byte layout —
/// per-file extent runs (one <see cref="DefragBlockInfo"/> per contiguous
/// block range) plus metadata regions (superblock, group descriptors, block
/// + inode bitmaps, inode table). Used by the defragment window's block-map
/// preview.
/// <para>
/// Streaming: never loads the whole image. All reads flow through a
/// <see cref="SectorCache"/> so multi-TB ext4 images (a 50 TB volume's BGD
/// table + bitmaps are tens of MB) work without OOM.
/// </para>
/// </summary>
public static class ExtExtentMap {

  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;
  private const ushort InodeModeDir = 0x4000;
  private const uint ExtentsFlag = 0x80000;
  private const ushort ExtentMagic = 0xF30A;
  private const uint RootInode = 2;

  /// <summary>
  /// Single-pass walker. Parses superblock + BGD table; emits the metadata
  /// regions (SB, BGDT, block bitmap, inode bitmap, inode table) of every
  /// group as <see cref="DefragBlockKind.MetadataReserved"/> extents; walks
  /// the directory tree from inode 2 and emits one extent per contiguous
  /// data-block run per file.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < SuperblockOffset + 264) yield break;

    // Read just the superblock (1 KB at offset 1024).
    var sb = new byte[1024];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);

    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(56));
    if (magic != ExtMagic) yield break;

    var blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4));
    var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(24));
    var blockSize = 1024 << (int)logBlockSize;
    var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(32));
    var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(40));
    var inodeSize = (int)BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(88));
    if (inodeSize == 0) inodeSize = 128;
    var featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(96));
    var firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(20));
    if (blocksPerGroup == 0 || inodesPerGroup == 0) yield break;

    using var cache = new SectorCache(image);

    // 1 KiB superblock at byte offset 1024.
    yield return new DefragBlockInfo(SuperblockOffset, 1024, DefragBlockKind.MetadataReserved,
      FileName: "ext superblock");

    var bgdtBlock = firstDataBlock + 1;
    var bgdtOffset = (long)bgdtBlock * blockSize;
    var groupCount = (blocksCount + blocksPerGroup - 1) / blocksPerGroup;
    var bgInodeTable = new uint[groupCount];
    var bgBlockBitmap = new uint[groupCount];
    var bgInodeBitmap = new uint[groupCount];
    var bgdEntry = new byte[32];
    for (uint g = 0; g < groupCount; g++) {
      var bgOffset = bgdtOffset + g * 32;
      if (bgOffset + 32 > image.Length) yield break;
      cache.Read(bgOffset, bgdEntry);
      bgBlockBitmap[g] = BinaryPrimitives.ReadUInt32LittleEndian(bgdEntry);
      bgInodeBitmap[g] = BinaryPrimitives.ReadUInt32LittleEndian(bgdEntry.AsSpan(4));
      bgInodeTable[g] = BinaryPrimitives.ReadUInt32LittleEndian(bgdEntry.AsSpan(8));
    }

    yield return new DefragBlockInfo(bgdtOffset, blockSize, DefragBlockKind.MetadataReserved,
      FileName: "ext group descriptor table");

    var inodeTableBlocks = (int)((inodesPerGroup * (uint)inodeSize + (uint)blockSize - 1) / (uint)blockSize);
    for (uint g = 0; g < groupCount; g++) {
      yield return new DefragBlockInfo((long)bgBlockBitmap[g] * blockSize, blockSize,
        DefragBlockKind.MetadataReserved, FileName: $"ext block bitmap (group {g})");
      yield return new DefragBlockInfo((long)bgInodeBitmap[g] * blockSize, blockSize,
        DefragBlockKind.MetadataReserved, FileName: $"ext inode bitmap (group {g})");
      yield return new DefragBlockInfo((long)bgInodeTable[g] * blockSize,
        (long)inodeTableBlocks * blockSize, DefragBlockKind.MetadataReserved,
        FileName: $"ext inode table (group {g})");
    }

    // Walk the root directory tree.
    var files = new List<(uint inode, string name, long size)>();
    var directoryInodes = new List<(uint inode, string name)> { (RootInode, "/") };
    WalkDirStream(cache, blockSize, inodeSize, featureIncompat, inodesPerGroup, bgInodeTable,
      RootInode, "", files, directoryInodes, new HashSet<uint>());

    // Emit each directory's data blocks as MetadataReserved.
    foreach (var (inode, name) in directoryInodes) {
      var dirSize = DirSizeFromInodeStream(cache, blockSize, inodeSize, inodesPerGroup, bgInodeTable, inode);
      foreach (var ext in EnumerateFileExtentsStream(cache, blockSize, inodeSize, featureIncompat,
                 inodesPerGroup, bgInodeTable, inode, dirSize, name)) {
        yield return ext with { Kind = DefragBlockKind.MetadataReserved };
      }
    }

    // For each file, walk the extent tree / block pointers and emit
    // contiguous-run extents.
    foreach (var (inode, name, size) in files) {
      foreach (var ext in EnumerateFileExtentsStream(cache, blockSize, inodeSize, featureIncompat,
                 inodesPerGroup, bgInodeTable, inode, size, name)) {
        yield return ext;
      }
    }
  }

  private static long DirSizeFromInodeStream(SectorCache cache, int blockSize, int inodeSize,
      uint inodesPerGroup, uint[] bgInodeTable, uint inodeNum) {
    var inode = ReadInodeStream(cache, blockSize, inodeSize, inodesPerGroup, bgInodeTable, inodeNum);
    if (inode == null) return 0;
    return BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4));
  }

  private static byte[]? ReadInodeStream(SectorCache cache, int blockSize, int inodeSize,
      uint inodesPerGroup, uint[] bgInodeTable, uint inodeNum) {
    if (inodeNum == 0 || inodesPerGroup == 0) return null;
    var group = (inodeNum - 1) / inodesPerGroup;
    var index = (inodeNum - 1) % inodesPerGroup;
    if (group >= bgInodeTable.Length) return null;
    var tableBlock = bgInodeTable[group];
    var offset = (long)tableBlock * blockSize + (long)index * inodeSize;
    if (offset + inodeSize > cache.Length) return null;
    return cache.Read(offset, inodeSize);
  }

  private static byte[] ReadInodeDataStream(SectorCache cache, int blockSize, uint featureIncompat,
      byte[] inode) {
    var sizelow = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4));
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(32));
    var usesExtents = (flags & ExtentsFlag) != 0 && (featureIncompat & (1u << 6)) != 0;
    using var ms = new MemoryStream();
    if (usesExtents) ReadExtentTreeStream(cache, blockSize, inode, sizelow, ms);
    else ReadBlockPointersStream(cache, blockSize, inode, sizelow, ms);
    return ms.ToArray();
  }

  private static void ReadBlockPointersStream(SectorCache cache, int blockSize, byte[] inode, uint size, MemoryStream ms) {
    var remaining = (long)size;
    for (var i = 0; i < 12 && remaining > 0; i++) {
      var blockNum = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4));
      if (blockNum == 0) break;
      var toRead = (int)Math.Min(remaining, blockSize);
      var off = (long)blockNum * blockSize;
      if (off + toRead > cache.Length) break;
      var block = cache.Read(off, toRead);
      ms.Write(block, 0, toRead);
      remaining -= toRead;
    }
    if (remaining > 0) {
      var ind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(88));
      if (ind != 0) ReadIndirectStream(cache, blockSize, ind, ms, ref remaining, 1);
    }
    if (remaining > 0) {
      var ind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(92));
      if (ind != 0) ReadIndirectStream(cache, blockSize, ind, ms, ref remaining, 2);
    }
    if (remaining > 0) {
      var ind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(96));
      if (ind != 0) ReadIndirectStream(cache, blockSize, ind, ms, ref remaining, 3);
    }
  }

  private static void ReadIndirectStream(SectorCache cache, int blockSize, uint blockNum, MemoryStream ms,
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
        ReadIndirectStream(cache, blockSize, ptr, ms, ref remaining, level - 1);
    }
  }

  private static void ReadExtentTreeStream(SectorCache cache, int blockSize, byte[] inode, uint size, MemoryStream ms) {
    var remaining = (long)size;
    var ehMagic = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(40));
    if (ehMagic != ExtentMagic) return;
    var entries = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(42));
    var depth = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(46));
    ReadExtentNodeStream(cache, blockSize, inode.AsSpan(40, 60).ToArray(), 0, entries, depth, ms, ref remaining);
  }

  private static void ReadExtentNodeStream(SectorCache cache, int blockSize, byte[] node, int hdrOffset,
      int entries, int depth, MemoryStream ms, ref long remaining) {
    if (depth == 0) {
      for (var i = 0; i < entries && remaining > 0; i++) {
        var off = hdrOffset + 12 + i * 12;
        if (off + 12 > node.Length) break;
        var len = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 4));
        var startHi = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 6));
        var startLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off + 8));
        var startBlock = ((long)startHi << 32) | startLo;
        var actualLen = len & 0x7FFF;
        for (var b = 0; b < actualLen && remaining > 0; b++) {
          var blockOff = (startBlock + b) * blockSize;
          if (blockOff + blockSize > cache.Length) break;
          var toRead = (int)Math.Min(remaining, blockSize);
          var block = cache.Read(blockOff, toRead);
          ms.Write(block, 0, toRead);
          remaining -= toRead;
        }
      }
    } else {
      for (var i = 0; i < entries && remaining > 0; i++) {
        var off = hdrOffset + 12 + i * 12;
        if (off + 12 > node.Length) break;
        var leafLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off + 4));
        var leafHi = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 8));
        var leafBlock = ((long)leafHi << 32) | leafLo;
        var blockOff = leafBlock * blockSize;
        if (blockOff + blockSize > cache.Length) break;
        var child = cache.Read(blockOff, blockSize);
        if (BinaryPrimitives.ReadUInt16LittleEndian(child) != ExtentMagic) continue;
        var ce = BinaryPrimitives.ReadUInt16LittleEndian(child.AsSpan(2));
        var cd = BinaryPrimitives.ReadUInt16LittleEndian(child.AsSpan(6));
        ReadExtentNodeStream(cache, blockSize, child, 0, ce, cd, ms, ref remaining);
      }
    }
  }

  private static void WalkDirStream(SectorCache cache, int blockSize, int inodeSize, uint featureIncompat,
      uint inodesPerGroup, uint[] bgInodeTable,
      uint dirInode, string path, List<(uint, string, long)> files,
      List<(uint, string)> directoryInodes, HashSet<uint> seen) {
    if (!seen.Add(dirInode)) return;
    var inodeData = ReadInodeStream(cache, blockSize, inodeSize, inodesPerGroup, bgInodeTable, dirInode);
    if (inodeData == null) return;
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeData);
    if ((mode & InodeModeDir) == 0) return;
    var dirBytes = ReadInodeDataStream(cache, blockSize, featureIncompat, inodeData);

    var off = 0;
    while (off + 8 <= dirBytes.Length) {
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(off));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(off + 4));
      var nameLen = dirBytes[off + 6];
      if (recLen == 0) break;
      if (off + 8 + nameLen > dirBytes.Length) break;
      if (ino != 0 && nameLen > 0) {
        var name = Encoding.UTF8.GetString(dirBytes, off + 8, nameLen);
        if (name is not ("." or "..")) {
          var full = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
          var inoData = ReadInodeStream(cache, blockSize, inodeSize, inodesPerGroup, bgInodeTable, ino);
          if (inoData != null) {
            var m = BinaryPrimitives.ReadUInt16LittleEndian(inoData);
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(inoData.AsSpan(4));
            if ((m & InodeModeDir) != 0) {
              directoryInodes.Add((ino, full));
              WalkDirStream(cache, blockSize, inodeSize, featureIncompat, inodesPerGroup, bgInodeTable,
                ino, full, files, directoryInodes, seen);
            } else {
              files.Add((ino, full, size));
            }
          }
        }
      }
      off += recLen;
    }
  }

  /// <summary>
  /// Yields one <see cref="DefragBlockInfo"/> per contiguous block-pointer or
  /// extent run for the named file. Coalesces adjacent block numbers.
  /// </summary>
  private static List<DefragBlockInfo> EnumerateFileExtentsStream(SectorCache cache, int blockSize, int inodeSize,
      uint featureIncompat, uint inodesPerGroup, uint[] bgInodeTable,
      uint inodeNum, long size, string name) {
    var result = new List<DefragBlockInfo>();
    var inode = ReadInodeStream(cache, blockSize, inodeSize, inodesPerGroup, bgInodeTable, inodeNum);
    if (inode == null) return result;
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(32));
    var usesExtents = (flags & ExtentsFlag) != 0 && (featureIncompat & (1u << 6)) != 0;

    if (usesExtents) {
      result.AddRange(WalkExtentTreeStream(cache, blockSize, inode, size, name));
      return result;
    }

    var coalesce = new RunBuilder(blockSize, name);
    var remaining = size;
    for (var i = 0; i < 12 && remaining > 0; i++) {
      var bn = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4));
      if (bn == 0) break;
      result.AddRange(coalesce.Add(bn, Math.Min(remaining, blockSize)));
      remaining -= blockSize;
    }
    if (remaining > 0) {
      var ind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(88));
      if (ind != 0)
        result.AddRange(WalkIndirectMaterialisedStream(cache, blockSize, ind, coalesce, 1, ref remaining));
    }
    if (remaining > 0) {
      var ind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(92));
      if (ind != 0)
        result.AddRange(WalkIndirectMaterialisedStream(cache, blockSize, ind, coalesce, 2, ref remaining));
    }
    if (remaining > 0) {
      var ind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(96));
      if (ind != 0)
        result.AddRange(WalkIndirectMaterialisedStream(cache, blockSize, ind, coalesce, 3, ref remaining));
    }
    result.AddRange(coalesce.Flush());
    return result;
  }

  private static List<DefragBlockInfo> WalkIndirectMaterialisedStream(SectorCache cache, int blockSize, uint blockNum,
      RunBuilder coalesce, int level, ref long remaining) {
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
        emitted.AddRange(WalkIndirectMaterialisedStream(cache, blockSize, ptr, coalesce, level - 1, ref local));
      }
    }
    remaining = local;
    return emitted;
  }

  private static IEnumerable<DefragBlockInfo> WalkExtentTreeStream(SectorCache cache, int blockSize,
      byte[] inode, long size, string name) {
    var ehMagic = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(40));
    if (ehMagic != ExtentMagic) yield break;
    var entries = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(42));
    var depth = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(46));
    var node = inode.AsSpan(40, 60).ToArray();
    var remaining = size;
    foreach (var ext in WalkExtentNodeStream(cache, blockSize, node, 0, entries, depth, name, remaining))
      yield return ext;
  }

  private static IEnumerable<DefragBlockInfo> WalkExtentNodeStream(SectorCache cache, int blockSize, byte[] node,
      int hdrOff, int entries, int depth, string name, long fileSizeRemaining) {
    if (depth == 0) {
      for (var i = 0; i < entries; i++) {
        var off = hdrOff + 12 + i * 12;
        if (off + 12 > node.Length) break;
        var len = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 4));
        var startHi = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 6));
        var startLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off + 8));
        var startBlock = ((long)startHi << 32) | startLo;
        var actualLen = len & 0x7FFF;
        var byteLen = (long)actualLen * blockSize;
        yield return new DefragBlockInfo(startBlock * blockSize, byteLen,
          DefragBlockKind.Used, name);
      }
    } else {
      for (var i = 0; i < entries; i++) {
        var off = hdrOff + 12 + i * 12;
        if (off + 12 > node.Length) break;
        var leafLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(off + 4));
        var leafHi = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(off + 8));
        var leafBlock = ((long)leafHi << 32) | leafLo;
        var blockOff = leafBlock * blockSize;
        if (blockOff + blockSize > cache.Length) continue;
        var child = cache.Read(blockOff, blockSize);
        if (BinaryPrimitives.ReadUInt16LittleEndian(child) != ExtentMagic) continue;
        var ce = BinaryPrimitives.ReadUInt16LittleEndian(child.AsSpan(2));
        var cd = BinaryPrimitives.ReadUInt16LittleEndian(child.AsSpan(6));
        foreach (var ext in WalkExtentNodeStream(cache, blockSize, child, 0, ce, cd, name, fileSizeRemaining))
          yield return ext;
      }
    }
  }

  private sealed class RunBuilder {
    private readonly int _blockSize;
    private readonly string _name;
    private long _runStart = -1;
    private long _runEnd = -1;
    private long _runByteLen;

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
