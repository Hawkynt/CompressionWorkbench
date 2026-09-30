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
/// identical to a freshly-built image) into the target folder — creating missing
/// folders and growing a chained folder by a cluster when it is full. Existing
/// files, their attributes, times and data clusters and the boot sector stay
/// byte-identical at their original offsets; the image keeps its length.
/// <para>
/// Replace-by-path: an existing entry of the same name is removed first
/// (<see cref="FatRemover.Remove"/>) so the new bytes win. A volume or a fixed
/// FAT12/16 root directory with no room throws <see cref="IOException"/>.
/// </para>
/// </summary>
public static class FatModifier {

  /// <summary>
  /// Adds (or replaces by path) <paramref name="name"/> — a path separated by <c>/</c>
  /// or <c>\</c> — in the in-memory FAT image. Missing folders are created, and a
  /// folder stored as a cluster chain grows by a cluster when its slots run out.
  /// Throws <see cref="IOException"/> when the volume, or FAT12/16's fixed root
  /// directory, has no room.
  /// </summary>
  public static void AddFile(byte[] image, string name, byte[] data, DateTime? modTime = null, bool forceLfn = false) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var segments = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0) throw new ArgumentException("Empty path.", nameof(name));

    var fs = ParseBootSector(image);

    // Replace-by-path: drop any prior file (frees its clusters + slots) so a
    // same-named add overwrites rather than duplicates. A folder of that name stays.
    if (FatRemover.Lookup(image, string.Join('/', segments)) is { } prior && (prior.Attr & 0x10) != 0)
      throw new IOException($"FAT: '{name}' is a folder.");
    try { FatRemover.Remove(image, string.Join('/', segments)); } catch (FileNotFoundException) { /* new file */ }

    // Descend, creating what is missing. Cluster 0 stands for the root.
    var parentCluster = 0;
    for (var i = 0; i < segments.Length - 1; ++i) {
      var existing = FatRemover.Lookup(image, string.Join('/', segments[..(i + 1)]));
      if (existing is { } entry) {
        if ((entry.Attr & 0x10) == 0)
          throw new IOException($"FAT: '{string.Join('/', segments[..(i + 1)])}' is a file, not a folder.");
        parentCluster = entry.FirstCluster;
      } else {
        parentCluster = MakeDirectory(image, fs, parentCluster, segments[i], modTime);
      }
    }

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
    LinkChain(image, fs, chain);

    InsertEntry(image, fs, parentCluster, segments[^1], chain.Count == 0 ? 0 : chain[0], (uint)data.Length,
      attr: 0x20, modTime, forceLfn);
    AdjustFsInfoFree(image, fs, -chain.Count);
  }

  /// <summary>
  /// Creates folder <paramref name="name" /> in the folder whose chain starts at
  /// <paramref name="parentCluster" /> (0 = root): one zeroed cluster holding "." and
  /// "..", linked as a chain of one, and an entry in the parent. Returns its cluster.
  /// </summary>
  private static int MakeDirectory(byte[] image, FatGeom fs, int parentCluster, string name, DateTime? modTime) {
    var cluster = FindFreeClusters(image, fs, 1)[0];
    var off = ClusterByteOffset(fs, cluster);
    image.AsSpan(off, fs.ClusterSize).Clear();
    LinkChain(image, fs, [cluster]);

    // "." and ".." carry the creation stamp of the folder itself; ".." names the
    // parent, and the root is always named 0 there, even on FAT32.
    var stamp = FatWriter.BuildDirentSlots("X", [], modTime, enableLfn: false, attr: 0x10, forceLfn: false);
    WriteDotEntry(image, off, ".          ", cluster, stamp);
    WriteDotEntry(image, off + 32, "..         ", parentCluster, stamp);

    InsertEntry(image, fs, parentCluster, name, cluster, 0, attr: 0x10, modTime, forceLfn: false);
    AdjustFsInfoFree(image, fs, -1);
    return cluster;
  }

  private static void WriteDotEntry(byte[] image, int off, string shortName, int cluster, byte[] stampSource) {
    var stampOff = stampSource.Length - 32;
    stampSource.AsSpan(stampOff, 32).CopyTo(image.AsSpan(off, 32));
    Encoding.ASCII.GetBytes(shortName).CopyTo(image, off);
    image[off + 11] = 0x10;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 20), (ushort)((cluster >> 16) & 0xFFFF));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 26), (ushort)(cluster & 0xFFFF));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 28), 0);
  }

  /// <summary>
  /// Writes the entry slots (LFN + 8.3, encoded exactly as the writer does) for
  /// <paramref name="name" /> into the first free run of the folder's slots, growing a
  /// chained folder by a cluster when none is long enough.
  /// </summary>
  private static void InsertEntry(byte[] image, FatGeom fs, int dirCluster, string name, int firstCluster, uint size,
      byte attr, DateTime? modTime, bool forceLfn) {
    var dir = dirCluster == 0 ? OpenRootDir(image, fs) : OpenSubDir(image, fs, dirCluster);
    var existingShort = CollectShortNames(image, dir);
    var slots = FatWriter.BuildDirentSlots(name, existingShort, modTime, enableLfn: true, attr: attr, forceLfn: forceLfn);
    var shortOff = slots.Length - 32;
    BinaryPrimitives.WriteUInt16LittleEndian(slots.AsSpan(shortOff + 20), (ushort)((firstCluster >> 16) & 0xFFFF));
    BinaryPrimitives.WriteUInt16LittleEndian(slots.AsSpan(shortOff + 26), (ushort)(firstCluster & 0xFFFF));
    BinaryPrimitives.WriteUInt32LittleEndian(slots.AsSpan(shortOff + 28), size);

    var slotCount = slots.Length / 32;
    var startSlot = TryFindFreeSlotRun(image, dir, slotCount);
    while (startSlot < 0) {
      var chainStart = dirCluster != 0 ? dirCluster : fs.FatType == 32 ? fs.RootCluster : 0;
      if (chainStart == 0)
        throw new IOException($"FAT in-place add: no run of {slotCount} free slots in the fixed-size root directory.");
      GrowDirectory(image, fs, chainStart);
      dir = OpenSubDir(image, fs, chainStart);
      startSlot = TryFindFreeSlotRun(image, dir, slotCount);
    }
    for (var k = 0; k < slotCount; ++k)
      slots.AsSpan(k * 32, 32).CopyTo(image.AsSpan(dir.SlotImageOffset(startSlot + k), 32));
  }

  /// <summary>Appends one zeroed cluster to the chain starting at <paramref name="firstCluster" />.</summary>
  private static void GrowDirectory(byte[] image, FatGeom fs, int firstCluster) {
    var chain = WalkChain(image, firstCluster, fs);
    var added = FindFreeClusters(image, fs, 1)[0];
    image.AsSpan(ClusterByteOffset(fs, added), fs.ClusterSize).Clear();
    for (var fatIdx = 0; fatIdx < fs.FatCount; ++fatIdx) {
      var fatStart = (fs.ReservedSectors + fatIdx * fs.FatSize) * fs.BytesPerSector;
      WriteFatEntry(image, fatStart, chain[^1], added, fs.FatType);
      WriteFatEntry(image, fatStart, added, EndOfChain(fs.FatType), fs.FatType);
    }
    AdjustFsInfoFree(image, fs, -1);
  }

  /// <summary>Links <paramref name="chain" /> in every FAT copy, ending it with an end-of-chain mark.</summary>
  private static void LinkChain(byte[] image, FatGeom fs, List<int> chain) {
    for (var fatIdx = 0; fatIdx < fs.FatCount; ++fatIdx) {
      var fatStart = (fs.ReservedSectors + fatIdx * fs.FatSize) * fs.BytesPerSector;
      for (var i = 0; i < chain.Count; ++i) {
        var next = i + 1 < chain.Count ? chain[i + 1] : EndOfChain(fs.FatType);
        WriteFatEntry(image, fatStart, chain[i], next, fs.FatType);
      }
    }
  }

  /// <summary>Moves the FAT32 FSInfo free-cluster hint by <paramref name="delta" /> (best-effort).</summary>
  private static void AdjustFsInfoFree(byte[] image, FatGeom fs, int delta) {
    if (fs.FatType != 32 || delta == 0) return;
    var fsInfoSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(48));
    if (fsInfoSector == 0 || fsInfoSector >= fs.TotalSectors) return;
    var fsInfoOffset = fsInfoSector * fs.BytesPerSector;
    if (fsInfoOffset + 512 > image.Length
        || BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(fsInfoOffset)) != 0x41615252) return;
    var free = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(fsInfoOffset + 488));
    if (free == 0xFFFFFFFF) return;
    var updated = (long)free + delta;
    if (updated >= 0) BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(fsInfoOffset + 488), (uint)updated);
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

  private static int TryFindFreeSlotRun(byte[] image, DirAccess dir, int runLength) {
    var consecutive = 0;
    for (var i = 0; i < dir.SlotCount; ++i) {
      var first = image[dir.SlotImageOffset(i)];
      if (first is 0x00 or 0xE5) {
        if (++consecutive == runLength) return i - runLength + 1;
      } else {
        consecutive = 0;
      }
    }
    return -1;
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
    // A FAT32 boot sector says so by leaving the 16-bit FAT size at zero; only
    // FAT12 and FAT16 are told apart by cluster count. Counting alone read a small
    // FAT32 volume (fewer than 65525 clusters, which mkfs.vfat -F 32 makes) as FAT16.
    var fatType = fatSize16 == 0 ? 32 : dataClusters < 4085 ? 12 : 16;
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
