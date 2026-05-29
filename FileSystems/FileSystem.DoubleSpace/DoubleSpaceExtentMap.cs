#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.DoubleSpace;

/// <summary>
/// Walks a DoubleSpace/DriveSpace CVF image and yields the actual on-disk
/// byte layout: metadata regions (MDBPB, inner FAT, root dir, MDFAT, BitFAT),
/// every compressed/stored cluster run per file (mapped through MDFAT), and
/// free physical sectors in the DATA region.
/// </summary>
public static class DoubleSpaceExtentMap {

  /// <summary>
  /// Enumerates the on-disk layout of a CVF image. Parses the MDBPB, walks the
  /// inner FAT chain per file, resolves each logical cluster through the MDFAT
  /// to its physical sector run in the DATA region, and emits one
  /// <see cref="DefragBlockInfo"/> per contiguous physical run per file.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();

    if (data.Length < 512) yield break;

    var signature = Encoding.ASCII.GetString(data, 3, 8);
    if (signature is not ("MSDSP6.0" or "MSDSP6.2" or "DRVSPACE")) yield break;

    var bytesPerSector = (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(11));
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;
    var sectorsPerCluster = (int)data[13];
    if (sectorsPerCluster == 0) sectorsPerCluster = 1;
    var reservedSectors = (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
    if (reservedSectors == 0) reservedSectors = 1;
    var fatCount = (int)data[16];
    if (fatCount == 0) fatCount = 2;
    var rootEntryCount = (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(17));
    var fatSize = (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(22));

    var mdfatStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44));
    var mdfatLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48));
    var bitFatStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(52));
    var bitFatLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(56));
    var dataStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(60));
    var dataLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(64));

    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;

    // Read MDFAT entries.
    uint[]? mdfat = null;
    var mdfatEntryCount = 0;
    if (mdfatStartSector > 0 && mdfatLenSectors > 0) {
      mdfatEntryCount = mdfatLenSectors * bytesPerSector / 4;
      mdfat = new uint[mdfatEntryCount];
      var baseOffset = mdfatStartSector * bytesPerSector;
      for (var i = 0; i < mdfatEntryCount; i++) {
        var off = baseOffset + i * 4;
        if (off + 4 > data.Length) break;
        mdfat[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
      }
    }

    // Metadata: MDBPB + inner FATs + root directory.
    var innerMetaEnd = firstDataSector * bytesPerSector;
    yield return new DefragBlockInfo(0, innerMetaEnd, DefragBlockKind.MetadataReserved,
      FileName: "CVF reserved (MDBPB/FAT/root)");

    // Metadata: MDFAT region.
    var mdfatByteStart = (long)mdfatStartSector * bytesPerSector;
    var mdfatByteLen = (long)mdfatLenSectors * bytesPerSector;
    if (mdfatByteLen > 0)
      yield return new DefragBlockInfo(mdfatByteStart, mdfatByteLen, DefragBlockKind.MetadataReserved,
        FileName: "MDFAT");

    // Metadata: BitFAT region.
    var bitFatByteStart = (long)bitFatStartSector * bytesPerSector;
    var bitFatByteLen = (long)bitFatLenSectors * bytesPerSector;
    if (bitFatByteLen > 0)
      yield return new DefragBlockInfo(bitFatByteStart, bitFatByteLen, DefragBlockKind.MetadataReserved,
        FileName: "BitFAT");

    // Parse directory to get file->cluster mappings.
    var rootOffset = (reservedSectors + fatCount * fatSize) * bytesPerSector;
    var entries = new List<(string Name, int StartCluster, long Size, bool IsDir)>();
    if (rootOffset + rootDirSectors * bytesPerSector <= data.Length)
      ParseDirectory(data, rootOffset, rootEntryCount, "", entries,
        firstDataSector, sectorsPerCluster, bytesPerSector);

    // Track which physical sectors in the DATA region are used.
    var dataRegionByteStart = (long)dataStartSector * bytesPerSector;
    var usedPhysSectors = new HashSet<int>();

    // For each file, walk its inner FAT chain, resolve each cluster through
    // MDFAT, and emit extents in the DATA region.
    foreach (var (name, startCluster, size, isDir) in entries) {
      // Subdir cluster chains get emitted as Used with trailing "/" so the
      // planner recognises them as directory metadata. Without this the dir
      // cluster sectors would be invisible to the planner and counted as Free,
      // letting "pack at start" overwrite directory entries.
      if (size == 0 || startCluster < 2) continue;
      var emitName = isDir ? name + "/" : name;
      if (mdfat == null) continue;

      var cluster = startCluster;
      var seen = new HashSet<int>();
      // Track physical sector runs for this file (contiguous runs in DATA region).
      var runPhysStart = -1L;
      var runPhysLen = 0L;

      while (cluster >= 2 && cluster < mdfatEntryCount && seen.Add(cluster)) {
        var entry = mdfat[cluster];
        var physSector = (int)(entry & 0x1FFFFFu);
        var runSectors = (int)((entry >> 21) & 0x7Fu);
        var flags = (int)((entry >> 28) & 0xFu);

        if (flags is 1 or 2 && runSectors > 0) {
          var physByteOffset = dataRegionByteStart + (long)physSector * bytesPerSector;
          var physByteLen = (long)runSectors * bytesPerSector;

          for (var s = 0; s < runSectors; s++)
            usedPhysSectors.Add(physSector + s);

          // Try to merge with previous run if contiguous.
          if (runPhysStart >= 0 && physByteOffset == runPhysStart + runPhysLen) {
            runPhysLen += physByteLen;
          } else {
            // Flush previous run.
            if (runPhysStart >= 0)
              yield return new DefragBlockInfo(runPhysStart, runPhysLen, DefragBlockKind.Used, emitName,
                Classification: isDir ? DefragBlockClass.Directory : null);
            runPhysStart = physByteOffset;
            runPhysLen = physByteLen;
          }
        }

        // Follow inner FAT16 chain.
        cluster = ReadInnerFatEntry(data, reservedSectors * bytesPerSector, cluster);
        if (cluster is 0 or >= 0xFFF8 and <= 0xFFFF) break;
      }

      // Flush last run.
      if (runPhysStart >= 0)
        yield return new DefragBlockInfo(runPhysStart, runPhysLen, DefragBlockKind.Used, emitName,
          Classification: isDir ? DefragBlockClass.Directory : null);
    }

    // Emit free regions in the DATA area.
    if (dataLenSectors > 0) {
      var freeStart = -1;
      for (var s = 0; s < dataLenSectors; s++) {
        if (!usedPhysSectors.Contains(s)) {
          if (freeStart < 0) freeStart = s;
        } else if (freeStart >= 0) {
          var off = dataRegionByteStart + (long)freeStart * bytesPerSector;
          var len = (long)(s - freeStart) * bytesPerSector;
          yield return new DefragBlockInfo(off, len, DefragBlockKind.Free);
          freeStart = -1;
        }
      }
      if (freeStart >= 0) {
        var off = dataRegionByteStart + (long)freeStart * bytesPerSector;
        var len = (long)(dataLenSectors - freeStart) * bytesPerSector;
        if (len > 0)
          yield return new DefragBlockInfo(off, len, DefragBlockKind.Free);
      }
    }
  }

  private static int ReadInnerFatEntry(byte[] data, int fatOffset, int cluster) {
    var entryOffset = fatOffset + cluster * 2;
    if (entryOffset + 2 > data.Length) return 0xFFFF;
    return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(entryOffset));
  }

  private static void ParseDirectory(byte[] data, int offset, int maxEntries, string path,
      List<(string Name, int StartCluster, long Size, bool IsDir)> results,
      int firstDataSector, int sectorsPerCluster, int bytesPerSector) {
    var pendingLfn = new List<string>();

    for (var i = 0; i < maxEntries; i++) {
      var off = offset + i * 32;
      if (off + 32 > data.Length) break;
      var firstByte = data[off];
      if (firstByte == 0x00) break;
      if (firstByte == 0xE5) { pendingLfn.Clear(); continue; }

      var attr = data[off + 11];
      if ((attr & 0x3F) == 0x0F) {
        var seq = firstByte & 0x3F;
        var chars = new char[13];
        int[] slots = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
        for (var k = 0; k < 13; k++) {
          var ch = (ushort)(data[off + slots[k]] | (data[off + slots[k] + 1] << 8));
          chars[k] = (char)ch;
        }
        while (pendingLfn.Count < seq) pendingLfn.Add("");
        pendingLfn[seq - 1] = new string(chars);
        continue;
      }

      if ((attr & 0x08) != 0) { pendingLfn.Clear(); continue; }

      var shortName = GetShortName(data, off);
      if (shortName is "." or "..") { pendingLfn.Clear(); continue; }

      var name = shortName;
      if (pendingLfn.Count > 0) {
        var combined = string.Concat(pendingLfn);
        var endIdx = combined.IndexOfAny(['\0', '￿']);
        if (endIdx >= 0) combined = combined[..endIdx];
        if (combined.Length > 0) name = combined;
        pendingLfn.Clear();
      }

      var isDir = (attr & 0x10) != 0;
      var fileSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 28));
      var startCluster = (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 26));

      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
      results.Add((fullPath, startCluster, isDir ? 0 : fileSize, isDir));

      if (isDir && startCluster >= 2) {
        var clusterBytes = sectorsPerCluster * bytesPerSector;
        var dirOffset = (firstDataSector + (startCluster - 2) * sectorsPerCluster) * bytesPerSector;
        var dirSize = clusterBytes / 32;
        if (dirOffset + 32 <= data.Length)
          ParseDirectory(data, dirOffset, dirSize, fullPath, results,
            firstDataSector, sectorsPerCluster, bytesPerSector);
      }
    }
  }

  private static string GetShortName(byte[] data, int offset) {
    var name = Encoding.ASCII.GetString(data, offset, 8).TrimEnd();
    var ext = Encoding.ASCII.GetString(data, offset + 8, 3).TrimEnd();
    return string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";
  }
}
