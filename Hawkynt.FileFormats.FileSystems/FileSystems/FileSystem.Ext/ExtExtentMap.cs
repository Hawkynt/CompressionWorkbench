#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileSystem.Ext;

/// <summary>
/// Enumerates a complete ext2/3/4 physical allocation map.
/// </summary>
/// <remarks>
/// <para>
/// The block/cluster bitmap is authoritative for allocation. Decoded regular-file
/// extents are overlaid as <see cref="DefragBlockKind.Used"/>; every other allocated
/// byte is <see cref="DefragBlockKind.MetadataReserved"/>. Consequently external
/// xattr blocks, EA-inode payloads, extent-tree/index blocks, indirect-pointer blocks,
/// journals, orphan metadata, directory blocks, quota files, and future metadata that
/// this walker does not understand can never appear as free space.
/// </para>
/// <para>
/// A group whose bitmap cannot be proven trustworthy is deliberately treated as fully
/// allocated. META_BG is currently handled the same way for the whole image because its
/// descriptor table is not contiguous. This follows <see cref="IFilesystemExtentMap"/>'s
/// fail-closed contract: unsupported metadata reduces optimisation opportunities instead
/// of creating a corruption path.
/// </para>
/// <para>
/// On-disk structure details follow the Linux ext4 documentation; no e2fsprogs/kernel
/// implementation code is copied.
/// </para>
/// </remarks>
public static class ExtExtentMap {
  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;
  private const uint IncompatMetaBg = 0x0010;
  private const uint IncompatExtents = 0x0040;
  private const uint Incompat64Bit = 0x0080;
  private const uint RoCompatBigalloc = 0x0200;
  private const ushort BgBlockUninit = 0x0002;
  private const uint ExtentsFlag = 0x00080000;
  private const ushort ExtentMagic = 0xF30A;

  /// <summary>
  /// Returns a gap-free physical map for valid supported ext geometry. Malformed
  /// images yield no extents; valid but unsupported allocation geometry is returned
  /// as one metadata-reserved range so maintenance fails closed.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("ext extent enumeration requires a readable, seekable stream.", nameof(image));
    if (image.Length <= 0) return [];

    using var cache = new SectorCache(image);
    if (!TryReadGeometry(cache, out var geometry)) return [];

    if (!geometry.DescriptorTableIsContiguous)
      return [new DefragBlockInfo(0, image.Length, DefragBlockKind.MetadataReserved,
        "ext allocation metadata (META_BG unsupported)", DefragBlockClass.Directory)];

    var free = ReadProvenFreeRanges(cache, geometry);
    var decoded = ReadDecodedFileExtents(image, cache, geometry);
    return FilesystemAllocationMapCompleter.Complete(image.Length, geometry.BlockSize, decoded, free);
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

  private static List<DefragBlockInfo> ReadDecodedFileExtents(
      Stream image,
      SectorCache cache,
      Geometry geometry) {
    var result = new List<DefragBlockInfo>();
    var original = image.Position;
    try {
      image.Position = 0;
      using var reader = new ExtReader(image, leaveOpen: true);
      var seenInodes = new HashSet<uint>();
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory || entry.IsSymlink || entry.Inode == 0 || !seenInodes.Add(entry.Inode)) continue;
        if (!TryDecodeFile(cache, geometry, entry.Inode, entry.Name, entry.Size, out var decoded)) continue;
        result.AddRange(decoded);
      }
    } catch (InvalidDataException) {
      // Allocation coverage remains complete: undecoded files simply stay reserved.
    } catch (NotSupportedException) {
      // Same fail-closed behaviour for reader profiles we do not yet decode.
    } finally {
      image.Position = original;
    }

    return result;
  }

  private static bool TryDecodeFile(
      SectorCache cache,
      Geometry geometry,
      uint inodeNumber,
      string name,
      long logicalSize,
      out List<DefragBlockInfo> extents) {
    extents = [];
    if (!TryReadInode(cache, geometry, inodeNumber, out var inode)) return false;

    var collector = new RunCollector(geometry.BlockSize, name);
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(32));
    var usesExtents = (flags & ExtentsFlag) != 0 && (geometry.FeatureIncompat & IncompatExtents) != 0;

    var ok = usesExtents
      ? TryDecodeExtentTree(cache, geometry, inode, collector)
      : TryDecodeClassicMap(cache, geometry, inode, logicalSize, collector);
    if (!ok) return false;

    extents.AddRange(collector.Finish());
    return true;
  }

  private static bool TryReadInode(SectorCache cache, Geometry geometry, uint inodeNumber, out byte[] inode) {
    inode = [];
    if (inodeNumber == 0 || geometry.InodesPerGroup == 0) return false;
    var group = (inodeNumber - 1) / geometry.InodesPerGroup;
    var index = (inodeNumber - 1) % geometry.InodesPerGroup;
    if (group >= (uint)geometry.Groups.Length) return false;
    var tableBlock = geometry.Groups[checked((int)group)].InodeTable;
    if (tableBlock == 0 || tableBlock >= geometry.BlocksCount) return false;

    var offset = checked((long)tableBlock * geometry.BlockSize + (long)index * geometry.InodeSize);
    if (offset < 0 || offset + geometry.InodeSize > cache.Length) return false;
    inode = cache.Read(offset, geometry.InodeSize);
    return true;
  }

  private static bool TryDecodeExtentTree(
      SectorCache cache,
      Geometry geometry,
      byte[] inode,
      RunCollector collector) {
    if (inode.Length < 100 || BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(40)) != ExtentMagic)
      return false;

    var root = inode.AsSpan(40, Math.Min(60, inode.Length - 40)).ToArray();
    return TryDecodeExtentNode(cache, geometry, root, collector, new HashSet<ulong>(), expectedDepth: null);
  }

  private static bool TryDecodeExtentNode(
      SectorCache cache,
      Geometry geometry,
      byte[] node,
      RunCollector collector,
      HashSet<ulong> visited,
      int? expectedDepth) {
    if (node.Length < 12 || BinaryPrimitives.ReadUInt16LittleEndian(node) != ExtentMagic) return false;
    var entries = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(2));
    var maximum = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(4));
    var depth = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(6));
    if (expectedDepth is { } expected && depth != expected) return false;
    if (depth > 8 || entries > maximum || 12L + entries * 12L > node.Length) return false;

    if (depth == 0) {
      for (var i = 0; i < entries; ++i) {
        var at = 12 + i * 12;
        var encodedLength = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(at + 4));
        var blockCount = encodedLength <= 0x8000 ? encodedLength : encodedLength - 0x8000;
        if (blockCount == 0) return false;
        var startHi = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(at + 6));
        var startLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(at + 8));
        var startBlock = (ulong)startLo | (ulong)startHi << 32;
        if (startBlock >= geometry.BlocksCount || startBlock + blockCount > geometry.BlocksCount) return false;
        collector.AddRun(startBlock, blockCount);
      }
      return true;
    }

    for (var i = 0; i < entries; ++i) {
      var at = 12 + i * 12;
      var childLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(at + 4));
      var childHi = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(at + 8));
      var childBlock = (ulong)childLo | (ulong)childHi << 32;
      if (childBlock == 0 || childBlock >= geometry.BlocksCount || !visited.Add(childBlock)) return false;
      var childOffset = checked((long)childBlock * geometry.BlockSize);
      if (childOffset < 0 || childOffset + geometry.BlockSize > cache.Length) return false;
      var child = cache.Read(childOffset, geometry.BlockSize);
      if (!TryDecodeExtentNode(cache, geometry, child, collector, visited, depth - 1)) return false;
    }
    return true;
  }

  private static bool TryDecodeClassicMap(
      SectorCache cache,
      Geometry geometry,
      byte[] inode,
      long logicalSize,
      RunCollector collector) {
    var blocksLeft = logicalSize <= 0 ? 0 : (logicalSize + geometry.BlockSize - 1) / geometry.BlockSize;
    for (var i = 0; i < 12 && blocksLeft > 0; ++i) {
      var block = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4));
      if (block != 0) {
        if (block >= geometry.BlocksCount) return false;
        collector.AddRun(block, 1);
      }
      --blocksLeft;
    }

    var pointersPerBlock = geometry.BlockSize / 4;
    for (var level = 1; level <= 3 && blocksLeft > 0; ++level) {
      var root = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(84 + level * 4));
      if (!TryDecodeIndirect(cache, geometry, root, level, pointersPerBlock, ref blocksLeft, collector,
            new HashSet<uint>()))
        return false;
    }

    return blocksLeft == 0;
  }

  private static bool TryDecodeIndirect(
      SectorCache cache,
      Geometry geometry,
      uint blockNumber,
      int level,
      int pointersPerBlock,
      ref long blocksLeft,
      RunCollector collector,
      HashSet<uint> visited) {
    if (blocksLeft <= 0) return true;
    var subtreeCapacity = PowSaturating(pointersPerBlock, level);
    if (blockNumber == 0) {
      blocksLeft -= Math.Min(blocksLeft, subtreeCapacity);
      return true;
    }
    if (blockNumber >= geometry.BlocksCount || !visited.Add(blockNumber)) return false;

    var offset = checked((long)blockNumber * geometry.BlockSize);
    if (offset < 0 || offset + geometry.BlockSize > cache.Length) return false;
    var block = cache.Read(offset, geometry.BlockSize);
    var childCapacity = PowSaturating(pointersPerBlock, level - 1);

    for (var i = 0; i < pointersPerBlock && blocksLeft > 0; ++i) {
      var pointer = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i * 4));
      if (level == 1) {
        if (pointer != 0) {
          if (pointer >= geometry.BlocksCount) return false;
          collector.AddRun(pointer, 1);
        }
        --blocksLeft;
        continue;
      }

      if (pointer == 0) {
        blocksLeft -= Math.Min(blocksLeft, childCapacity);
        continue;
      }
      if (!TryDecodeIndirect(cache, geometry, pointer, level - 1, pointersPerBlock,
            ref blocksLeft, collector, visited))
        return false;
    }

    return true;
  }

  private static long PowSaturating(int value, int exponent) {
    long result = 1;
    for (var i = 0; i < exponent; ++i) {
      if (value != 0 && result > long.MaxValue / value) return long.MaxValue;
      result *= value;
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

  private sealed class RunCollector {
    private readonly int _blockSize;
    private readonly string _name;
    private readonly List<DefragBlockInfo> _result = [];
    private ulong _start;
    private ulong _end;
    private bool _active;

    public RunCollector(int blockSize, string name) {
      _blockSize = blockSize;
      _name = name;
    }

    public void AddRun(ulong startBlock, ulong blockCount) {
      if (blockCount == 0) return;
      if (_active && startBlock == _end) {
        _end += blockCount;
        return;
      }
      Flush();
      _start = startBlock;
      _end = startBlock + blockCount;
      _active = true;
    }

    public IReadOnlyList<DefragBlockInfo> Finish() {
      Flush();
      return _result;
    }

    private void Flush() {
      if (!_active) return;
      _result.Add(new DefragBlockInfo(
        checked((long)_start * _blockSize),
        checked((long)(_end - _start) * _blockSize),
        DefragBlockKind.Used,
        _name));
      _active = false;
    }
  }
}
