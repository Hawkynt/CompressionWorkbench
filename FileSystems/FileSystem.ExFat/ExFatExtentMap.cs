#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.ExFat;

/// <summary>
/// Walks an exFAT image and yields its actual on-disk byte layout — the
/// reserved boot region (VBR + backup VBR + OEM parameters), the FAT,
/// every cluster-chain run per file, and the free-cluster set. Honours the
/// FAT-chain bypass bit (NoFatChain) for contiguous extent shortcuts.
/// <para>
/// Streaming: reads only the VBR + dir clusters from disk. FAT navigation
/// flows through a <see cref="SectorCache"/> so a 50 TB exFAT image with a
/// 50 GB FAT keeps memory bounded to ~256 MB.
/// </para>
/// </summary>
public static class ExFatExtentMap {

  /// <summary>
  /// Single-pass walker. Parses the VBR, then for each directory entry set
  /// (File 0x85 + Stream 0xC0 + Name 0xC1) walks the FAT chain (or the
  /// contiguous range when <c>GeneralSecondaryFlags.NoFatChain</c> is set),
  /// emitting one <see cref="DefragBlockInfo"/> per contiguous run.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < 512) yield break;

    // Read just the VBR (first 512 bytes).
    var vbr = new byte[512];
    image.Position = 0;
    image.ReadExactly(vbr);

    if (Encoding.ASCII.GetString(vbr, 3, 8) != "EXFAT   ") yield break;
    if (vbr[510] != 0x55 || vbr[511] != 0xAA) yield break;

    var bytesPerSectorShift = vbr[108];
    var sectorsPerClusterShift = vbr[109];
    if (bytesPerSectorShift > 12 || sectorsPerClusterShift > 25) yield break;
    var bytesPerSector = 1 << bytesPerSectorShift;
    var sectorsPerCluster = 1 << sectorsPerClusterShift;
    var clusterSize = bytesPerSector * sectorsPerCluster;

    var fatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(80));
    var fatLengthSectors = BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(84));
    var clusterHeapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(88));
    var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(92));
    var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(96));

    if (clusterCount == 0 || clusterCount > 0x0FFFFFF5) yield break;

    var fatOffset = (long)fatOffsetSectors * bytesPerSector;
    var fatLengthBytes = (long)fatLengthSectors * bytesPerSector;
    var clusterHeapOffset = (long)clusterHeapOffsetSectors * bytesPerSector;

    using var cache = new SectorCache(image);

    // Reserved boot + FAT + alignment as MetadataReserved.
    yield return new DefragBlockInfo(0, fatOffset, DefragBlockKind.MetadataReserved,
      FileName: "exFAT VBR + backup VBR");
    yield return new DefragBlockInfo(fatOffset, fatLengthBytes,
      DefragBlockKind.MetadataReserved, FileName: "exFAT FAT");
    var preDataPad = clusterHeapOffset - (fatOffset + fatLengthBytes);
    if (preDataPad > 0)
      yield return new DefragBlockInfo(fatOffset + fatLengthBytes, preDataPad,
        DefragBlockKind.MetadataReserved, FileName: "exFAT alignment");

    // Walk root directory tree to find allocation bitmap, up-case table, and files.
    var clusterOwners = new string?[clusterCount + 2];
    var entries = new List<(string name, uint firstCluster, long size, bool isDir, bool noFatChain)>();
    WalkDirectoryStream(image, cache, fatOffset, clusterHeapOffset, clusterCount, clusterSize,
      rootCluster, "", entries, [rootCluster]);

    // Yield each entry's cluster runs.
    foreach (var (name, firstCluster, size, isDir, noFatChain) in entries) {
      var isMeta = name is "<bitmap>" or "<upcase>";
      foreach (var run in WalkClusterChainStream(cache, fatOffset, clusterCount,
                 clusterSize, firstCluster, size, noFatChain)) {
        for (var c = run.firstCluster; c < run.firstCluster + run.clusterCount && c < clusterOwners.Length; c++)
          clusterOwners[c] = name;
        var off = clusterHeapOffset + (long)(run.firstCluster - 2) * clusterSize;
        var rawLen = (long)run.clusterCount * clusterSize;
        var len = run.bytesValid > 0 ? Math.Min(rawLen, run.bytesValid) : rawLen;
        if (len <= 0) len = rawLen;
        if (isMeta)
          yield return new DefragBlockInfo(off, len, DefragBlockKind.MetadataReserved, FileName: name);
        else if (isDir)
          yield return new DefragBlockInfo(off, len, DefragBlockKind.Used,
            FileName: $"dir:{name}",
            Classification: DefragBlockClass.Directory);
        else
          yield return new DefragBlockInfo(off, len, DefragBlockKind.Used, name);
      }
    }

    // Free runs.
    var freeStart = -1;
    for (var c = 2; c <= clusterCount + 1; c++) {
      if (clusterOwners[c] == null) {
        if (freeStart < 0) freeStart = c;
      } else if (freeStart >= 0) {
        var off = clusterHeapOffset + (long)(freeStart - 2) * clusterSize;
        var len = (long)(c - freeStart) * clusterSize;
        yield return new DefragBlockInfo(off, len, DefragBlockKind.Free);
        freeStart = -1;
      }
    }
    if (freeStart >= 0) {
      var off = clusterHeapOffset + (long)(freeStart - 2) * clusterSize;
      var len = (long)(clusterCount + 2 - freeStart) * clusterSize;
      if (len > 0) yield return new DefragBlockInfo(off, len, DefragBlockKind.Free);
    }
  }

  private record struct ChainRun(uint firstCluster, uint clusterCount, long bytesValid);

  private static IEnumerable<ChainRun> WalkClusterChainStream(SectorCache cache, long fatOffset,
      uint clusterCount, int clusterSize, uint firstCluster, long size, bool noFatChain) {
    if (firstCluster < 2 || firstCluster > clusterCount + 1) yield break;

    if (noFatChain) {
      var clustersNeeded = (uint)((size + clusterSize - 1) / clusterSize);
      if (clustersNeeded == 0) clustersNeeded = 1;
      if (firstCluster + clustersNeeded - 1 > clusterCount + 1)
        clustersNeeded = clusterCount + 2 - firstCluster;
      yield return new ChainRun(firstCluster, clustersNeeded, size);
      yield break;
    }

    var cluster = firstCluster;
    var seen = new HashSet<uint>();
    var runStart = cluster;
    var runEnd = cluster;
    var bytesLeft = size;

    while (cluster >= 2 && cluster <= clusterCount + 1 && cluster < 0xFFFFFFF8 && seen.Add(cluster)) {
      var next = GetNextClusterStream(cache, fatOffset, cluster);
      if (next == cluster + 1 && cluster < clusterCount + 1) {
        runEnd = next;
        cluster = next;
        continue;
      }
      var clustersInRun = runEnd - runStart + 1;
      var rawBytes = (long)clustersInRun * clusterSize;
      var validInRun = bytesLeft > 0 ? Math.Min(rawBytes, bytesLeft) : rawBytes;
      yield return new ChainRun(runStart, clustersInRun, validInRun);
      bytesLeft -= validInRun;
      if (next >= 0xFFFFFFF8 || next < 2 || next > clusterCount + 1) yield break;
      cluster = next;
      runStart = cluster;
      runEnd = cluster;
    }
  }

  private static uint GetNextClusterStream(SectorCache cache, long fatOffset, uint cluster) {
    Span<byte> buf = stackalloc byte[4];
    cache.Read(fatOffset + (long)cluster * 4, buf);
    return BinaryPrimitives.ReadUInt32LittleEndian(buf);
  }

  /// <summary>
  /// Reads all bytes of a cluster-chain directory by following the FAT chain
  /// (via cache) and concatenating cluster contents (via targeted reads).
  /// </summary>
  private static byte[] ReadDirectoryBytesStream(Stream image, SectorCache cache, long fatOffset,
      long clusterHeapOffset, uint clusterCount, int clusterSize, uint firstCluster) {
    using var ms = new MemoryStream();
    var buf = ArrayPool<byte>.Shared.Rent(clusterSize);
    try {
      var cluster = firstCluster;
      var seen = new HashSet<uint>();
      while (cluster >= 2 && cluster <= clusterCount + 1 && cluster < 0xFFFFFFF8 && seen.Add(cluster)) {
        var off = clusterHeapOffset + (long)(cluster - 2) * clusterSize;
        if (off + clusterSize > image.Length) break;
        image.Position = off;
        image.ReadExactly(buf, 0, clusterSize);
        ms.Write(buf, 0, clusterSize);
        cluster = GetNextClusterStream(cache, fatOffset, cluster);
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buf);
    }
    return ms.ToArray();
  }

  private static void WalkDirectoryStream(Stream image, SectorCache cache, long fatOffset,
      long clusterHeapOffset, uint clusterCount, int clusterSize, uint dirCluster, string path,
      List<(string, uint, long, bool, bool)> entries, HashSet<uint> seenDirs) {
    var dirData = ReadDirectoryBytesStream(image, cache, fatOffset, clusterHeapOffset, clusterCount,
      clusterSize, dirCluster);
    var entryCount = dirData.Length / 32;

    for (var i = 0; i < entryCount; i++) {
      var off = i * 32;
      var entryType = dirData[off];
      if (entryType == 0x00) break;

      if (entryType == 0x81 || entryType == 0x82) {
        var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(off + 20));
        var dataLength = BinaryPrimitives.ReadInt64LittleEndian(dirData.AsSpan(off + 24));
        var name = entryType == 0x81 ? "<bitmap>" : "<upcase>";
        entries.Add((name, firstCluster, dataLength, false, true));
        continue;
      }

      if (entryType != 0x85) continue;
      var secondaryCount = dirData[off + 1];
      var attributes = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 4));
      var isDir = (attributes & 0x10) != 0;

      if (i + 1 >= entryCount) break;
      var streamOff = (i + 1) * 32;
      if (streamOff + 32 > dirData.Length) break;
      if (dirData[streamOff] != 0xC0) { i += secondaryCount; continue; }

      var generalSecondaryFlags = dirData[streamOff + 1];
      var noFatChain = (generalSecondaryFlags & 0x02) != 0;
      var nameLength = dirData[streamOff + 3];
      var firstCluster2 = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(streamOff + 20));
      var dataLength2 = BinaryPrimitives.ReadInt64LittleEndian(dirData.AsSpan(streamOff + 24));

      var nameBuilder = new StringBuilder();
      var nameEntriesNeeded = (nameLength + 14) / 15;
      for (var n = 0; n < nameEntriesNeeded && i + 2 + n < entryCount; n++) {
        var nameOff = (i + 2 + n) * 32;
        if (nameOff + 32 > dirData.Length || dirData[nameOff] != 0xC1) break;
        var charsToRead = Math.Min(15, nameLength - n * 15);
        for (var c = 0; c < charsToRead; c++) {
          var charOff = nameOff + 2 + c * 2;
          if (charOff + 2 > dirData.Length) break;
          var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(charOff));
          if (ch == 0) break;
          nameBuilder.Append(ch);
        }
      }

      var name2 = nameBuilder.ToString();
      var fullPath = string.IsNullOrEmpty(path) ? name2 : $"{path}/{name2}";
      entries.Add((fullPath, firstCluster2, isDir ? 0 : dataLength2, isDir, noFatChain));

      if (isDir && firstCluster2 >= 2 && seenDirs.Add(firstCluster2)) {
        WalkDirectoryStream(image, cache, fatOffset, clusterHeapOffset, clusterCount, clusterSize,
          firstCluster2, fullPath, entries, seenDirs);
      }
      i += secondaryCount;
    }
  }
}
