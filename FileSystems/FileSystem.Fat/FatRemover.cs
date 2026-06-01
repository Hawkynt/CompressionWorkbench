#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Fat;

/// <summary>
/// Secure-remove implementation for FAT12/16/32 images. Resolves a path that
/// may include subdirectory components (separated by <c>/</c>), finds the leaf
/// entry — matching either its short 8.3 name or its long filename — zeros
/// every cluster the file occupies (including trailing cluster-tip slack past
/// <c>i_size</c>), zeros the on-disk directory entry bytes (LFN slots plus the
/// short entry), and frees its clusters in every FAT copy. After the operation
/// no bytes of the filename or content remain recoverable from the image.
/// </summary>
public static class FatRemover {

  /// <summary>
  /// Removes <paramref name="filePath"/> from the in-memory FAT image. The path
  /// may name a file directly in the root directory (e.g. <c>"README.TXT"</c>)
  /// or in a nested subdirectory (e.g. <c>"Documents/Pictures/Desktop.ini"</c>).
  /// Matching is case-insensitive and supports both 8.3 short names and VFAT
  /// long filenames. Throws <see cref="FileNotFoundException"/> if no entry
  /// matches along the path. The image is modified in place.
  /// </summary>
  public static void Remove(byte[] image, string filePath) {
    ArgumentNullException.ThrowIfNull(image);
    if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("Empty path.", nameof(filePath));

    var fs = ParseBootSector(image);
    var segments = filePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0) throw new ArgumentException("Empty path.", nameof(filePath));

    // Walk each non-leaf segment: find its directory entry, descend into its
    // cluster chain. The root directory has special framing on FAT12/16 (fixed
    // single contiguous run); subdirectories live in cluster chains.
    var dir = OpenRootDir(image, fs);
    for (var i = 0; i < segments.Length - 1; ++i) {
      var match = FindEntry(image, dir, segments[i]);
      if (match.EntryImageOffset < 0)
        throw new FileNotFoundException(
          $"Directory '{string.Join('/', segments.AsSpan(0, i + 1).ToArray())}' not found in FAT image.");
      if ((match.Attr & 0x10) == 0)
        throw new FileNotFoundException(
          $"Path component '{segments[i]}' is a file, not a directory.");
      dir = OpenSubDir(image, fs, match.FirstCluster);
    }

    var leafName = segments[^1];
    var leaf = FindEntry(image, dir, leafName);
    if (leaf.EntryImageOffset < 0)
      throw new FileNotFoundException($"File '{filePath}' not found in FAT image.");
    if ((leaf.Attr & 0x10) != 0)
      throw new InvalidOperationException(
        $"'{filePath}' is a directory; directory removal is not yet implemented.");

    // Zero the file's data clusters + their trailing slack.
    var chain = WalkChain(image, leaf.FirstCluster, fs);
    foreach (var cluster in chain) {
      var dataOffset = ClusterByteOffset(fs, cluster);
      if (dataOffset + fs.ClusterSize <= image.Length)
        image.AsSpan(dataOffset, fs.ClusterSize).Clear();
    }

    // Zero FAT entries in every FAT copy.
    for (var fatIdx = 0; fatIdx < fs.FatCount; ++fatIdx) {
      var fatStart = (fs.ReservedSectors + fatIdx * fs.FatSize) * fs.BytesPerSector;
      foreach (var cluster in chain)
        ClearFatEntry(image, fatStart, cluster, fs.FatType);
    }

    // Wipe the leaf's directory entry slots (LFN precursors + short entry).
    // Each slot lives at a known image offset (per the directory's cluster
    // mapping); first byte stays as 0xE5 sentinel so readers don't truncate.
    var firstSlot = leaf.LfnStartSlotIndex >= 0 ? leaf.LfnStartSlotIndex : leaf.ShortSlotIndex;
    for (var slot = firstSlot; slot <= leaf.ShortSlotIndex; ++slot) {
      var off = dir.SlotImageOffset(slot);
      image.AsSpan(off, 32).Clear();
      image[off] = 0xE5;
    }

    // Update FAT32 FSInfo free-count hint (best-effort).
    if (fs.FatType == 32) {
      var fsInfoSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(48));
      if (fsInfoSector != 0 && fsInfoSector < fs.TotalSectors) {
        var fsInfoOffset = fsInfoSector * fs.BytesPerSector;
        if (fsInfoOffset + 512 <= image.Length
            && BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(fsInfoOffset)) == 0x41615252) {
          var currentFree = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(fsInfoOffset + 488));
          if (currentFree != 0xFFFFFFFF)
            BinaryPrimitives.WriteUInt32LittleEndian(
              image.AsSpan(fsInfoOffset + 488),
              currentFree + (uint)chain.Count);
        }
      }
    }
  }

  // ── Boot sector + directory geometry ─────────────────────────────────────

  private readonly record struct FatGeom(
      int BytesPerSector, int SectorsPerCluster, int ReservedSectors, int FatCount,
      int RootEntryCount, int TotalSectors, int FatSize, int FirstDataSector,
      int TotalDataClusters, int FatType, int RootCluster) {
    public int ClusterSize => this.SectorsPerCluster * this.BytesPerSector;
  }

  private static FatGeom ParseBootSector(byte[] image) {
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    if (bps is 0 or > 4096) bps = 512;
    var spc = image[13] == 0 ? 1 : image[13];
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(14));
    var fatCount = image[16] == 0 ? 2 : image[16];
    var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(17));
    var total16 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(19));
    var total = total16 == 0
      ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(32))
      : total16;
    var fatSize16 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(22));
    var fatSize = fatSize16 == 0
      ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(36))
      : fatSize16;
    var rootDirSectors = (rootEntries * 32 + bps - 1) / bps;
    var firstDataSector = reserved + fatCount * fatSize + rootDirSectors;
    var dataClusters = (total - firstDataSector) / spc;
    var fatType = dataClusters < 4085 ? 12 : dataClusters < 65525 ? 16 : 32;
    var rootCluster = fatType == 32 ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(44)) : 0;
    return new FatGeom(bps, spc, reserved, fatCount, rootEntries, total, fatSize,
      firstDataSector, dataClusters, fatType, rootCluster);
  }

  /// <summary>
  /// Maps a directory's 32-byte slot indices to absolute image offsets. For
  /// the FAT12/16 root, all slots are in one contiguous run. For subdirectories
  /// (and the FAT32 root), slots can span multiple non-contiguous clusters.
  /// </summary>
  private readonly struct DirAccess {
    public readonly int[] SlotOffsets; // 32-byte aligned image offsets

    public DirAccess(int[] slotOffsets) { this.SlotOffsets = slotOffsets; }
    public int SlotCount => this.SlotOffsets.Length;
    public int SlotImageOffset(int slotIndex) => this.SlotOffsets[slotIndex];
  }

  private static DirAccess OpenRootDir(byte[] image, FatGeom fs) {
    if (fs.FatType != 32) {
      var rootOffset = (fs.ReservedSectors + fs.FatCount * fs.FatSize) * fs.BytesPerSector;
      var slots = new int[fs.RootEntryCount];
      for (var i = 0; i < fs.RootEntryCount; ++i) slots[i] = rootOffset + i * 32;
      return new DirAccess(slots);
    }
    return OpenSubDir(image, fs, fs.RootCluster);
  }

  private static DirAccess OpenSubDir(byte[] image, FatGeom fs, int firstCluster) {
    var chain = WalkChain(image, firstCluster, fs);
    var slotsPerCluster = fs.ClusterSize / 32;
    var slots = new int[chain.Count * slotsPerCluster];
    for (var c = 0; c < chain.Count; ++c) {
      var clusterOff = ClusterByteOffset(fs, chain[c]);
      for (var s = 0; s < slotsPerCluster; ++s)
        slots[c * slotsPerCluster + s] = clusterOff + s * 32;
    }
    return new DirAccess(slots);
  }

  private static int ClusterByteOffset(FatGeom fs, int cluster)
    => (fs.FirstDataSector + (cluster - 2) * fs.SectorsPerCluster) * fs.BytesPerSector;

  // ── Directory entry search (LFN + 8.3, case-insensitive) ─────────────────

  private readonly record struct EntryMatch(
      int EntryImageOffset, int ShortSlotIndex, int LfnStartSlotIndex,
      byte Attr, int FirstCluster, uint FileSize);

  private static EntryMatch FindEntry(byte[] image, DirAccess dir, string targetName) {
    var lfnParts = new SortedDictionary<int, string>();
    var lfnStart = -1;
    for (var i = 0; i < dir.SlotCount; ++i) {
      var off = dir.SlotImageOffset(i);
      var first = image[off];
      if (first == 0x00) break; // end of directory
      if (first == 0xE5) { lfnParts.Clear(); lfnStart = -1; continue; }
      var attr = image[off + 11];

      if ((attr & 0x3F) == 0x0F) {
        // LFN slot — collect a fragment of the long name.
        var seq = first & 0x3F;
        var part = new StringBuilder();
        ReadLfnChars(image, off + 1, 5, part);
        ReadLfnChars(image, off + 14, 6, part);
        ReadLfnChars(image, off + 28, 2, part);
        lfnParts[seq] = part.ToString();
        if (lfnStart < 0) lfnStart = i;
        continue;
      }

      if ((attr & 0x08) != 0) { lfnParts.Clear(); lfnStart = -1; continue; } // volume label

      // Reconstruct LFN if present.
      string? longName = null;
      if (lfnParts.Count > 0) {
        var sb = new StringBuilder();
        foreach (var p in lfnParts.Values) sb.Append(p);
        longName = sb.ToString().TrimEnd('\0', '\xFFFF');
      }
      var shortName = DecodeShortName(image.AsSpan(off, 11));

      var matchesLong = longName != null
        && longName.Equals(targetName, StringComparison.OrdinalIgnoreCase);
      var matchesShort = shortName.Equals(targetName, StringComparison.OrdinalIgnoreCase);
      if (matchesLong || matchesShort) {
        var firstClusterLow = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(off + 26));
        var firstClusterHigh = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(off + 20));
        var firstCluster = (firstClusterHigh << 16) | firstClusterLow;
        var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 28));
        return new EntryMatch(off, i, lfnParts.Count > 0 ? lfnStart : -1,
          attr, firstCluster, fileSize);
      }
      lfnParts.Clear();
      lfnStart = -1;
    }
    return new EntryMatch(-1, -1, -1, 0, 0, 0);
  }

  private static void ReadLfnChars(byte[] data, int offset, int count, StringBuilder sb) {
    for (var j = 0; j < count; ++j) {
      var charOff = offset + j * 2;
      if (charOff + 2 > data.Length) break;
      var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(charOff));
      if (c == 0 || c == 0xFFFF) break;
      sb.Append(c);
    }
  }

  private static string DecodeShortName(ReadOnlySpan<byte> entry) {
    var baseName = Encoding.ASCII.GetString(entry[..8]).TrimEnd(' ');
    var ext = Encoding.ASCII.GetString(entry[8..11]).TrimEnd(' ');
    return ext.Length == 0 ? baseName : $"{baseName}.{ext}";
  }

  // ── FAT chain walking + clearing ─────────────────────────────────────────

  private static List<int> WalkChain(byte[] image, int startCluster, FatGeom fs) {
    var chain = new List<int>();
    var cluster = startCluster;
    var fatStart = fs.ReservedSectors * fs.BytesPerSector;
    while (cluster >= 2 && cluster < fs.TotalDataClusters + 2 && chain.Count <= fs.TotalDataClusters) {
      chain.Add(cluster);
      cluster = ReadFatEntry(image, fatStart, cluster, fs.FatType);
      if (IsEndOfChain(cluster, fs.FatType)) break;
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
}
