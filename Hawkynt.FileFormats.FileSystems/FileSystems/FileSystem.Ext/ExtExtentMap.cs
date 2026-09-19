#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;
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
/// <para>
/// The walk names what it recognises; the block/cluster bitmap then accounts
/// for everything it did not. Only a block the bitmap positively proves free
/// is reported free, so external xattr blocks, EA-inode payloads, extent-tree
/// and indirect-pointer blocks, orphan and quota metadata, and metadata a
/// later revision introduces stay reserved rather than becoming wipe targets.
/// </para>
/// </summary>
public static class ExtExtentMap {

  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;
  private const ushort InodeModeDir = 0x4000;
  private const uint ExtentsFlag = 0x80000;
  private const ushort ExtentMagic = 0xF30A;
  private const uint RootInode = 2;
  private const uint IncompatMetaBg = 0x0010;
  private const uint Incompat64Bit = 0x0080;
  private const uint RoCompatBigalloc = 0x0200;
  private const ushort BgBlockUninit = 0x0002;

  /// <summary>
  /// Returns the decoded layout completed against the allocation bitmap, so
  /// every byte of the image is either named, proven free, or reserved.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("ext extent enumeration requires a readable, seekable stream.", nameof(image));
    if (image.Length <= 0) return [];

    var decoded = EnumerateDecoded(image).ToList();
    if (decoded.Count == 0) return [];

    var position = image.Position;
    List<(long Offset, long Length)> free;
    try {
      using var cache = new SectorCache(image);

      // META_BG scatters the descriptor table, so the bitmaps cannot be located
      // by the contiguous rule below. Proving nothing free reserves the image.
      free = TryReadGeometry(cache, out var geometry) && geometry.DescriptorTableIsContiguous
        ? ReadProvenFreeRanges(cache, geometry)
        : [];
    } catch (InvalidDataException) {
      free = [];
    } catch (IOException) {
      free = [];
    } finally {
      image.Position = position;
    }

    return FilesystemAllocationMapCompleter.Complete(image.Length, decoded, free);
  }

  /// <summary>
  /// Single-pass walker. Parses superblock + BGD table; emits the metadata
  /// regions (SB, BGDT, block bitmap, inode bitmap, inode table) of every
  /// group as <see cref="DefragBlockKind.MetadataReserved"/> extents; walks
  /// the directory tree from inode 2 and emits one extent per contiguous
  /// data-block run per file.
  /// </summary>
  private static IEnumerable<DefragBlockInfo> EnumerateDecoded(Stream image) {
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
    // A 64BIT volume writes wider group descriptors and says how wide in the
    // superblock; stepping through the table 32 bytes at a time then lands in the
    // middle of the second one.
    var descriptorSize = ExtBlockGroupGeometry.DescriptorSize(sb);
    var bgdEntry = new byte[32];
    for (uint g = 0; g < groupCount; g++) {
      var bgOffset = bgdtOffset + (long)g * descriptorSize;
      if (bgOffset + 32 > image.Length) yield break;
      cache.Read(bgOffset, bgdEntry);
      bgBlockBitmap[g] = BinaryPrimitives.ReadUInt32LittleEndian(bgdEntry);
      bgInodeBitmap[g] = BinaryPrimitives.ReadUInt32LittleEndian(bgdEntry.AsSpan(4));
      bgInodeTable[g] = BinaryPrimitives.ReadUInt32LittleEndian(bgdEntry.AsSpan(8));
    }

    // The descriptor table is as many blocks as the group count needs, not one.
    var gdtBlocks = (int)(((long)groupCount * 32 + blockSize - 1) / blockSize);
    yield return new DefragBlockInfo(bgdtOffset, (long)gdtBlocks * blockSize,
      DefragBlockKind.MetadataReserved, FileName: "ext group descriptor table");

    // Without SPARSE_SUPER every group past the first opens with a superblock
    // and descriptor-table backup. They are not reachable from the directory
    // tree, so anything that treats unreported space as free would overwrite
    // exactly what e2fsck needs to repair the volume.
    for (uint g = 1; g < groupCount; g++) {
      var groupStart = (long)(firstDataBlock + g * blocksPerGroup) * blockSize;
      if (groupStart + (long)(1 + gdtBlocks) * blockSize > image.Length) break;
      yield return new DefragBlockInfo(groupStart, (long)(1 + gdtBlocks) * blockSize,
        DefragBlockKind.MetadataReserved, FileName: $"ext superblock backup (group {g})");
    }

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

    // The journal (inode 8 by default) hangs off the superblock rather than the
    // directory tree, so nothing above has accounted for its blocks.
    var featureCompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(92));
    if ((featureCompat & FeatureCompatHasJournal) != 0) {
      var journalInode = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(224));
      if (journalInode == 0) journalInode = 8;
      var journalSize = DirSizeFromInodeStream(cache, blockSize, inodeSize, inodesPerGroup, bgInodeTable, journalInode);
      foreach (var ext in EnumerateFileExtentsStream(cache, blockSize, inodeSize, featureIncompat,
                 inodesPerGroup, bgInodeTable, journalInode, journalSize, "ext journal")) {
        yield return ext with { Kind = DefragBlockKind.MetadataReserved };
      }
    }
  }

  /// <summary>EXT4_FEATURE_COMPAT_HAS_JOURNAL.</summary>
  private const uint FeatureCompatHasJournal = 0x0004;

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
    // The pointer blocks of the classic block map are part of the file's
    // footprint. Leaving them out marks them free, so a wipe zeroes the map and
    // a defrag relocates data on top of it -- either way the file past its
    // twelfth block is gone.
    for (var level = 1; level <= 3 && remaining > 0; ++level) {
      var ind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(84 + level * 4));
      if (ind == 0) continue;
      result.AddRange(coalesce.Flush());
      result.Add(new DefragBlockInfo((long)ind * blockSize, blockSize,
        DefragBlockKind.MetadataReserved, FileName: $"{name} (block map)"));
      result.AddRange(WalkIndirectMaterialisedStream(cache, blockSize, ind, coalesce, level, ref remaining, result));
    }
    result.AddRange(coalesce.Flush());
    return result;
  }

  /// <param name="pointerBlocks">Collects the pointer blocks met on the way down, which belong to the file as much as its data does.</param>
  private static List<DefragBlockInfo> WalkIndirectMaterialisedStream(SectorCache cache, int blockSize, uint blockNum,
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
        emitted.AddRange(WalkIndirectMaterialisedStream(cache, blockSize, ptr, coalesce, level - 1, ref local, pointerBlocks));
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

    /// <summary>The file these runs belong to.</summary>
    public string Name => this._name;

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

  private static bool TryReadGeometry(SectorCache cache, out Geometry geometry) {
    geometry = default;
    if (cache.Length < SuperblockOffset + 1024) return false;

    var sb = cache.Read(SuperblockOffset, 1024);
    if (BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(56)) != ExtMagic) return false;

    var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(24));
    if (logBlockSize > 6) return false;
    var blockSize = 1024 << checked((int)logBlockSize);
    if (blockSize is < 1024 or > 65536 || (blockSize & (blockSize - 1)) != 0) return false;

    var featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(96));
    var featureRoCompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(100));
    var blocksLow = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4));
    var blocksHigh = (featureIncompat & Incompat64Bit) != 0
      ? BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x150))
      : 0u;
    var blocksCount = blocksLow | (ulong)blocksHigh << 32;
    if (blocksCount == 0) return false;

    var firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(20));
    var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(32));
    var clustersPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(36));
    var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(40));
    if (blocksPerGroup == 0 || inodesPerGroup == 0 || blocksCount <= firstDataBlock) return false;

    var inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(88));
    if (inodeSize == 0) inodeSize = 128;
    if (inodeSize < 128 || inodeSize > blockSize || (inodeSize & 3) != 0) return false;

    var logClusterSize = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(28));
    if (logClusterSize < logBlockSize || logClusterSize - logBlockSize > 20) return false;
    var clusterBlocks = 1 << checked((int)(logClusterSize - logBlockSize));
    if ((featureRoCompat & RoCompatBigalloc) == 0) clusterBlocks = 1;

    var rawGroupCount = (blocksCount - firstDataBlock + blocksPerGroup - 1) / blocksPerGroup;
    if (rawGroupCount == 0 || rawGroupCount > int.MaxValue) return false;
    var groupCount = checked((int)rawGroupCount);
    var descriptorSize = ExtBlockGroupGeometry.DescriptorSize(sb);
    if (descriptorSize is < 32 or > 1024) return false;

    var declaredBytes = blocksCount > (ulong)(long.MaxValue / blockSize)
      ? long.MaxValue
      : checked((long)blocksCount * blockSize);
    if (declaredBytes > cache.Length) return false;

    var descriptorTableIsContiguous = (featureIncompat & IncompatMetaBg) == 0;
    var groups = new GroupInfo[groupCount];
    if (descriptorTableIsContiguous) {
      var bgdtBlock = (ulong)firstDataBlock + 1;
      var bgdtOffset = checked((long)bgdtBlock * blockSize);
      for (var group = 0; group < groupCount; ++group) {
        var descriptorOffset = bgdtOffset + (long)group * descriptorSize;
        if (descriptorOffset < 0 || descriptorOffset + descriptorSize > cache.Length) return false;
        var descriptor = cache.Read(descriptorOffset, descriptorSize);

        ulong blockBitmap = BinaryPrimitives.ReadUInt32LittleEndian(descriptor);
        ulong inodeTable = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(8));
        ulong freeUnits = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(12));
        if ((featureIncompat & Incompat64Bit) != 0 && descriptorSize >= 64) {
          blockBitmap |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(32)) << 32;
          inodeTable |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(40)) << 32;
          freeUnits |= (ulong)BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(44)) << 16;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(18));
        groups[group] = new GroupInfo(blockBitmap, inodeTable, freeUnits, flags);
      }
    }

    geometry = new Geometry(
      blockSize,
      blocksCount,
      firstDataBlock,
      blocksPerGroup,
      clustersPerGroup,
      clusterBlocks,
      inodesPerGroup,
      inodeSize,
      featureIncompat,
      featureRoCompat,
      descriptorSize,
      descriptorTableIsContiguous,
      groups);
    return true;
  }

  private static List<(long Offset, long Length)> ReadProvenFreeRanges(SectorCache cache, Geometry geometry) {
    var result = new List<(long Offset, long Length)>();

    for (var group = 0; group < geometry.Groups.Length; ++group) {
      var info = geometry.Groups[group];
      if ((info.Flags & BgBlockUninit) != 0) continue;
      if (info.BlockBitmap == 0 || info.BlockBitmap >= geometry.BlocksCount) continue;

      var bitmapOffset = checked((long)info.BlockBitmap * geometry.BlockSize);
      if (bitmapOffset < 0 || bitmapOffset + geometry.BlockSize > cache.Length) continue;
      var bitmap = cache.Read(bitmapOffset, geometry.BlockSize);

      var groupStart = (ulong)geometry.FirstDataBlock + (ulong)group * geometry.BlocksPerGroup;
      if (groupStart >= geometry.BlocksCount) break;
      var groupBlocks = Math.Min((ulong)geometry.BlocksPerGroup, geometry.BlocksCount - groupStart);
      var validUnits = (groupBlocks + (ulong)geometry.ClusterBlocks - 1) / (ulong)geometry.ClusterBlocks;
      if (geometry.ClustersPerGroup != 0 && validUnits > geometry.ClustersPerGroup) continue;
      if (validUnits > (ulong)bitmap.Length * 8) continue;

      ulong freeCount = 0;
      for (ulong unit = 0; unit < validUnits; ++unit)
        if ((bitmap[checked((int)(unit >> 3))] & (1 << (int)(unit & 7))) == 0)
          ++freeCount;

      // The descriptor count cross-check turns a stale/corrupt bitmap into an
      // all-allocated group rather than trusting it with destructive operations.
      if (freeCount != info.FreeUnits) continue;

      ulong runStart = 0;
      ulong runLength = 0;
      void Flush() {
        if (runLength == 0) return;
        var firstBlock = groupStart + runStart * (ulong)geometry.ClusterBlocks;
        var endBlock = Math.Min(
          groupStart + groupBlocks,
          firstBlock + runLength * (ulong)geometry.ClusterBlocks);
        if (endBlock > firstBlock) {
          var offset = checked((long)firstBlock * geometry.BlockSize);
          var length = checked((long)(endBlock - firstBlock) * geometry.BlockSize);
          result.Add((offset, length));
        }
        runLength = 0;
      }

      for (ulong unit = 0; unit < validUnits; ++unit) {
        var isFree = (bitmap[checked((int)(unit >> 3))] & (1 << (int)(unit & 7))) == 0;
        if (isFree) {
          if (runLength == 0) runStart = unit;
          ++runLength;
        } else {
          Flush();
        }
      }
      Flush();
    }

    return result;
  }

  private readonly record struct Geometry(
    int BlockSize,
    ulong BlocksCount,
    uint FirstDataBlock,
    uint BlocksPerGroup,
    uint ClustersPerGroup,
    int ClusterBlocks,
    uint InodesPerGroup,
    int InodeSize,
    uint FeatureIncompat,
    uint FeatureRoCompat,
    int DescriptorSize,
    bool DescriptorTableIsContiguous,
    GroupInfo[] Groups);

  private readonly record struct GroupInfo(
    ulong BlockBitmap,
    ulong InodeTable,
    ulong FreeUnits,
    ushort Flags);
}
