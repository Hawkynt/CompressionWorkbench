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

  /// <summary>
  /// Single-pass walker. Parses sb0, computes the AG layout, walks the root
  /// directory's inode (extents-format) to enumerate child files, then yields
  /// each child's BMBT extents as Used runs.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
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
        var extOff = forkOff;
        for (uint e = 0; e < nextents; e++) {
          if (extOff + 16 > rootInode.Length) break;
          var hi = BinaryPrimitives.ReadUInt64BigEndian(rootInode.AsSpan(extOff));
          var lo = BinaryPrimitives.ReadUInt64BigEndian(rootInode.AsSpan(extOff + 8));
          extOff += 16;
          var blockCount = (int)(lo & 0x1FFFFF);
          var startBlock = ((hi & 0x1FF) << 43) | (lo >> 21);
          for (var b = 0; b < blockCount; b++) {
            var blockOff = (long)(startBlock + (ulong)b) * blockSize;
            if (blockOff + 8 > image.Length) continue;
            // Yield directory data blocks as metadata.
            yield return new DefragBlockInfo(blockOff, blockSize,
              DefragBlockKind.MetadataReserved, FileName: "XFS dir block");
            var dirBlock = cache.Read(blockOff, (int)blockSize);
            ReadBlockFormDirEntries(dirBlock, (int)blockSize, children);
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

  private static void ReadBlockFormDirEntries(byte[] data, int blockLen,
      List<(ulong, string)> children) {
    var pos = 0;
    var end = blockLen;
    if (pos + 4 <= data.Length) {
      var bMagic = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
      if (bMagic == 0x58443242 || bMagic == 0x58444233) pos += 48;
    }
    while (pos + 12 <= end && pos + 12 <= data.Length) {
      var entIno = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos));
      var nameLen = data[pos + 8];
      if (nameLen == 0 || entIno == 0) { pos += 12; continue; }
      if (pos + 11 + nameLen > data.Length) break;
      var name = Encoding.UTF8.GetString(data, pos + 9, nameLen);
      if (name != "." && name != "..") children.Add((entIno, name));
      var entLen = 8 + 1 + nameLen + 2;
      entLen = (entLen + 7) & ~7;
      pos += entLen;
    }
  }
}
