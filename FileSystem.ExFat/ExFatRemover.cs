#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ExFat;

/// <summary>
/// Secure-remove implementation for exFAT images. Finds a root-directory file's
/// entry set (File <c>0x85</c> + Stream Extension <c>0xC0</c> + N × File Name <c>0xC1</c>),
/// zeros every cluster in its allocation chain, clears its FAT entries, clears its bits
/// in the allocation bitmap, and wipes the directory entry set itself — preserving only
/// each entry's first byte with its <em>type bit</em> (bit 7) cleared so exFAT readers
/// treat the slots as "unused in-use" instead of end-of-directory.
/// <para>
/// Root-directory-only for now; nested-directory removal is a follow-up. No set-checksum
/// update is needed on removed entries — a cleared type bit makes readers skip them
/// entirely, including their checksum field.
/// </para>
/// </summary>
public static class ExFatRemover {
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

    // --- Read the root directory cluster chain into a buffer we can search. ---
    // We deliberately read only one cluster at a time so we can compute absolute
    // offsets in the source image for each directory entry we find.
    var rootEntries = CollectDirectoryEntries(image, rootDirCluster, clusterHeapOffset, clusterSize, fatOffset, clusterCount);

    // --- First pass: find the 0x81 Allocation Bitmap entry (for bit clearing). ---
    var (bitmapFirstCluster, bitmapLength) = FindAllocationBitmap(image, rootEntries);

    // --- Second pass: find the file's entry set by name. ---
    var (fileEntryAbsOffset, firstCluster, setBytes) = FindFile(image, rootEntries, fileName);
    if (fileEntryAbsOffset < 0)
      throw new FileNotFoundException($"File '{fileName}' not found in exFAT root directory.");

    // --- Walk cluster chain. ---
    var chain = WalkChain(image, firstCluster, fatOffset, clusterCount);

    // --- 1. Zero cluster data. ---
    foreach (var cluster in chain) {
      var dataOffset = clusterHeapOffset + (long)(cluster - 2) * clusterSize;
      if (dataOffset < 0 || dataOffset + clusterSize > image.Length) continue;
      image.AsSpan((int)dataOffset, clusterSize).Clear();
    }

    // --- 2. Zero FAT entries for this chain. ---
    foreach (var cluster in chain) {
      var fatEntryOffset = fatOffset + (int)cluster * 4;
      if (fatEntryOffset + 4 > image.Length) continue;
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(fatEntryOffset), 0);
    }

    // --- 3. Clear the allocation-bitmap bits for the freed clusters. ---
    if (bitmapFirstCluster >= 2) {
      var bitmapOffset = clusterHeapOffset + (int)(bitmapFirstCluster - 2) * clusterSize;
      foreach (var cluster in chain) {
        // Cluster numbering in the bitmap is 0-based starting at cluster 2.
        var bitIndex = (int)(cluster - 2);
        var byteIdx = bitmapOffset + bitIndex / 8;
        if (byteIdx < 0 || byteIdx >= image.Length) continue;
        if (bitmapLength > 0 && byteIdx >= bitmapOffset + bitmapLength) continue;
        image[byteIdx] &= (byte)~(1 << (bitIndex % 8));
      }
    }

    // --- 4. Wipe the directory entry set itself. ---
    // exFAT spec: clearing bit 7 of EntryType flips "in-use" → "unused". Readers stop
    // at 0x00 (end-of-dir) but simply skip entries with the type-bit cleared, so we
    // must preserve that first byte (with bit 7 cleared) and zero the other 31.
    for (var off = fileEntryAbsOffset; off < fileEntryAbsOffset + setBytes; off += 32) {
      image[off] = (byte)(image[off] & 0x7F);
      image.AsSpan(off + 1, 31).Clear();
    }

    // --- 5. No SetChecksum update needed: cleared type bit makes readers ignore it. ---

    // --- 6. Best-effort PercentInUse update in primary + backup VBR. ---
    var freedClusters = (uint)chain.Count;
    UpdatePercentInUse(image, 0, clusterCount, freedClusters);
    var backupVbrOffset = 12 * bytesPerSector;
    if (backupVbrOffset + 512 <= image.Length &&
        Encoding.ASCII.GetString(image, backupVbrOffset + 3, 8) == "EXFAT   ")
      UpdatePercentInUse(image, backupVbrOffset, clusterCount, freedClusters);
  }

  /// <summary>
  /// Reads a directory's cluster chain and returns per-entry absolute-offset records
  /// (into the source <paramref name="image"/>). Bounded to avoid infinite chains.
  /// </summary>
  private static List<(int AbsOffset, byte Type, byte SecondaryCount)> CollectDirectoryEntries(
      byte[] image, uint startCluster, int clusterHeapOffset, int clusterSize,
      int fatOffset, uint clusterCount) {
    var entries = new List<(int, byte, byte)>();
    var cluster = startCluster;
    var seen = new HashSet<uint>();
    while (cluster >= 2 && cluster <= clusterCount + 1 && seen.Add(cluster)) {
      var baseOffset = clusterHeapOffset + (int)(cluster - 2) * clusterSize;
      if (baseOffset < 0 || baseOffset + clusterSize > image.Length) break;
      for (var i = 0; i < clusterSize; i += 32) {
        var abs = baseOffset + i;
        var type = image[abs];
        if (type == 0x00) return entries; // end-of-directory
        entries.Add((abs, type, image[abs + 1]));
      }
      var fatEntryOffset = fatOffset + (int)cluster * 4;
      if (fatEntryOffset + 4 > image.Length) break;
      cluster = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(fatEntryOffset));
      if (cluster >= 0xFFFFFFF8) break;
    }
    return entries;
  }

  private static (uint FirstCluster, long Length) FindAllocationBitmap(
      byte[] image, List<(int AbsOffset, byte Type, byte SecondaryCount)> entries) {
    foreach (var (abs, type, _) in entries) {
      // Allocation Bitmap directory entry type = 0x81. Also accept "unused" form 0x01
      // defensively, though writer never emits it for bitmap.
      if ((type & 0x7F) != 0x01) continue;
      if ((type & 0x80) == 0) continue; // must be in-use
      var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(abs + 20));
      var length = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(abs + 24));
      return (firstCluster, length);
    }
    return (0, 0);
  }

  private static (int FileEntryAbsOffset, uint FirstCluster, int SetBytes) FindFile(
      byte[] image, List<(int AbsOffset, byte Type, byte SecondaryCount)> entries, string fileName) {
    for (var i = 0; i < entries.Count; ++i) {
      var (abs, type, secondaryCount) = entries[i];
      if (type != 0x85) continue; // in-use File entry
      if (i + 1 + secondaryCount > entries.Count) continue;

      var streamAbs = entries[i + 1].AbsOffset;
      if (image[streamAbs] != 0xC0) continue;
      var nameLength = image[streamAbs + 3];
      var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(streamAbs + 20));

      // Reconstruct the file name from the 0xC1 entries.
      var sb = new StringBuilder();
      var nameEntries = (nameLength + 14) / 15;
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

      if (!sb.ToString().Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;

      var setBytes = 32 * (1 + secondaryCount);
      return (abs, firstCluster, setBytes);
    }
    return (-1, 0, 0);
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

  private static void UpdatePercentInUse(byte[] image, int vbrOffset, uint clusterCount, uint freedClusters) {
    if (vbrOffset + 113 > image.Length) return;
    var current = image[vbrOffset + 112];
    if (current == 0xFF || clusterCount == 0) return; // unknown — leave untouched
    var freedPercent = (uint)(freedClusters * 100 / clusterCount);
    var updated = current > freedPercent ? current - freedPercent : 0;
    image[vbrOffset + 112] = (byte)updated;
  }
}
