#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Fat;

/// <summary>
/// Secure-remove implementation for FAT12/16/32 images. Finds the named file in
/// the root directory, zeros every cluster it occupies (including trailing
/// cluster-tip slack past <c>i_size</c>), zeros the directory entry bytes (not
/// just the conventional 0xE5 deleted marker), and marks its clusters as free
/// in both FAT copies. After the operation no bytes of the original filename or
/// content remain recoverable from the image.
/// <para>
/// Root-directory-only for now; nested-directory removal is a follow-up.
/// </para>
/// </summary>
public static class FatRemover {
  /// <summary>
  /// Removes <paramref name="fileName"/> from the in-memory FAT image. Throws
  /// <see cref="FileNotFoundException"/> if no root-dir entry matches. The image
  /// is modified in place.
  /// </summary>
  public static void Remove(byte[] image, string fileName) {
    ArgumentNullException.ThrowIfNull(image);

    // Boot sector fields
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;
    var sectorsPerCluster = image[13] == 0 ? 1 : image[13];
    var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(14));
    var fatCount = image[16] == 0 ? 2 : image[16];
    var rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(17));
    var totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(19));
    var totalSectors = totalSectors16 == 0
      ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(32))
      : totalSectors16;
    var fatSize16 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(22));
    var fatSize = fatSize16 == 0
      ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(36))
      : fatSize16;
    var clusterSize = sectorsPerCluster * bytesPerSector;
    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    var totalDataClusters = (totalSectors - firstDataSector) / sectorsPerCluster;
    var fatType = totalDataClusters < 4085 ? 12 : totalDataClusters < 65525 ? 16 : 32;

    // Locate root directory.
    var rootDirOffset = fatType == 32
      ? ClusterOffset(image, reservedSectors, fatCount, fatSize, firstDataSector, bytesPerSector,
          BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(44)), sectorsPerCluster)
      : (reservedSectors + fatCount * fatSize) * bytesPerSector;
    var rootDirCapacity = fatType == 32 ? clusterSize : rootEntryCount * 32;

    // Search the root directory for the short-name entry. (LFN entries precede the
    // short-name entry; we zero them too to leave no trace.)
    var nameKey = fileName.ToUpperInvariant();
    var (entryIndex, firstLfnIndex) = FindEntry(image, rootDirOffset, rootDirCapacity, nameKey);
    if (entryIndex < 0)
      throw new FileNotFoundException($"File '{fileName}' not found in FAT root directory.");

    var entryOffset = rootDirOffset + entryIndex * 32;
    var firstClusterLow = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(entryOffset + 26));
    var firstClusterHigh = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(entryOffset + 20));
    var firstCluster = (firstClusterHigh << 16) | firstClusterLow;
    var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(entryOffset + 28));

    // Walk the cluster chain and collect its members (bounded to avoid infinite loops).
    var chain = WalkChain(image, firstCluster, reservedSectors, bytesPerSector, fatType, totalDataClusters);

    // Zero cluster data (including tip slack past fileSize).
    var remaining = (long)fileSize;
    foreach (var cluster in chain) {
      var dataOffset = (firstDataSector + (cluster - 2) * sectorsPerCluster) * bytesPerSector;
      if (dataOffset + clusterSize <= image.Length)
        image.AsSpan(dataOffset, clusterSize).Clear();
      remaining -= clusterSize;
    }
    _ = remaining;

    // Zero FAT entries for this chain in every FAT copy.
    for (var fatIdx = 0; fatIdx < fatCount; ++fatIdx) {
      var fatStart = (reservedSectors + fatIdx * fatSize) * bytesPerSector;
      foreach (var cluster in chain)
        ClearFatEntry(image, fatStart, cluster, fatType);
    }

    // Wipe the directory entries (short + any LFN precursors). Zero every byte after the
    // first, but keep the first byte as the 0xE5 "deleted" sentinel so readers don't treat
    // the slot as end-of-directory (which would truncate enumeration of any entries that
    // follow). That preserves FAT semantics while leaving no forensic trace of the filename.
    var from = firstLfnIndex >= 0 ? firstLfnIndex : entryIndex;
    for (var i = from; i <= entryIndex; ++i) {
      var off = rootDirOffset + i * 32;
      image.AsSpan(off, 32).Clear();
      image[off] = 0xE5;
    }

    // Update FSInfo free-count hint on FAT32 (informational — best-effort).
    if (fatType == 32) {
      var fsInfoSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(48));
      if (fsInfoSector != 0 && fsInfoSector < totalSectors) {
        var fsInfoOffset = fsInfoSector * bytesPerSector;
        if (fsInfoOffset + 512 <= image.Length &&
            BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(fsInfoOffset)) == 0x41615252) {
          var currentFree = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(fsInfoOffset + 488));
          if (currentFree != 0xFFFFFFFF)
            BinaryPrimitives.WriteUInt32LittleEndian(
              image.AsSpan(fsInfoOffset + 488),
              currentFree + (uint)chain.Count);
        }
      }
    }
  }

  private static (int EntryIndex, int FirstLfnIndex) FindEntry(
      byte[] image, int rootOffset, int capacity, string upperFileName) {
    var maxEntries = capacity / 32;
    var firstLfn = -1;
    for (var i = 0; i < maxEntries; ++i) {
      var off = rootOffset + i * 32;
      var first = image[off];
      if (first == 0x00) break;
      if (first == 0xE5) { firstLfn = -1; continue; }

      var attr = image[off + 11];
      // LFN entry: record its start and keep going.
      if ((attr & 0x3F) == 0x0F) {
        if (firstLfn < 0) firstLfn = i;
        continue;
      }

      // Volume-label entry (bit 3 set): not removable.
      if ((attr & 0x08) != 0) { firstLfn = -1; continue; }

      // Short name match — 8.3 form, space-padded, case-insensitive.
      var shortName = DecodeShortName(image.AsSpan(off, 11));
      if (shortName.Equals(upperFileName, StringComparison.OrdinalIgnoreCase))
        return (i, firstLfn);
      firstLfn = -1;
    }
    return (-1, -1);
  }

  private static string DecodeShortName(ReadOnlySpan<byte> entry) {
    var baseName = Encoding.ASCII.GetString(entry[..8]).TrimEnd(' ');
    var ext = Encoding.ASCII.GetString(entry[8..11]).TrimEnd(' ');
    return ext.Length == 0 ? baseName : $"{baseName}.{ext}";
  }

  private static List<int> WalkChain(byte[] image, int startCluster,
      int reservedSectors, int bytesPerSector, int fatType, int totalDataClusters) {
    var chain = new List<int>();
    var cluster = startCluster;
    var fatStart = reservedSectors * bytesPerSector;
    while (cluster >= 2 && cluster < totalDataClusters + 2 && chain.Count <= totalDataClusters) {
      chain.Add(cluster);
      cluster = ReadFatEntry(image, fatStart, cluster, fatType);
      if (IsEndOfChain(cluster, fatType)) break;
    }
    return chain;
  }

  private static int ReadFatEntry(byte[] image, int fatStart, int cluster, int fatType) => fatType switch {
    12 => ReadFat12(image, fatStart, cluster),
    16 => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(fatStart + cluster * 2)),
    _ => BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(fatStart + cluster * 4)) & 0x0FFFFFFF,
  };

  private static int ReadFat12(byte[] image, int fatStart, int cluster) {
    var off = fatStart + cluster + cluster / 2;
    var raw = (ushort)(image[off] | (image[off + 1] << 8));
    return (cluster & 1) != 0 ? raw >> 4 : raw & 0x0FFF;
  }

  private static void ClearFatEntry(byte[] image, int fatStart, int cluster, int fatType) {
    switch (fatType) {
      case 12: ClearFat12(image, fatStart, cluster); break;
      case 16:
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(fatStart + cluster * 2), 0);
        break;
      default:
        // FAT32: preserve top-4-reserved-bits, zero the low 28 bits.
        var off = fatStart + cluster * 4;
        var current = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(off));
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(off), current & unchecked((int)0xF0000000));
        break;
    }
  }

  private static void ClearFat12(byte[] image, int fatStart, int cluster) {
    var off = fatStart + cluster + cluster / 2;
    if ((cluster & 1) == 0) {
      image[off] = 0;
      image[off + 1] = (byte)(image[off + 1] & 0xF0);
    } else {
      image[off] = (byte)(image[off] & 0x0F);
      image[off + 1] = 0;
    }
  }

  private static bool IsEndOfChain(int value, int fatType) => fatType switch {
    12 => value >= 0xFF8,
    16 => value >= 0xFFF8,
    _ => value >= 0x0FFFFFF8,
  };

  private static int ClusterOffset(byte[] image, int reservedSectors, int fatCount, int fatSize,
      int firstDataSector, int bytesPerSector, int cluster, int sectorsPerCluster)
    => (firstDataSector + (cluster - 2) * sectorsPerCluster) * bytesPerSector;
}
