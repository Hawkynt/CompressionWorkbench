#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Xfs;

/// <summary>
/// Enumerates a complete XFS physical allocation map.
/// </summary>
/// <remarks>
/// <para>
/// Per-AG BNO/CNT free-space btrees are the allocation oracle. The two trees are
/// walked independently and must describe the same free extents and agree with
/// <c>agf_freeblks</c> before any range is accepted as free. Everything else is
/// allocated and therefore preserved.
/// </para>
/// <para>
/// Regular-file extents that can be decoded directly from the inode data fork are
/// surfaced as <see cref="DefragBlockKind.Used"/>. Allocated blocks that are not
/// positively identified remain <see cref="DefragBlockKind.MetadataReserved"/>;
/// this automatically covers attribute leaf/node btrees, remote xattr values,
/// directory btrees, inode-bmap trees, AG metadata, AGFL entries, log blocks,
/// reflink/rmap/refcount structures, and data-fork btrees that this walker does not
/// yet expose as movable file extents.
/// </para>
/// <para>
/// Both v4 ABTB/ABTC and v5 AB3B/AB3C allocation btrees are understood, including
/// multi-level trees. Any damaged or unsupported AG fails closed as entirely
/// allocated. Layout constants come from the published XFS on-disk specification;
/// no Linux implementation code is copied.
/// </para>
/// </remarks>
public static class XfsExtentMap {
  private const uint XfsMagic = 0x58465342; // XFSB
  private const uint AgfMagic = 0x58414746; // XAGF
  private const ushort InodeMagic = 0x494E; // IN
  private const uint BnobtV4Magic = 0x41425442; // ABTB
  private const uint BnobtV5Magic = 0x41423342; // AB3B
  private const uint CntbtV4Magic = 0x41425443; // ABTC
  private const uint CntbtV5Magic = 0x41423343; // AB3C
  private const byte FormatExtents = 2;

  /// <summary>
  /// Returns a gap-free allocation map for a valid XFS data device. If the
  /// superblock itself cannot be trusted, no extents are returned.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("XFS extent enumeration requires a readable, seekable stream.", nameof(image));
    if (image.Length <= 0) return [];

    using var cache = new SectorCache(image);
    if (!TryReadGeometry(cache, out var geometry)) return [];

    var free = ReadProvenFreeRanges(cache, geometry);
    var decoded = ReadDecodedFileExtents(image, cache, geometry);
    return FilesystemAllocationMapCompleter.Complete(image.Length, geometry.BlockSize, decoded, free);
  }

  private static bool TryReadGeometry(SectorCache cache, out Geometry geometry) {
    geometry = default;
    if (cache.Length < 512) return false;
    var sb = cache.Read(0, 512);
    if (BinaryPrimitives.ReadUInt32BigEndian(sb) != XfsMagic) return false;

    var blockSizeRaw = BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(4));
    if (blockSizeRaw is < 512 or > 65536 || blockSizeRaw > int.MaxValue) return false;
    var blockSize = checked((int)blockSizeRaw);
    if ((blockSize & blockSize - 1) != 0) return false;

    var dataBlocks = BinaryPrimitives.ReadUInt64BigEndian(sb.AsSpan(8));
    var agBlocks = BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(84));
    var agCount = BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(88));
    var version = BinaryPrimitives.ReadUInt16BigEndian(sb.AsSpan(100));
    var sectorSize = BinaryPrimitives.ReadUInt16BigEndian(sb.AsSpan(102));
    var inodeSize = BinaryPrimitives.ReadUInt16BigEndian(sb.AsSpan(104));
    var inoPbLog = sb[123];
    var agBlkLog = sb[124];

    if (dataBlocks == 0 || agBlocks == 0 || agCount == 0) return false;
    if (sectorSize == 0) sectorSize = 512;
    if (sectorSize < 512 || sectorSize > blockSize || (sectorSize & sectorSize - 1) != 0) return false;
    if (inodeSize == 0 || inodeSize > blockSize || blockSize % inodeSize != 0) return false;

    var expectedAgCount = (dataBlocks + agBlocks - 1) / agBlocks;
    if (expectedAgCount != agCount) return false;

    var declaredBytes = dataBlocks > (ulong)(long.MaxValue / blockSize)
      ? long.MaxValue
      : checked((long)dataBlocks * blockSize);
    if (declaredBytes > cache.Length) return false;

    if (inoPbLog == 0) {
      var inodesPerBlock = blockSize / inodeSize;
      while ((1 << inoPbLog) < inodesPerBlock) ++inoPbLog;
    }
    if (agBlkLog == 0) {
      ulong value = 1;
      while (value < agBlocks && agBlkLog < 63) {
        value <<= 1;
        ++agBlkLog;
      }
    }
    if (agBlkLog + inoPbLog >= 63) return false;

    geometry = new Geometry(
      blockSize,
      dataBlocks,
      agBlocks,
      agCount,
      sectorSize,
      inodeSize,
      inoPbLog,
      agBlkLog,
      IsV5: (version & 0xF) >= 5);
    return true;
  }

  private static List<(long Offset, long Length)> ReadProvenFreeRanges(SectorCache cache, Geometry geometry) {
    var result = new List<(long Offset, long Length)>();

    for (uint ag = 0; ag < geometry.AgCount; ++ag) {
      var agStart = (ulong)ag * geometry.AgBlocks;
      if (agStart >= geometry.DataBlocks) break;
      var agLength = checked((uint)Math.Min((ulong)geometry.AgBlocks, geometry.DataBlocks - agStart));
      if (!TryReadAgFreeRanges(cache, geometry, ag, agStart, agLength, out var ranges)) continue;

      foreach (var (start, count) in ranges) {
        var globalStart = agStart + start;
        result.Add((
          checked((long)globalStart * geometry.BlockSize),
          checked((long)count * geometry.BlockSize)));
      }
    }

    return result;
  }

  private static bool TryReadAgFreeRanges(
      SectorCache cache,
      Geometry geometry,
      uint agNumber,
      ulong agStart,
      uint agLength,
      out List<BlockRange> ranges) {
    ranges = [];
    var agfOffset = checked((long)agStart * geometry.BlockSize + geometry.SectorSize);
    if (agfOffset < 0 || agfOffset + geometry.SectorSize > cache.Length) return false;
    var agf = cache.Read(agfOffset, geometry.SectorSize);
    if (agf.Length < 64 || BinaryPrimitives.ReadUInt32BigEndian(agf) != AgfMagic) return false;
    if (BinaryPrimitives.ReadUInt32BigEndian(agf.AsSpan(8)) != agNumber) return false;

    var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(agf.AsSpan(12));
    if (declaredLength == 0 || declaredLength > geometry.AgBlocks || declaredLength != agLength) return false;

    var bnoRoot = BinaryPrimitives.ReadUInt32BigEndian(agf.AsSpan(16));
    var cntRoot = BinaryPrimitives.ReadUInt32BigEndian(agf.AsSpan(20));
    var bnoLevels = BinaryPrimitives.ReadUInt32BigEndian(agf.AsSpan(28));
    var cntLevels = BinaryPrimitives.ReadUInt32BigEndian(agf.AsSpan(32));
    var freeBlocks = BinaryPrimitives.ReadUInt32BigEndian(agf.AsSpan(52));
    if (bnoLevels is 0 or > 32 || cntLevels is 0 or > 32) return false;

    if (!TryReadAllocationTree(cache, geometry, agStart, agLength, bnoRoot,
          bnoLevels - 1, isCountTree: false, out var byBlock))
      return false;
    if (!TryReadAllocationTree(cache, geometry, agStart, agLength, cntRoot,
          cntLevels - 1, isCountTree: true, out var byCount))
      return false;

    var canonicalByBlock = byBlock.OrderBy(range => range.Start).ThenBy(range => range.Count).ToArray();
    var canonicalByCount = byCount.OrderBy(range => range.Start).ThenBy(range => range.Count).ToArray();
    if (!canonicalByBlock.SequenceEqual(canonicalByCount)) return false;

    ulong totalFree = 0;
    ulong previousEnd = 0;
    for (var i = 0; i < canonicalByBlock.Length; ++i) {
      var range = canonicalByBlock[i];
      if (range.Count == 0 || range.Start >= agLength || (ulong)range.Start + range.Count > agLength) return false;
      if (i > 0 && range.Start < previousEnd) return false;
      previousEnd = (ulong)range.Start + range.Count;
      totalFree += range.Count;
    }
    if (totalFree != freeBlocks) return false;

    ranges = canonicalByBlock.ToList();
    return true;
  }

  private static bool TryReadAllocationTree(
      SectorCache cache,
      Geometry geometry,
      ulong agStart,
      uint agLength,
      uint rootBlock,
      uint expectedRootLevel,
      bool isCountTree,
      out List<BlockRange> ranges) {
    ranges = [];
    if (rootBlock >= agLength) return false;
    var visited = new HashSet<uint>();
    return TryReadAllocationNode(
      cache,
      geometry,
      agStart,
      agLength,
      rootBlock,
      expectedRootLevel,
      isCountTree,
      visited,
      ranges);
  }

  private static bool TryReadAllocationNode(
      SectorCache cache,
      Geometry geometry,
      ulong agStart,
      uint agLength,
      uint blockNumber,
      uint expectedLevel,
      bool isCountTree,
      HashSet<uint> visited,
      List<BlockRange> ranges) {
    if (blockNumber >= agLength || !visited.Add(blockNumber)) return false;
    var offset = checked((long)(agStart + blockNumber) * geometry.BlockSize);
    if (offset < 0 || offset + geometry.BlockSize > cache.Length) return false;
    var block = cache.Read(offset, geometry.BlockSize);

    var expectedMagic = isCountTree
      ? geometry.IsV5 ? CntbtV5Magic : CntbtV4Magic
      : geometry.IsV5 ? BnobtV5Magic : BnobtV4Magic;
    if (BinaryPrimitives.ReadUInt32BigEndian(block) != expectedMagic) return false;

    var level = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(4));
    var records = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(6));
    if (level != expectedLevel) return false;

    var headerSize = geometry.IsV5 ? 56 : 16;
    if (level == 0) {
      var maximum = (geometry.BlockSize - headerSize) / 8;
      if (records > maximum) return false;
      for (var i = 0; i < records; ++i) {
        var at = headerSize + i * 8;
        var start = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at));
        var count = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at + 4));
        if (count == 0 || start >= agLength || (ulong)start + count > agLength) return false;
        ranges.Add(new BlockRange(start, count));
      }
      return true;
    }

    var maxNodeRecords = (geometry.BlockSize - headerSize) / 12;
    if (records == 0 || records > maxNodeRecords) return false;
    var pointerBase = headerSize + maxNodeRecords * 8;
    if (pointerBase + records * 4 > block.Length) return false;

    for (var i = 0; i < records; ++i) {
      var pointer = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(pointerBase + i * 4));
      if (pointer == uint.MaxValue) return false;
      if (!TryReadAllocationNode(cache, geometry, agStart, agLength, pointer,
            level - 1u, isCountTree, visited, ranges))
        return false;
    }
    return true;
  }

  private static List<DefragBlockInfo> ReadDecodedFileExtents(
      Stream image,
      SectorCache cache,
      Geometry geometry) {
    var result = new List<DefragBlockInfo>();
    var original = image.Position;
    try {
      image.Position = 0;
      using var reader = new XfsReader(image, leaveOpen: true);
      var seen = new HashSet<ulong>();
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory || entry.InodeNumber <= 0) continue;
        var inodeNumber = checked((ulong)entry.InodeNumber);
        if (!seen.Add(inodeNumber)) continue;
        if (!TryDecodeExtentFormatFile(cache, geometry, inodeNumber, entry.Name, out var decoded)) continue;
        result.AddRange(decoded);
      }
    } catch (InvalidDataException) {
      // Allocation coverage remains complete; undecoded file blocks stay reserved.
    } catch (NotSupportedException) {
      // Same fail-closed behaviour for reader profiles outside the decoded subset.
    } finally {
      image.Position = original;
    }
    return result;
  }

  private static bool TryDecodeExtentFormatFile(
      SectorCache cache,
      Geometry geometry,
      ulong inodeNumber,
      string name,
      out List<DefragBlockInfo> extents) {
    extents = [];
    var inodeOffset = InodeOffset(inodeNumber, geometry);
    if (inodeOffset < 0 || inodeOffset + geometry.InodeSize > cache.Length) return false;
    var inode = cache.Read(inodeOffset, geometry.InodeSize);
    if (BinaryPrimitives.ReadUInt16BigEndian(inode) != InodeMagic) return false;
    if (inode[5] != FormatExtents) return false;

    var coreSize = inode[4] >= 3 ? 176 : 100;
    var nextents = BinaryPrimitives.ReadUInt32BigEndian(inode.AsSpan(76));
    if (nextents == 0) return true;
    if ((long)coreSize + (long)nextents * 16 > inode.Length) return false;

    var collector = new RunCollector(geometry.BlockSize, name);
    for (uint i = 0; i < nextents; ++i) {
      var at = coreSize + checked((int)i * 16);
      var hi = BinaryPrimitives.ReadUInt64BigEndian(inode.AsSpan(at));
      var lo = BinaryPrimitives.ReadUInt64BigEndian(inode.AsSpan(at + 8));
      var startBlock = ((hi & 0x1FFUL) << 43) | (lo >> 21);
      var blockCount = lo & 0x1FFFFFUL;
      if (blockCount == 0 || startBlock >= geometry.DataBlocks || startBlock + blockCount > geometry.DataBlocks)
        return false;
      collector.AddRun(startBlock, blockCount);
    }

    extents.AddRange(collector.Finish());
    return true;
  }

  private static long InodeOffset(ulong inodeNumber, Geometry geometry) {
    var aginoLog = geometry.AgBlkLog + geometry.InoPbLog;
    var agNumber = inodeNumber >> aginoLog;
    if (agNumber >= geometry.AgCount) return -1;
    var aginoMask = (1UL << aginoLog) - 1;
    var agInode = inodeNumber & aginoMask;
    var inodesPerBlock = 1UL << geometry.InoPbLog;
    var blockInAg = agInode / inodesPerBlock;
    var inodeInBlock = agInode % inodesPerBlock;
    if (blockInAg >= geometry.AgBlocks) return -1;

    var globalBlock = agNumber * geometry.AgBlocks + blockInAg;
    if (globalBlock >= geometry.DataBlocks) return -1;
    var offset = globalBlock * (ulong)geometry.BlockSize + inodeInBlock * geometry.InodeSize;
    return offset > long.MaxValue ? -1 : checked((long)offset);
  }

  private readonly record struct Geometry(
    int BlockSize,
    ulong DataBlocks,
    uint AgBlocks,
    uint AgCount,
    int SectorSize,
    ushort InodeSize,
    byte InoPbLog,
    byte AgBlkLog,
    bool IsV5);

  private readonly record struct BlockRange(uint Start, uint Count);

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
