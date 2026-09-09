#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ExFat;

/// <summary>
/// Secure-remove implementation for exFAT images. Finds a root-directory file's
/// entry set (File <c>0x85</c> + Stream Extension <c>0xC0</c> + N × File Name <c>0xC1</c>),
/// wipes its directory entry set, then zeros and frees only clusters that are no
/// longer referenced by any other live directory entry.
/// <para>
/// This deliberately understands both FAT-described chains and <c>NoFatChain</c>
/// contiguous allocations. That keeps read-only shared-data aliases emitted by the
/// optimizer valid until their last directory entry is removed. The file selected
/// for deletion is still root-directory-only; reference discovery recurses through
/// all live subdirectories so a nested alias can keep a root file's allocation alive.
/// </para>
/// <para>
/// No set-checksum update is needed on removed entries — clearing bit 7 of each
/// EntryType makes readers ignore those slots, including their checksum field.
/// </para>
/// </summary>
public static class ExFatRemover {
  private readonly record struct DirectorySlot(int AbsOffset, byte Type, byte SecondaryCount);
  private readonly record struct FileAllocation(
    int EntryOffset,
    int[] SetOffsets,
    uint FirstCluster,
    byte StreamFlags,
    long DataLength);

  /// <summary>
  /// Removes <paramref name="fileName"/> from the in-memory exFAT image. Throws
  /// <see cref="FileNotFoundException"/> if no root-dir entry matches. The image is
  /// modified in place.
  /// </summary>
  public static void Remove(byte[] image, string fileName) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < 512) throw new InvalidDataException("exFAT: image too small.");
    if (Encoding.ASCII.GetString(image, 3, 8) != "EXFAT   ")
      throw new InvalidDataException("exFAT: invalid signature.");

    // --- Parse VBR ---
    var bytesPerSector = 1 << image[108];
    var sectorsPerCluster = 1 << image[109];
    var clusterSize = bytesPerSector * sectorsPerCluster;
    var fatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(80));
    var clusterHeapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(88));
    var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(92));
    var rootDirCluster = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(96));
    var fatOffset = (int)(fatOffsetSectors * (uint)bytesPerSector);
    var clusterHeapOffset = (int)(clusterHeapOffsetSectors * (uint)bytesPerSector);

    var rootEntries = CollectDirectoryEntries(
      image,
      WalkChain(image, rootDirCluster, fatOffset, clusterCount),
      clusterHeapOffset,
      clusterSize);

    // Allocation Bitmap is authoritative for allocated/free state in exFAT.
    var (bitmapFirstCluster, bitmapLength) = FindAllocationBitmap(image, rootEntries);

    var file = FindFile(image, rootEntries, fileName);
    if (file is null)
      throw new FileNotFoundException($"File '{fileName}' not found in exFAT root directory.");

    var allocation = file.Value;
    var chain = ResolveAllocation(
      image,
      allocation.FirstCluster,
      allocation.StreamFlags,
      allocation.DataLength,
      fatOffset,
      clusterCount,
      clusterSize);

    // Discover references before deleting the directory entry. In particular,
    // another file may point at the same first cluster or may overlap only a suffix
    // of this allocation. No cluster still reachable elsewhere may be wiped/freed.
    var referencedElsewhere = CollectReferencedClusters(
      image,
      rootDirCluster,
      allocation.EntryOffset,
      clusterHeapOffset,
      clusterSize,
      fatOffset,
      clusterCount);
    var freeable = chain.Where(cluster => !referencedElsewhere.Contains(cluster)).ToArray();

    // exFAT §8.1 recommends deleting/updating the directory entry before releasing
    // its allocation. If a later write fails, that ordering leaks space rather than
    // leaving a live name pointing at storage that has already been freed.
    foreach (var off in allocation.SetOffsets) {
      image[off] = (byte)(image[off] & 0x7F);
      image.AsSpan(off + 1, 31).Clear();
    }

    // Securely wipe only clusters whose last live reference just disappeared.
    foreach (var cluster in freeable) {
      var dataOffset = clusterHeapOffset + (long)(cluster - 2) * clusterSize;
      if (dataOffset < 0 || dataOffset + clusterSize > image.Length) continue;
      image.AsSpan((int)dataOffset, clusterSize).Clear();
    }

    // FAT entries are meaningful for FAT-described allocations. Clearing an
    // unreferenced entry is harmless even when this particular allocation used
    // NoFatChain; another live FAT-chain allocation would have put the cluster in
    // referencedElsewhere and prevented the clear.
    foreach (var cluster in freeable) {
      var fatEntryOffset = fatOffset + (int)cluster * 4;
      if (fatEntryOffset + 4 > image.Length) continue;
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(fatEntryOffset), 0);
    }

    if (bitmapFirstCluster >= 2) {
      var bitmapOffset = clusterHeapOffset + (int)(bitmapFirstCluster - 2) * clusterSize;
      foreach (var cluster in freeable) {
        var bitIndex = (int)(cluster - 2);
        var byteIdx = bitmapOffset + bitIndex / 8;
        if (byteIdx < 0 || byteIdx >= image.Length) continue;
        if (bitmapLength > 0 && byteIdx >= bitmapOffset + bitmapLength) continue;
        image[byteIdx] &= (byte)~(1 << (bitIndex % 8));
      }
    }

    // Removed slots are ignored, so no SetChecksum rewrite is required.
    var freedClusters = (uint)freeable.Length;
    UpdatePercentInUse(image, 0, clusterCount, freedClusters);
    var backupVbrOffset = 12 * bytesPerSector;
    if (backupVbrOffset + 512 <= image.Length &&
        Encoding.ASCII.GetString(image, backupVbrOffset + 3, 8) == "EXFAT   ")
      UpdatePercentInUse(image, backupVbrOffset, clusterCount, freedClusters);
  }

  /// <summary>
  /// Returns per-slot absolute offsets for a logical directory allocation. Passing
  /// explicit cluster indices keeps entry sets correct even when consecutive logical
  /// directory slots live in physically non-contiguous clusters.
  /// </summary>
  private static List<DirectorySlot> CollectDirectoryEntries(
      byte[] image,
      IReadOnlyList<uint> clusters,
      int clusterHeapOffset,
      int clusterSize) {
    var entries = new List<DirectorySlot>();
    foreach (var cluster in clusters) {
      var baseOffsetLong = clusterHeapOffset + (long)(cluster - 2) * clusterSize;
      if (baseOffsetLong < 0 || baseOffsetLong + clusterSize > image.Length) break;
      var baseOffset = (int)baseOffsetLong;
      for (var i = 0; i < clusterSize; i += 32) {
        var abs = baseOffset + i;
        var type = image[abs];
        if (type == 0x00) return entries;
        entries.Add(new DirectorySlot(abs, type, image[abs + 1]));
      }
    }
    return entries;
  }

  private static (uint FirstCluster, long Length) FindAllocationBitmap(
      byte[] image, IReadOnlyList<DirectorySlot> entries) {
    foreach (var entry in entries) {
      if ((entry.Type & 0x7F) != 0x01) continue;
      if ((entry.Type & 0x80) == 0) continue;
      var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(entry.AbsOffset + 20));
      var length = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(entry.AbsOffset + 24));
      return (firstCluster, length);
    }
    return (0, 0);
  }

  private static FileAllocation? FindFile(
      byte[] image, IReadOnlyList<DirectorySlot> entries, string fileName) {
    for (var i = 0; i < entries.Count; ++i) {
      var primary = entries[i];
      if (primary.Type != 0x85) continue;
      var secondaryCount = primary.SecondaryCount;
      if (i + secondaryCount >= entries.Count) continue;

      var streamAbs = entries[i + 1].AbsOffset;
      if (image[streamAbs] != 0xC0) continue;
      var nameLength = image[streamAbs + 3];
      var nameEntries = (nameLength + 14) / 15;
      if (nameEntries + 1 > secondaryCount) continue;

      var sb = new StringBuilder(nameLength);
      for (var n = 0; n < nameEntries; ++n) {
        var nameAbs = entries[i + 2 + n].AbsOffset;
        if (image[nameAbs] != 0xC1) break;
        var charsToRead = Math.Min(15, nameLength - n * 15);
        for (var c = 0; c < charsToRead; ++c) {
          var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(nameAbs + 2 + c * 2));
          if (ch == 0) break;
          sb.Append(ch);
        }
      }

      if (!sb.ToString().Equals(fileName, StringComparison.OrdinalIgnoreCase)) {
        i += secondaryCount;
        continue;
      }

      var setOffsets = new int[1 + secondaryCount];
      for (var slot = 0; slot < setOffsets.Length; ++slot)
        setOffsets[slot] = entries[i + slot].AbsOffset;
      return new FileAllocation(
        primary.AbsOffset,
        setOffsets,
        BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(streamAbs + 20)),
        image[streamAbs + 1],
        BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(streamAbs + 24)));
    }
    return null;
  }

  private static List<uint> WalkChain(byte[] image, uint startCluster, int fatOffset, uint clusterCount) {
    var chain = new List<uint>();
    var cluster = startCluster;
    var seen = new HashSet<uint>();
    while (cluster >= 2 && cluster <= clusterCount + 1 && seen.Add(cluster) && chain.Count <= clusterCount) {
      chain.Add(cluster);
      var fatEntryOffset = fatOffset + (int)cluster * 4;
      if (fatEntryOffset + 4 > image.Length) break;
      var next = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(fatEntryOffset));
      if (next >= 0xFFFFFFF8) break;
      cluster = next;
    }
    return chain;
  }

  private static List<uint> ResolveAllocation(
      byte[] image,
      uint firstCluster,
      byte streamFlags,
      long dataLength,
      int fatOffset,
      uint clusterCount,
      int clusterSize) {
    if (firstCluster < 2 || dataLength <= 0) return [];
    if ((streamFlags & 0x02) == 0)
      return WalkChain(image, firstCluster, fatOffset, clusterCount);

    var count = checked((int)(((dataLength - 1) / clusterSize) + 1));
    var result = new List<uint>(count);
    for (var i = 0; i < count; ++i) {
      var cluster = firstCluster + (uint)i;
      if (cluster > clusterCount + 1) break;
      result.Add(cluster);
    }
    return result;
  }

  /// <summary>
  /// Collects every cluster reachable from another live file or directory. The
  /// recursion intentionally follows nested directories even though Remove itself
  /// currently accepts only a root file name.
  /// </summary>
  private static HashSet<uint> CollectReferencedClusters(
      byte[] image,
      uint rootDirCluster,
      int excludedEntryOffset,
      int clusterHeapOffset,
      int clusterSize,
      int fatOffset,
      uint clusterCount) {
    var referenced = new HashSet<uint>();
    var visitedDirectories = new HashSet<uint>();

    foreach (var cluster in WalkChain(image, rootDirCluster, fatOffset, clusterCount))
      referenced.Add(cluster);

    ScanDirectoryReferences(
      image,
      rootDirCluster,
      0x01,
      clusterSize,
      excludedEntryOffset,
      clusterHeapOffset,
      clusterSize,
      fatOffset,
      clusterCount,
      referenced,
      visitedDirectories);
    return referenced;
  }

  private static void ScanDirectoryReferences(
      byte[] image,
      uint firstCluster,
      byte streamFlags,
      long dataLength,
      int excludedEntryOffset,
      int clusterHeapOffset,
      int clusterSize,
      int fatOffset,
      uint clusterCount,
      HashSet<uint> referenced,
      HashSet<uint> visitedDirectories) {
    if (firstCluster < 2 || !visitedDirectories.Add(firstCluster)) return;

    var directoryClusters = ResolveAllocation(
      image, firstCluster, streamFlags, Math.Max(dataLength, clusterSize), fatOffset, clusterCount, clusterSize);
    foreach (var cluster in directoryClusters)
      referenced.Add(cluster);

    var entries = CollectDirectoryEntries(image, directoryClusters, clusterHeapOffset, clusterSize);
    for (var i = 0; i < entries.Count; ++i) {
      var primary = entries[i];

      // Allocation Bitmap and Up-case Table are root-level metadata allocations;
      // keep them protected even in a damaged image where a file happens to overlap.
      if (primary.Type is 0x81 or 0x82) {
        var systemFirst = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(primary.AbsOffset + 20));
        var systemLength = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(primary.AbsOffset + 24));
        foreach (var cluster in WalkChain(image, systemFirst, fatOffset, clusterCount).Take(
                   systemLength > 0 ? checked((int)(((systemLength - 1) / clusterSize) + 1)) : int.MaxValue))
          referenced.Add(cluster);
        continue;
      }

      if (primary.Type != 0x85) continue;
      var secondaryCount = primary.SecondaryCount;
      if (i + secondaryCount >= entries.Count) continue;
      var streamAbs = entries[i + 1].AbsOffset;
      if (image[streamAbs] != 0xC0) continue;

      var attributes = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(primary.AbsOffset + 4));
      var childFlags = image[streamAbs + 1];
      var childFirst = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(streamAbs + 20));
      var childLength = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(streamAbs + 24));
      var childClusters = ResolveAllocation(
        image, childFirst, childFlags, childLength, fatOffset, clusterCount, clusterSize);

      if (primary.AbsOffset != excludedEntryOffset)
        foreach (var cluster in childClusters)
          referenced.Add(cluster);

      if ((attributes & 0x0010) != 0 && childFirst >= 2)
        ScanDirectoryReferences(
          image,
          childFirst,
          childFlags,
          childLength,
          excludedEntryOffset,
          clusterHeapOffset,
          clusterSize,
          fatOffset,
          clusterCount,
          referenced,
          visitedDirectories);

      i += secondaryCount;
    }
  }

  private static void UpdatePercentInUse(byte[] image, int vbrOffset, uint clusterCount, uint freedClusters) {
    if (vbrOffset + 113 > image.Length) return;
    var current = image[vbrOffset + 112];
    if (current == 0xFF || clusterCount == 0) return;
    var freedPercent = (uint)(freedClusters * 100 / clusterCount);
    var updated = current > freedPercent ? current - freedPercent : 0;
    image[vbrOffset + 112] = (byte)updated;
  }
}
