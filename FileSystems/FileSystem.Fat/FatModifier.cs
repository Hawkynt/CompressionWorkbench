#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Fat;

/// <summary>
/// Genuine in-place add for FAT12/16/32 images — the inverse of
/// <see cref="FatRemover"/>. Allocates free clusters from the FAT, writes the
/// file data into them (zeroing the trailing cluster-tip slack), links the
/// cluster chain in every FAT copy, and inserts a directory entry (VFAT/LFN +
/// 8.3, encoded by <see cref="FatWriter.BuildDirentSlots"/> so the bytes are
/// identical to a freshly-built image) into the first free run of root-directory
/// slots. Existing files, their data clusters and the boot sector stay
/// byte-identical at their original offsets; the image keeps its length.
/// <para>
/// Replace-by-name: an existing entry of the same name is removed first
/// (<see cref="FatRemover.Remove"/>) so the new bytes win. Cases the in-place
/// path does not handle — nested sub-directory targets, a full root directory,
/// or insufficient free clusters — throw so the caller can fall back to the
/// verified rebuild.
/// </para>
/// </summary>
public static class FatModifier {

  /// <summary>
  /// Adds (or replaces by name) <paramref name="name"/> in the root directory of
  /// the in-memory FAT image. Throws <see cref="NotSupportedException"/> for nested
  /// paths and <see cref="IOException"/> when the volume or root directory is full —
  /// the signal for the caller to use the rebuild path.
  /// </summary>
  public static void AddFile(byte[] image, string name, byte[] data, DateTime? modTime = null, bool forceLfn = false) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (name.Contains('/') || name.Contains('\\'))
      throw new NotSupportedException("FAT in-place add does not handle nested sub-directory targets.");

    var fs = ParseBootSector(image);

    // Replace-by-name: drop any prior entry (frees its clusters + slot) so a
    // same-named add overwrites rather than duplicates.
    try { FatRemover.Remove(image, name); } catch (FileNotFoundException) { /* new file */ }

    var clusterSize = fs.ClusterSize;
    var clustersNeeded = data.Length == 0 ? 0 : (data.Length + clusterSize - 1) / clusterSize;
    var chain = clustersNeeded == 0 ? [] : FindFreeClusters(image, fs, clustersNeeded);

    // Write data into the allocated clusters; zero the tail slack of the last one.
    for (var i = 0; i < chain.Count; ++i) {
      var off = ClusterByteOffset(fs, chain[i]);
      if (off + clusterSize > image.Length)
        throw new IOException("FAT in-place add: allocated cluster lies past the image end.");
      image.AsSpan(off, clusterSize).Clear();
      var srcStart = i * clusterSize;
      var copy = Math.Min(clusterSize, data.Length - srcStart);
      if (copy > 0) data.AsSpan(srcStart, copy).CopyTo(image.AsSpan(off));
    }

    // Link the cluster chain in every FAT copy.
    for (var fatIdx = 0; fatIdx < fs.FatCount; ++fatIdx) {
      var fatStart = (fs.ReservedSectors + fatIdx * fs.FatSize) * fs.BytesPerSector;
      for (var i = 0; i < chain.Count; ++i) {
        var next = i + 1 < chain.Count ? chain[i + 1] : EndOfChain(fs.FatType);
        WriteFatEntry(image, fatStart, chain[i], next, fs.FatType);
      }
    }

    // Build the directory entry slot blob (LFN + 8.3) exactly as the writer would,
    // then patch in the first cluster + file size.
    var existingShort = CollectShortNames(image, OpenRootDir(image, fs));
    var slots = FatWriter.BuildDirentSlots(name, existingShort, modTime, enableLfn: true, attr: 0x20, forceLfn: forceLfn);
    var shortOff = slots.Length - 32;
    var firstCluster = chain.Count == 0 ? 0 : chain[0];
    BinaryPrimitives.WriteUInt16LittleEndian(slots.AsSpan(shortOff + 20), (ushort)((firstCluster >> 16) & 0xFFFF));
    BinaryPrimitives.WriteUInt16LittleEndian(slots.AsSpan(shortOff + 26), (ushort)(firstCluster & 0xFFFF));
    BinaryPrimitives.WriteUInt32LittleEndian(slots.AsSpan(shortOff + 28), (uint)data.Length);

    // Place the slots in the first run of free root-directory slots.
    var dir = OpenRootDir(image, fs);
    var slotCount = slots.Length / 32;
    var startSlot = FindFreeSlotRun(image, dir, slotCount);
    for (var k = 0; k < slotCount; ++k) {
      var destOff = dir.SlotImageOffset(startSlot + k);
      slots.AsSpan(k * 32, 32).CopyTo(image.AsSpan(destOff, 32));
    }

    // FAT32 FSInfo free-count hint (best-effort).
    if (fs.FatType == 32 && chain.Count > 0) {
      var fsInfoSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(48));
      if (fsInfoSector != 0 && fsInfoSector < fs.TotalSectors) {
        var fsInfoOffset = fsInfoSector * fs.BytesPerSector;
        if (fsInfoOffset + 512 <= image.Length
            && BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(fsInfoOffset)) == 0x41615252) {
          var free = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(fsInfoOffset + 488));
          if (free != 0xFFFFFFFF && free >= chain.Count)
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(fsInfoOffset + 488), free - (uint)chain.Count);
        }
      }
    }
  }

  // ── Free-space allocation ────────────────────────────────────────────────

  private static List<int> FindFreeClusters(byte[] image, FatGeom fs, int count) {
    var fatStart = fs.ReservedSectors * fs.BytesPerSector;
    var free = new List<int>(count);
    for (var cluster = 2; cluster < fs.TotalDataClusters + 2 && free.Count < count; ++cluster) {
      if (ReadFatEntry(image, fatStart, cluster, fs.FatType) != 0) continue;
      if (ClusterByteOffset(fs, cluster) + fs.ClusterSize > image.Length) continue;
      free.Add(cluster);
    }
    if (free.Count < count)
      throw new IOException($"FAT in-place add: only {free.Count} free clusters, need {count}.");
    return free;
  }

  private static int FindFreeSlotRun(byte[] image, DirAccess dir, int runLength) {
    var consecutive = 0;
    for (var i = 0; i < dir.SlotCount; ++i) {
      var first = image[dir.SlotImageOffset(i)];
      if (first is 0x00 or 0xE5) {
        if (++consecutive == runLength) return i - runLength + 1;
      } else {
        consecutive = 0;
      }
    }
    throw new IOException($"FAT in-place add: no run of {runLength} free directory slots in the root.");
  }

  private static HashSet<string> CollectShortNames(byte[] image, DirAccess dir) {
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < dir.SlotCount; ++i) {
      var off = dir.SlotImageOffset(i);
      var first = image[off];
      if (first == 0x00) break;
      if (first == 0xE5) continue;
      var attr = image[off + 11];
      if ((attr & 0x3F) == 0x0F || (attr & 0x08) != 0) continue; // LFN slot / volume label
      var baseName = Encoding.ASCII.GetString(image, off, 8).TrimEnd(' ');
      var ext = Encoding.ASCII.GetString(image, off + 8, 3).TrimEnd(' ');
      set.Add(ext.Length == 0 ? baseName : $"{baseName}.{ext}");
    }
    return set;
  }

  // ── Boot sector + directory geometry (mirror of FatRemover) ───────────────

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
    var total = total16 == 0 ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(32)) : total16;
    var fatSize16 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(22));
    var fatSize = fatSize16 == 0 ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(36)) : fatSize16;
    var rootDirSectors = (rootEntries * 32 + bps - 1) / bps;
    var firstDataSector = reserved + fatCount * fatSize + rootDirSectors;
    var dataClusters = (total - firstDataSector) / spc;
    var fatType = dataClusters < 4085 ? 12 : dataClusters < 65525 ? 16 : 32;
    var rootCluster = fatType == 32 ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(44)) : 0;
    return new FatGeom(bps, spc, reserved, fatCount, rootEntries, total, fatSize,
      firstDataSector, dataClusters, fatType, rootCluster);
  }

  private readonly struct DirAccess(int[] slotOffsets) {
    public int SlotCount => slotOffsets.Length;
    public int SlotImageOffset(int slotIndex) => slotOffsets[slotIndex];
  }

  private static DirAccess OpenRootDir(byte[] image, FatGeom fs) {
    if (fs.FatType != 32) {
      var rootOffset = (fs.ReservedSectors + fs.FatCount * fs.FatSize) * fs.BytesPerSector;
      var slots = new int[fs.RootEntryCount];
      for (var i = 0; i < fs.RootEntryCount; ++i) slots[i] = rootOffset + i * 32;
      return new DirAccess(slots);
    }
    var chain = WalkChain(image, fs.RootCluster, fs);
    var slotsPerCluster = fs.ClusterSize / 32;
    var fat32Slots = new int[chain.Count * slotsPerCluster];
    for (var c = 0; c < chain.Count; ++c) {
      var clusterOff = ClusterByteOffset(fs, chain[c]);
      for (var s = 0; s < slotsPerCluster; ++s)
        fat32Slots[c * slotsPerCluster + s] = clusterOff + s * 32;
    }
    return new DirAccess(fat32Slots);
  }

  private static int ClusterByteOffset(FatGeom fs, int cluster)
    => (fs.FirstDataSector + (cluster - 2) * fs.SectorsPerCluster) * fs.BytesPerSector;

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

  private static void WriteFatEntry(byte[] image, int fatStart, int cluster, int value, int fatType) {
    switch (fatType) {
      case 12:
        var off12 = fatStart + cluster + cluster / 2;
        if ((cluster & 1) == 0) {
          image[off12] = (byte)(value & 0xFF);
          image[off12 + 1] = (byte)((image[off12 + 1] & 0xF0) | ((value >> 8) & 0x0F));
        } else {
          image[off12] = (byte)((image[off12] & 0x0F) | ((value << 4) & 0xF0));
          image[off12 + 1] = (byte)((value >> 4) & 0xFF);
        }
        break;
      case 16:
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(fatStart + cluster * 2), (ushort)value);
        break;
      default:
        var off32 = fatStart + cluster * 4;
        var reserved = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(off32)) & unchecked((int)0xF0000000);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(off32), reserved | (value & 0x0FFFFFFF));
        break;
    }
  }

  private static int EndOfChain(int fatType) => fatType switch { 12 => 0xFFF, 16 => 0xFFFF, _ => 0x0FFFFFFF };

  private static bool IsEndOfChain(int value, int fatType) => fatType switch {
    12 => value >= 0xFF8,
    16 => value >= 0xFFF8,
    _ => value >= 0x0FFFFFF8,
  };
}
