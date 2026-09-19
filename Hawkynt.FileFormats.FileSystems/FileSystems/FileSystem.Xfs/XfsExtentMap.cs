#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Xfs;

/// <summary>
/// Walks an XFS image and yields its actual on-disk byte layout. Targets the
/// WORM writer profile: per-AG superblock + AGF + AGI + AGFL + bnobt/cntbt/
/// inobt headers as MetadataReserved, plus per-file extents (BMBT_REC packed
/// 128-bit format) as Used runs. For inodes whose data fork is in
/// <c>local</c> (inline) format, the file content lives inside the inode
/// itself and surfaces as MetadataReserved.
/// <para>
/// Streaming: never loads the whole image. All reads flow through a
/// <see cref="SectorCache"/> so multi-TB XFS images (an XFS volume can span
/// thousands of AGs) work without OOM.
/// </para>
/// </summary>
public static class XfsExtentMap {

  private const uint XfsMagic = 0x58465342; // "XFSB"
  private const ushort InodeMagic = 0x494E;  // "IN"
  private const uint XfsFeatIncompatFtype = 0x1;
  private const uint AgfMagic = 0x58414746; // XAGF
  private const uint BnobtV4Magic = 0x41425442; // ABTB
  private const uint BnobtV5Magic = 0x41423342; // AB3B
  private const uint CntbtV4Magic = 0x41425443; // ABTC
  private const uint CntbtV5Magic = 0x41423343; // AB3C
  private const byte FormatExtents = 2;

  /// <summary>
  /// Where each inode chunk of an allocation group sits, as its inode btree
  /// records them.
  /// </summary>
  private static IEnumerable<(long Offset, long Length)> InodeChunks(SectorCache cache,
      uint agNumber, long agBlocks, int blockSize, int inodeSize) {
    // The inode btree root is the AG's fourth block, and its leaf records name
    // a chunk of 64 inodes apiece.
    const int InobtBlock = 3;
    const int RecordOffset = 56;
    const int InodesPerChunk = 64;

    var rootOffset = ((long)agNumber * agBlocks + InobtBlock) * blockSize;
    if (rootOffset < 0 || rootOffset + blockSize > cache.Length) yield break;

    var root = cache.Read(rootOffset, blockSize);
    if (BinaryPrimitives.ReadUInt32BigEndian(root) != 0x49414233u) yield break;   // "IAB3"
    if (BinaryPrimitives.ReadUInt16BigEndian(root.AsSpan(4)) != 0) yield break;   // a leaf only

    var records = BinaryPrimitives.ReadUInt16BigEndian(root.AsSpan(6));
    var inodesPerBlock = blockSize / inodeSize;
    if (inodesPerBlock <= 0) yield break;

    for (var i = 0; i < records && i < 256; ++i) {
      var startIno = BinaryPrimitives.ReadUInt32BigEndian(root.AsSpan(RecordOffset + i * 16));
      var chunkBlock = startIno / (uint)inodesPerBlock;
      var offset = ((long)agNumber * agBlocks + chunkBlock) * blockSize;
      yield return (offset, (long)InodesPerChunk / inodesPerBlock * blockSize);
    }
  }

  /// <summary>
  /// Returns the decoded layout completed against the per-AG free-space btrees,
  /// so every byte of the image is either named, proven free, or reserved.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("XFS extent enumeration requires a readable, seekable stream.", nameof(image));
    if (image.Length <= 0) return [];

    var decoded = EnumerateDecoded(image).ToList();
    if (decoded.Count == 0) return [];

    var position = image.Position;
    List<(long Offset, long Length)> free;
    try {
      using var cache = new SectorCache(image);
      if (TryReadGeometry(cache, out var geometry)) {
        free = ReadProvenFreeRanges(cache, geometry);

        // The walk above only reaches the root directory's own children. The
        // reader knows the whole tree, so files nested deeper are named here
        // rather than left to the anonymous reserved fill.
        decoded.AddRange(ReadDecodedFileExtents(image, cache, geometry));
      } else
        free = [];
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
  /// Single-pass walker. Parses sb0, computes the AG layout, walks the root
  /// directory's inode (extents-format) to enumerate child files, then yields
  /// each child's BMBT extents as Used runs.
  /// </summary>
  private static IEnumerable<DefragBlockInfo> EnumerateDecoded(Stream image) {
    if (image.Length < 512) yield break;

    using var cache = new SectorCache(image);

    // Read just the 512-byte superblock via cache.
    var sb = cache.Read(0, 512);
    if (BinaryPrimitives.ReadUInt32BigEndian(sb) != XfsMagic) yield break;

    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(4));
    var rootIno = BinaryPrimitives.ReadUInt64BigEndian(sb.AsSpan(56));
    var agBlocks = BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(84));
    var agCount = BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(88));
    var versionNum = BinaryPrimitives.ReadUInt16BigEndian(sb.AsSpan(100));
    var inodeSize = BinaryPrimitives.ReadUInt16BigEndian(sb.AsSpan(104));
    var agBlkLog = sb[124];
    uint featuresIncompat = 0;
    if ((versionNum & 0xF) >= 5)
      featuresIncompat = BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(216));

    if (blockSize == 0) blockSize = 4096;
    if (inodeSize == 0) inodeSize = 256;
    if (agBlocks == 0) agBlocks = (uint)(image.Length / blockSize);
    if (agBlkLog == 0) {
      var v = agBlocks;
      while (v > 1) { agBlkLog++; v >>= 1; }
    }

    var hasFtype = (featuresIncompat & XfsFeatIncompatFtype) != 0;
    var forkOff = (versionNum & 0xF) >= 5 ? 176 : 100;

    // Per-AG metadata: SB(0) + AGF(1) + AGI(2) + AGFL(3) + bnobt(4) + cntbt(5) + inobt(6) + ...
    // We yield the first 8 blocks of each AG as a single MetadataReserved tile — covers
    // all the AG-level structures the WORM writer emits.
    for (uint a = 0; a < agCount; a++) {
      var agOff = (long)a * agBlocks * blockSize;
      var agMetaLen = Math.Min(8L * blockSize, agBlocks * (long)blockSize);
      if (agOff + agMetaLen > image.Length) agMetaLen = Math.Max(0, image.Length - agOff);
      if (agMetaLen > 0)
        yield return new DefragBlockInfo(agOff, agMetaLen, DefragBlockKind.MetadataReserved,
          FileName: $"XFS AG{a} metadata");
    }

    // The log is a region of its own, and the inode chunks sit past the AG's
    // header blocks. Neither was described here, so both read as free space —
    // a wipe would zero the log and every inode in the volume, and a layout
    // would put a file on top of them.
    var logStart = BinaryPrimitives.ReadUInt64BigEndian(sb.AsSpan(48));
    var logBlocks = BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(96));
    if (logBlocks > 0) {
      var logAg = (long)(logStart >> agBlkLog);
      var logAgBlock = (long)(logStart & ((1UL << agBlkLog) - 1));
      var logOffset = (logAg * agBlocks + logAgBlock) * blockSize;
      var logLength = (long)logBlocks * blockSize;
      if (logOffset >= 0 && logOffset + logLength <= image.Length)
        yield return new DefragBlockInfo(logOffset, logLength,
          DefragBlockKind.MetadataReserved, FileName: "XFS log");
    }

    // Every inode chunk the allocation btree records, which is where the
    // inodes themselves live.
    for (uint a = 0; a < agCount; a++) {
      foreach (var (chunkOffset, chunkLength) in InodeChunks(cache, a, agBlocks, (int)blockSize, (int)inodeSize)) {
        if (chunkOffset < 0 || chunkOffset + chunkLength > image.Length) continue;
        yield return new DefragBlockInfo(chunkOffset, chunkLength,
          DefragBlockKind.MetadataReserved, FileName: $"XFS AG{a} inode chunk");
      }
    }

    // Walk root directory's inode (short-form or extents).
    var rootOff = InodeOffset(rootIno, blockSize, inodeSize, agBlkLog, agBlocks);
    if (rootOff < 0 || rootOff + inodeSize > image.Length) yield break;
    var rootInode = cache.Read(rootOff, inodeSize);
    if (BinaryPrimitives.ReadUInt16BigEndian(rootInode) != InodeMagic) yield break;

    // Yield root inode itself as metadata.
    yield return new DefragBlockInfo(rootOff, inodeSize, DefragBlockKind.MetadataReserved,
      FileName: "XFS root inode");

    var rootMode = BinaryPrimitives.ReadUInt16BigEndian(rootInode.AsSpan(2));
    if ((rootMode & 0xF000) != 0x4000) yield break; // not directory

    var rootFormat = rootInode[5];
    var rootSize = (long)BinaryPrimitives.ReadUInt64BigEndian(rootInode.AsSpan(56));

    var children = new List<(ulong ino, string name)>();
    if (rootFormat == 1) {
      ReadShortFormDir(rootInode, forkOff,
        Math.Min((int)rootSize, inodeSize - forkOff), hasFtype, children);
    } else if (rootFormat == 2) {
      // Extents-format directory: read extent list, parse block-form entries.
      var nextents = BinaryPrimitives.ReadUInt32BigEndian(rootInode.AsSpan(76));
      if (nextents > 0 && nextents <= 100) {
        var dirBlkLog = sb[192];
        var dirFsBlocks = 1 << dirBlkLog;
        var blockShift = 0;
        while ((1u << blockShift) < blockSize) blockShift++;
        var leafFsBlockOffset = 1L << (35 - blockShift);

        var extOff = forkOff;
        for (uint e = 0; e < nextents; e++) {
          if (extOff + 16 > rootInode.Length) break;
          var hi = BinaryPrimitives.ReadUInt64BigEndian(rootInode.AsSpan(extOff));
          var lo = BinaryPrimitives.ReadUInt64BigEndian(rootInode.AsSpan(extOff + 8));
          extOff += 16;
          var blockCount = (int)(lo & 0x1FFFFF);
          var startBlock = ((hi & 0x1FF) << 43) | (lo >> 21);
          var startOff = (long)((hi >> 9) & 0x3FFFFFFFFFFFFFUL);
          var isData = startOff < leafFsBlockOffset;
          for (var b = 0; b < blockCount; b += dirFsBlocks) {
            var blockOff = (long)(startBlock + (ulong)b) * blockSize;
            if (blockOff + 8 > image.Length) continue;
            var dirBlockBytes = (long)dirFsBlocks * blockSize;
            // Yield directory blocks (data + leaf/free index) as metadata.
            yield return new DefragBlockInfo(blockOff, dirBlockBytes,
              DefragBlockKind.MetadataReserved, FileName: "XFS dir block");
            if (!isData) continue;
            var dirBlock = cache.Read(blockOff, (int)dirBlockBytes);
            ReadDirDataBlockEntries(dirBlock, (int)dirBlockBytes, hasFtype, children);
          }
        }
      }
    }

    // For each child file inode, yield its inode + data extents.
    foreach (var (ino, name) in children) {
      var off = InodeOffset(ino, blockSize, inodeSize, agBlkLog, agBlocks);
      if (off < 0 || off + inodeSize > image.Length) continue;
      var inode = cache.Read(off, inodeSize);
      if (BinaryPrimitives.ReadUInt16BigEndian(inode) != InodeMagic) continue;

      var mode = BinaryPrimitives.ReadUInt16BigEndian(inode.AsSpan(2));
      if ((mode & 0xF000) == 0x4000) continue; // skip subdirs (WORM writer is flat)

      yield return new DefragBlockInfo(off, inodeSize, DefragBlockKind.MetadataReserved,
        FileName: $"inode:{name}");

      var format = inode[5];
      var size = (long)BinaryPrimitives.ReadUInt64BigEndian(inode.AsSpan(56));

      if (format == 1) {
        // Local fork — file data inline inside the inode. Already covered by inode metadata above.
        continue;
      }
      if (format == 2) {
        var nextents = BinaryPrimitives.ReadUInt32BigEndian(inode.AsSpan(76));
        if (nextents == 0 || nextents > 100) continue;

        var extOff = forkOff;
        long? runStart = null;
        long runLen = 0;
        var bytesLeft = size;
        for (uint e = 0; e < nextents && bytesLeft > 0; e++) {
          if (extOff + 16 > inode.Length) break;
          var hi = BinaryPrimitives.ReadUInt64BigEndian(inode.AsSpan(extOff));
          var lo = BinaryPrimitives.ReadUInt64BigEndian(inode.AsSpan(extOff + 8));
          extOff += 16;
          var blockCount = (int)(lo & 0x1FFFFF);
          var startBlock = ((hi & 0x1FF) << 43) | (lo >> 21);

          var byteOff = (long)startBlock * blockSize;
          var byteLen = Math.Min((long)blockCount * blockSize, bytesLeft);
          bytesLeft -= byteLen;

          if (runStart is { } rs && rs + runLen == byteOff) {
            runLen += byteLen;
          } else {
            if (runStart is { } prev)
              yield return new DefragBlockInfo(prev, runLen, DefragBlockKind.Used, name);
            runStart = byteOff;
            runLen = byteLen;
          }
        }
        if (runStart is { } finalOff)
          yield return new DefragBlockInfo(finalOff, runLen, DefragBlockKind.Used, name);
      }
    }
  }

  private static long InodeOffset(ulong ino, uint blockSize, ushort inodeSize, byte agBlkLog,
      uint agBlocks) {
    var inoPerBlock = (int)(blockSize / inodeSize);
    var inoPbLog = 0;
    for (var v = inoPerBlock; v > 1; v >>= 1) inoPbLog++;
    var aginoLog = agBlkLog + inoPbLog;
    var agNo = (uint)(ino >> aginoLog);
    var agIno = ino & ((1UL << aginoLog) - 1);
    var block = agIno / (ulong)inoPerBlock;
    var offset = agIno % (ulong)inoPerBlock;
    return (long)((agNo * agBlocks + block) * blockSize + offset * inodeSize);
  }

  private static void ReadShortFormDir(byte[] data, int dataOff, int dataLen, bool hasFtype,
      List<(ulong, string)> children) {
    if (dataOff + 6 > data.Length) return;
    var count = data[dataOff];
    var i8count = data[dataOff + 1];
    var pos = dataOff + 6;
    if (i8count > 0) pos = dataOff + 10;

    for (var i = 0; i < count + i8count && pos + 3 < dataOff + dataLen; i++) {
      var nameLen = data[pos];
      if (nameLen == 0) break;
      if (pos + 3 + nameLen > data.Length) break;
      var name = Encoding.UTF8.GetString(data, pos + 3, nameLen);
      var ftypeLen = hasFtype ? 1 : 0;
      var inoPos = pos + 3 + nameLen + ftypeLen;
      ulong childIno;
      if (i < count && i8count == 0) {
        if (inoPos + 4 > data.Length) break;
        childIno = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(inoPos));
        pos = inoPos + 4;
      } else {
        if (inoPos + 8 > data.Length) break;
        childIno = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(inoPos));
        pos = inoPos + 8;
      }
      children.Add((childIno, name));
    }
  }

  private static void ReadDirDataBlockEntries(byte[] data, int blockLen, bool hasFtype,
      List<(ulong, string)> children) {
    var pos = 0;
    var end = blockLen;
    if (pos + 4 > data.Length) return;
    var bMagic = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
    var isV3 = bMagic is 0x58444233 or 0x58444433; // XDB3 / XDD3
    var isV2 = bMagic is 0x58443242 or 0x58443244; // XD2B / XD2D
    if (!isV3 && !isV2) return;
    pos += isV3 ? 64 : 16;

    var ftypeLen = hasFtype ? 1 : 0;
    while (pos + 12 <= end && pos + 12 <= data.Length) {
      if (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)) == 0xFFFF) {
        var freeLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 2));
        if (freeLen < 8) break;
        pos += freeLen;
        continue;
      }
      var entIno = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos));
      var nameLen = data[pos + 8];
      if (nameLen == 0 || entIno == 0) { pos += 8; continue; }
      if (pos + 9 + nameLen + ftypeLen + 2 > data.Length) break;
      var name = Encoding.UTF8.GetString(data, pos + 9, nameLen);
      if (name != "." && name != "..") children.Add((entIno, name));
      var entLen = 8 + 1 + nameLen + ftypeLen + 2;
      entLen = (entLen + 7) & ~7;
      pos += entLen;
    }
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
