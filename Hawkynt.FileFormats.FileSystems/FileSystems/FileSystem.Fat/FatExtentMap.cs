#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Fat;

/// <summary>
/// Walks a FAT12/16/32 image and yields the actual on-disk byte layout —
/// reserved region (boot + FATs + root dir on FAT12/16), every cluster-chain
/// segment per file, and free clusters. Used by the defrag window to render
/// the real fragmented layout before defragmentation runs.
/// <para>
/// Streaming: only the BPB + the first FAT copy + (FAT12/16) fixed root dir
/// are kept in memory; subdirectory clusters are read from disk one at a time.
/// A 100 GB FAT32 image needs roughly 100 MB of RAM (the FAT itself) rather
/// than 100 GB.
/// </para>
/// </summary>
public static class FatExtentMap {

  /// <summary>
  /// Single-pass FAT walker. Parses the boot sector, then for each directory
  /// entry walks the cluster chain, emitting one <see cref="DefragBlockInfo"/>
  /// per contiguous run. Reserved region (boot sector, FAT1, FAT2, root dir
  /// on FAT12/16) becomes a single <see cref="DefragBlockKind.MetadataReserved"/>
  /// extent. Free clusters are emitted in chunks.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < 512) yield break;

    // Read BPB (first 512 bytes only — never load the whole image).
    var bpb = new byte[512];
    image.Position = 0;
    image.ReadExactly(bpb);

    // Reject obviously non-FAT data (boot-jump byte).
    if (bpb[0] != 0xEB && bpb[0] != 0xE9 && bpb[0] != 0x00) yield break;

    var bytesPerSector = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb.AsSpan(11));
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;
    var sectorsPerCluster = (int)bpb[13];
    if (sectorsPerCluster == 0) sectorsPerCluster = 1;
    var reservedSectors = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb.AsSpan(14));
    var fatCount = (int)bpb[16];
    if (fatCount == 0) fatCount = 2;
    var rootEntryCount = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb.AsSpan(17));
    var totalSectors = (long)BinaryPrimitives.ReadUInt16LittleEndian(bpb.AsSpan(19));
    if (totalSectors == 0)
      totalSectors = BinaryPrimitives.ReadUInt32LittleEndian(bpb.AsSpan(32));
    var fatSize = (long)BinaryPrimitives.ReadUInt16LittleEndian(bpb.AsSpan(22));
    if (fatSize == 0)
      fatSize = BinaryPrimitives.ReadUInt32LittleEndian(bpb.AsSpan(36));

    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    var totalDataClusters = (int)((totalSectors - firstDataSector) / sectorsPerCluster);
    if (totalDataClusters <= 0) yield break;

    // The BPB says outright which variant this is: FAT32 is the one that keeps
    // its FAT size in the 32-bit field and has no fixed root directory. Reading
    // the type off the cluster count alone called a small forced-FAT32 volume
    // FAT16, and the walk then looked for a root directory area that FAT32 does
    // not have — the map came back with the reserved region and no files at
    // all, so a wipe saw the whole volume as free.
    var isFat32ByBpb = BinaryPrimitives.ReadUInt16LittleEndian(bpb.AsSpan(22)) == 0 && rootEntryCount == 0;
    var fatType = isFat32ByBpb ? 32
      : totalDataClusters < 4085 ? 12
      : totalDataClusters < 65525 ? 16
      : 32;
    var rootCluster = fatType == 32
      ? BinaryPrimitives.ReadInt32LittleEndian(bpb.AsSpan(44))
      : 0;
    var clusterSize = (long)sectorsPerCluster * bytesPerSector;
    var firstDataByte = firstDataSector * (long)bytesPerSector;

    // FAT navigation goes through a chunked LRU cache so we never hold the
    // whole FAT in RAM. For a 50 TB exFAT image the FAT alone is ~50 GB; the
    // cache keeps it bounded to ~256 MB regardless of image size.
    using var cache = new SectorCache(image);
    var fatBase = (long)reservedSectors * bytesPerSector;

    // For FAT12/16: the fixed root dir is small (max 14 sectors = 7 KB) — load
    // it into memory directly. Subdirectories use per-cluster reads.
    byte[]? fixedRoot = null;
    if (fatType != 32) {
      var rootBytes = rootDirSectors * bytesPerSector;
      fixedRoot = new byte[rootBytes];
      image.Position = (long)(reservedSectors + fatCount * fatSize) * bytesPerSector;
      image.ReadExactly(fixedRoot);
    }

    // Reserved region: boot + FATs + (FAT12/16) root directory.
    yield return new DefragBlockInfo(0, firstDataByte, DefragBlockKind.MetadataReserved,
      FileName: "FAT reserved (boot/FAT/root)");

    // Collect entries by walking the directory tree (uses targeted cluster reads).
    var clusterOwners = new string?[totalDataClusters + 2];
    var rootEntries = new List<(string name, int firstCluster, long size, bool isDir, DateTime? mtime)>();

    if (fatType == 32) {
      WalkClusterDirStream(image, cache, fatBase, fatType, reservedSectors, bytesPerSector, firstDataByte,
        clusterSize, totalDataClusters, rootCluster, "", rootEntries, [rootCluster]);

      // FAT32 root dir cluster chain is metadata — emit as MetadataReserved and
      // mark clusters owned so they don't appear as Free.
      var rootChainOwner = "FAT32 root directory";
      int rcRunStart = -1, rcRunEnd = -1;
      var rcSeen = new HashSet<int>();
      var rcCluster = rootCluster;
      while (rcCluster >= 2 && rcCluster <= totalDataClusters + 1
             && !IsEndOfChain(fatType, rcCluster) && rcSeen.Add(rcCluster)) {
        if (rcCluster < clusterOwners.Length) clusterOwners[rcCluster] = rootChainOwner;
        if (rcRunStart < 0) { rcRunStart = rcCluster; rcRunEnd = rcCluster; }
        else if (rcCluster == rcRunEnd + 1) { rcRunEnd = rcCluster; }
        else {
          var ro = firstDataByte + (long)(rcRunStart - 2) * clusterSize;
          var rl = (long)(rcRunEnd - rcRunStart + 1) * clusterSize;
          yield return new DefragBlockInfo(ro, rl, DefragBlockKind.MetadataReserved, rootChainOwner);
          rcRunStart = rcCluster;
          rcRunEnd = rcCluster;
        }
        rcCluster = GetNextClusterFromCache(cache, fatBase, fatType,rcCluster);
      }
      if (rcRunStart >= 0) {
        var ro = firstDataByte + (long)(rcRunStart - 2) * clusterSize;
        var rl = (long)(rcRunEnd - rcRunStart + 1) * clusterSize;
        yield return new DefragBlockInfo(ro, rl, DefragBlockKind.MetadataReserved, rootChainOwner);
      }
    } else {
      WalkFixedDirStream(fixedRoot!, image, cache, fatBase, fatType, reservedSectors, bytesPerSector,
        firstDataByte, clusterSize, totalDataClusters, "", rootEntries);
    }

    // Per-entry cluster-chain enumeration → contiguous-run extents. Subdir
    // chains emit with trailing "/" so the planner treats them as movable
    // metadata.
    foreach (var (name, firstCluster, size, isDir, _) in rootEntries) {
      if (firstCluster < 2 || firstCluster > totalDataClusters + 1) continue;
      var extentName = isDir ? name + "/" : name;
      var hasBoundedSize = !isDir;
      var bytesLeft = size;

      var cluster = firstCluster;
      var seen = new HashSet<int>();
      var runStart = -1;
      var runEnd = -1;
      while (cluster >= 2 && cluster <= totalDataClusters + 1
             && !IsEndOfChain(fatType, cluster) && seen.Add(cluster)) {
        if (cluster < clusterOwners.Length) clusterOwners[cluster] = extentName;
        if (runStart < 0) {
          runStart = cluster;
          runEnd = cluster;
        } else if (cluster == runEnd + 1) {
          runEnd = cluster;
        } else {
          var off = firstDataByte + (long)(runStart - 2) * clusterSize;
          var rawLen = (long)(runEnd - runStart + 1) * clusterSize;
          var len = hasBoundedSize ? Math.Min(rawLen, Math.Max(1L, bytesLeft)) : rawLen;
          if (len > 0)
            yield return new DefragBlockInfo(off, len, DefragBlockKind.Used, extentName,
              Classification: isDir ? DefragBlockClass.Directory : null);
          if (hasBoundedSize) bytesLeft -= len;
          runStart = cluster;
          runEnd = cluster;
        }
        cluster = GetNextClusterFromCache(cache, fatBase, fatType,cluster);
      }
      if (runStart >= 0) {
        var off = firstDataByte + (long)(runStart - 2) * clusterSize;
        var rawLen = (long)(runEnd - runStart + 1) * clusterSize;
        var len = hasBoundedSize && bytesLeft > 0 ? Math.Min(rawLen, bytesLeft) : rawLen;
        if (len <= 0) len = rawLen;
        yield return new DefragBlockInfo(off, len, DefragBlockKind.Used, extentName,
          Classification: isDir ? DefragBlockClass.Directory : null);
      }
    }

    // Free runs: contiguous unowned cluster ranges.
    {
      var freeStart = -1;
      for (var c = 2; c <= totalDataClusters + 1; c++) {
        if (clusterOwners[c] == null) {
          if (freeStart < 0) freeStart = c;
        } else if (freeStart >= 0) {
          var off = firstDataByte + (long)(freeStart - 2) * clusterSize;
          var len = (long)(c - freeStart) * clusterSize;
          yield return new DefragBlockInfo(off, len, DefragBlockKind.Free);
          freeStart = -1;
        }
      }
      if (freeStart >= 0) {
        var off = firstDataByte + (long)(freeStart - 2) * clusterSize;
        var len = (long)(totalDataClusters + 2 - freeStart) * clusterSize;
        if (len > 0) yield return new DefragBlockInfo(off, len, DefragBlockKind.Free);
      }
    }
  }

  // ── Streaming directory walks ──────────────────────────────────────────

  /// <summary>
  /// Walks a cluster-chain directory (FAT32 root + any subdir) by reading one
  /// cluster at a time from <paramref name="image"/>. FAT entries flow through
  /// <paramref name="cache"/> so we never need to hold the whole FAT in RAM.
  /// </summary>
  private static void WalkClusterDirStream(Stream image, SectorCache cache, long fatBase, int fatType,
      int reservedSectors, int bytesPerSector, long firstDataByte, long clusterSize,
      int totalDataClusters, int firstCluster, string path,
      List<(string, int, long, bool, DateTime?)> entries, HashSet<int> seenDirs) {
    using var ms = new MemoryStream();
    var clusterBuf = ArrayPool<byte>.Shared.Rent((int)clusterSize);
    try {
      var cluster = firstCluster;
      var seen = new HashSet<int>();
      while (cluster >= 2 && cluster <= totalDataClusters + 1
             && !IsEndOfChain(fatType, cluster) && seen.Add(cluster)) {
        var off = firstDataByte + (long)(cluster - 2) * clusterSize;
        if (off + clusterSize > image.Length) break;
        image.Position = off;
        image.ReadExactly(clusterBuf, 0, (int)clusterSize);
        ms.Write(clusterBuf, 0, (int)clusterSize);
        cluster = GetNextClusterFromCache(cache, fatBase, fatType, cluster);
      }
      var dir = ms.ToArray();
      ParseDirEntriesStream(image, cache, fatBase, dir, fatType, reservedSectors, bytesPerSector,
        firstDataByte, clusterSize, totalDataClusters, path, entries, seenDirs);
    } finally {
      ArrayPool<byte>.Shared.Return(clusterBuf);
    }
  }

  /// <summary>
  /// Walks the fixed FAT12/16 root directory (already loaded into memory).
  /// </summary>
  private static void WalkFixedDirStream(byte[] fixedRoot, Stream image, SectorCache cache, long fatBase,
      int fatType, int reservedSectors, int bytesPerSector, long firstDataByte, long clusterSize,
      int totalDataClusters, string path,
      List<(string, int, long, bool, DateTime?)> entries) {
    ParseDirEntriesStream(image, cache, fatBase, fixedRoot, fatType, reservedSectors, bytesPerSector,
      firstDataByte, clusterSize, totalDataClusters, path, entries, []);
  }

  private static void ParseDirEntriesStream(Stream image, SectorCache cache, long fatBase,
      byte[] dir, int fatType, int reservedSectors, int bytesPerSector, long firstDataByte,
      long clusterSize, int totalDataClusters, string path,
      List<(string, int, long, bool, DateTime?)> entries, HashSet<int> seenDirs) {
    var lfnParts = new SortedDictionary<int, string>();
    var maxEntries = dir.Length / 32;
    for (var i = 0; i < maxEntries; i++) {
      var off = i * 32;
      if (off + 32 > dir.Length) break;
      var firstByte = dir[off];
      if (firstByte == 0x00) break;
      if (firstByte == 0xE5) { lfnParts.Clear(); continue; }

      var attr = dir[off + 11];
      if ((attr & 0x3F) == 0x0F) {
        var seq = dir[off] & 0x3F;
        var part = new StringBuilder();
        ReadLfn(dir, off + 1, 5, part);
        ReadLfn(dir, off + 14, 6, part);
        ReadLfn(dir, off + 28, 2, part);
        lfnParts[seq] = part.ToString();
        continue;
      }
      if ((attr & 0x08) != 0) { lfnParts.Clear(); continue; } // volume label

      var shortName = GetShortName(dir, off);
      string name;
      if (lfnParts.Count > 0) {
        var sb = new StringBuilder();
        foreach (var p in lfnParts.Values) sb.Append(p);
        name = sb.ToString().TrimEnd('\0', '\xFFFF');
        lfnParts.Clear();
      } else
        name = shortName;

      var isDir = (attr & 0x10) != 0;
      var fileSize = BinaryPrimitives.ReadInt32LittleEndian(dir.AsSpan(off + 28));
      var startCluster = (int)BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(off + 26));
      if (fatType == 32)
        startCluster |= BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(off + 20)) << 16;

      if (name is "." or "..") continue;

      var date = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(off + 24));
      var time = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(off + 22));
      DateTime? mtime = null;
      if (date != 0) {
        try {
          mtime = new DateTime(1980 + (date >> 9), (date >> 5) & 0xF, date & 0x1F,
            time >> 11, (time >> 5) & 0x3F, (time & 0x1F) * 2);
        } catch { /* invalid */ }
      }

      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
      entries.Add((fullPath, startCluster, isDir ? 0 : fileSize, isDir, mtime));

      // Recurse into subdirectories, guarding against cycles.
      if (isDir && startCluster >= 2 && seenDirs.Add(startCluster)) {
        WalkClusterDirStream(image, cache, fatBase, fatType, reservedSectors, bytesPerSector, firstDataByte,
          clusterSize, totalDataClusters, startCluster, fullPath, entries, seenDirs);
      }
    }
  }

  private static void ReadLfn(byte[] data, int offset, int count, StringBuilder sb) {
    for (var j = 0; j < count; j++) {
      var charOff = offset + j * 2;
      if (charOff + 2 > data.Length) break;
      var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(charOff));
      if (c == 0 || c == 0xFFFF) break;
      sb.Append(c);
    }
  }

  private static string GetShortName(byte[] data, int offset) {
    var name = Encoding.ASCII.GetString(data, offset, 8).TrimEnd();
    var ext = Encoding.ASCII.GetString(data, offset + 8, 3).TrimEnd();
    return string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";
  }

  /// <summary>
  /// Reads a FAT entry via the SectorCache. The cache transparently fetches
  /// the FAT sector on miss; sequential walks hit a warm chunk for thousands
  /// of consecutive entries.
  /// </summary>
  private static int GetNextClusterFromCache(SectorCache cache, long fatBase, int fatType, int cluster) {
    Span<byte> buf = stackalloc byte[4];
    switch (fatType) {
      case 12: {
        var bytePos = fatBase + cluster * 3 / 2;
        cache.Read(bytePos, buf[..2]);
        var val = BinaryPrimitives.ReadUInt16LittleEndian(buf[..2]);
        return (cluster & 1) != 0 ? val >> 4 : val & 0xFFF;
      }
      case 16: {
        cache.Read(fatBase + cluster * 2, buf[..2]);
        return BinaryPrimitives.ReadUInt16LittleEndian(buf[..2]);
      }
      case 32: {
        cache.Read(fatBase + cluster * 4, buf);
        return BinaryPrimitives.ReadInt32LittleEndian(buf) & 0x0FFFFFFF;
      }
      default: return 0;
    }
  }

  private static bool IsEndOfChain(int fatType, int cluster) => fatType switch {
    12 => cluster >= 0xFF8,
    16 => cluster >= 0xFFF8,
    32 => cluster >= 0x0FFFFFF8,
    _ => true,
  };
}
