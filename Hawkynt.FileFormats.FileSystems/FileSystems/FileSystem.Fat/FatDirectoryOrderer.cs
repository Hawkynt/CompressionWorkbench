using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Fat;

/// <summary>
/// Sorts FAT12/16/32 directory entries without relocating any file allocation.
/// VFAT long-name slots and their short entry are treated as one atomic record.
/// </summary>
internal static class FatDirectoryOrderer {
  private const byte AttrVolumeId = 0x08;
  private const byte AttrDirectory = 0x10;
  private const byte AttrLongName = 0x0F;

  public static void Sort(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("FAT directory ordering requires a readable, writable, seekable stream.", nameof(image));

    var geometry = Geometry.Read(image);
    var visited = new HashSet<int>();
    if (geometry.FatType == 32)
      SortClusterDirectory(image, geometry, geometry.RootCluster, "", visited);
    else
      SortFixedDirectory(image, geometry, "");
    image.Flush();
  }

  private static void SortFixedDirectory(Stream image, Geometry geometry, string path) {
    var offset = (long)(geometry.ReservedSectors + geometry.FatCount * geometry.FatSize) * geometry.BytesPerSector;
    var length = geometry.RootEntryCount * 32;
    var data = new byte[length];
    image.Position = offset;
    image.ReadExactly(data);

    var subdirectories = SortDirectoryBuffer(data, geometry.FatType, path);
    image.Position = offset;
    image.Write(data);

    var visited = new HashSet<int>();
    foreach (var subdirectory in subdirectories)
      SortClusterDirectory(image, geometry, subdirectory.Cluster, subdirectory.Path, visited);
  }

  private static void SortClusterDirectory(
      Stream image,
      Geometry geometry,
      int startCluster,
      string path,
      HashSet<int> visited) {
    if (startCluster < 2 || !visited.Add(startCluster))
      return;

    var chain = ReadChain(image, geometry, startCluster);
    var data = new byte[checked(chain.Count * geometry.ClusterSize)];
    for (var i = 0; i < chain.Count; ++i) {
      image.Position = geometry.ClusterToOffset(chain[i]);
      image.ReadExactly(data.AsSpan(i * geometry.ClusterSize, geometry.ClusterSize));
    }

    var subdirectories = SortDirectoryBuffer(data, geometry.FatType, path);
    for (var i = 0; i < chain.Count; ++i) {
      image.Position = geometry.ClusterToOffset(chain[i]);
      image.Write(data.AsSpan(i * geometry.ClusterSize, geometry.ClusterSize));
    }

    foreach (var subdirectory in subdirectories)
      SortClusterDirectory(image, geometry, subdirectory.Cluster, subdirectory.Path, visited);
  }

  private static IReadOnlyList<Subdirectory> SortDirectoryBuffer(byte[] data, int fatType, string path) {
    var pinned = new List<Record>();
    var sortable = new List<Record>();
    var deleted = new List<byte[]>();
    var pendingLongNames = new List<byte[]>();
    var subdirectories = new List<Subdirectory>();
    var ordinal = 0;

    for (var offset = 0; offset + 32 <= data.Length; offset += 32) {
      var slot = data.AsSpan(offset, 32);
      var first = slot[0];
      if (first == 0x00) {
        foreach (var orphan in pendingLongNames)
          deleted.Add(orphan);
        pendingLongNames.Clear();
        break;
      }

      if (first == 0xE5) {
        foreach (var orphan in pendingLongNames)
          deleted.Add(orphan);
        pendingLongNames.Clear();
        deleted.Add(slot.ToArray());
        continue;
      }

      var attr = slot[11];
      if ((attr & 0x3F) == AttrLongName) {
        pendingLongNames.Add(slot.ToArray());
        continue;
      }

      var shortSlot = slot.ToArray();
      var name = pendingLongNames.Count > 0
        ? DecodeLongName(pendingLongNames)
        : DecodeShortName(shortSlot);
      var raw = new byte[(pendingLongNames.Count + 1) * 32];
      for (var i = 0; i < pendingLongNames.Count; ++i)
        pendingLongNames[i].CopyTo(raw, i * 32);
      shortSlot.CopyTo(raw, pendingLongNames.Count * 32);
      pendingLongNames.Clear();

      var isVolumeLabel = (attr & AttrVolumeId) != 0;
      var isDot = name is "." or "..";
      var record = new Record(name, raw, ordinal++);
      if (isVolumeLabel || isDot)
        pinned.Add(record);
      else
        sortable.Add(record);

      if ((attr & AttrDirectory) == 0 || isDot || isVolumeLabel)
        continue;

      int cluster = BinaryPrimitives.ReadUInt16LittleEndian(shortSlot.AsSpan(26, 2));
      if (fatType == 32)
        cluster |= BinaryPrimitives.ReadUInt16LittleEndian(shortSlot.AsSpan(20, 2)) << 16;
      if (cluster >= 2) {
        var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
        subdirectories.Add(new Subdirectory(cluster, fullPath));
      }
    }

    var ordered = sortable
      .OrderBy(static r => r.Name, StringComparer.OrdinalIgnoreCase)
      .ThenBy(static r => r.Name, StringComparer.Ordinal)
      .ThenBy(static r => r.Ordinal)
      .ToArray();

    var cursor = 0;
    foreach (var record in pinned.Concat(ordered)) {
      if (cursor + record.Raw.Length > data.Length)
        throw new InvalidDataException("FAT directory records exceed their allocation.");
      record.Raw.CopyTo(data, cursor);
      cursor += record.Raw.Length;
    }

    // Deleted slots remain deleted and stay after all live records. They are not
    // interpreted as files, but retaining their bytes avoids turning a harmless
    // name-order operation into an implicit secure-delete operation.
    foreach (var raw in deleted) {
      if (cursor + raw.Length > data.Length)
        break;
      raw.CopyTo(data, cursor);
      cursor += raw.Length;
    }

    if (cursor < data.Length)
      data.AsSpan(cursor).Clear();

    return subdirectories;
  }

  private static string DecodeLongName(IReadOnlyList<byte[]> slots) {
    var parts = new SortedDictionary<int, string>();
    foreach (var slot in slots) {
      var sequence = slot[0] & 0x3F;
      var builder = new StringBuilder(13);
      AppendLfn(slot, 1, 5, builder);
      AppendLfn(slot, 14, 6, builder);
      AppendLfn(slot, 28, 2, builder);
      parts[sequence] = builder.ToString();
    }

    var result = new StringBuilder();
    foreach (var part in parts.Values)
      result.Append(part);
    return result.ToString().TrimEnd('\0', '\uFFFF');
  }

  private static void AppendLfn(ReadOnlySpan<byte> slot, int offset, int count, StringBuilder builder) {
    for (var i = 0; i < count; ++i) {
      var value = (char)BinaryPrimitives.ReadUInt16LittleEndian(slot.Slice(offset + i * 2, 2));
      if (value is '\0' or '\uFFFF')
        break;
      builder.Append(value);
    }
  }

  private static string DecodeShortName(ReadOnlySpan<byte> slot) {
    var name = Encoding.ASCII.GetString(slot[..8]).TrimEnd();
    var extension = Encoding.ASCII.GetString(slot.Slice(8, 3)).TrimEnd();
    var caseFlags = slot[12];
    if ((caseFlags & 0x08) != 0) name = name.ToLowerInvariant();
    if ((caseFlags & 0x10) != 0) extension = extension.ToLowerInvariant();
    return extension.Length == 0 ? name : $"{name}.{extension}";
  }

  private static List<int> ReadChain(Stream image, Geometry geometry, int startCluster) {
    var result = new List<int>();
    var seen = new HashSet<int>();
    var cluster = startCluster;
    while (cluster >= 2 && cluster <= geometry.TotalDataClusters + 1
           && !geometry.IsEndOfChain(cluster) && seen.Add(cluster)) {
      result.Add(cluster);
      cluster = geometry.ReadFatEntry(image, cluster);
    }

    if (result.Count == 0)
      throw new InvalidDataException($"FAT directory starts at invalid cluster {startCluster}.");
    return result;
  }

  private sealed record Record(string Name, byte[] Raw, int Ordinal);
  private readonly record struct Subdirectory(int Cluster, string Path);

  private sealed record Geometry(
      int BytesPerSector,
      int SectorsPerCluster,
      int ReservedSectors,
      int FatCount,
      int RootEntryCount,
      int FatSize,
      int FirstDataSector,
      int TotalDataClusters,
      int FatType,
      int RootCluster) {
    public int ClusterSize => checked(this.BytesPerSector * this.SectorsPerCluster);

    public long ClusterToOffset(int cluster)
      => ((long)this.FirstDataSector + (long)(cluster - 2) * this.SectorsPerCluster) * this.BytesPerSector;

    public bool IsEndOfChain(int cluster) => this.FatType switch {
      12 => cluster >= 0xFF8,
      16 => cluster >= 0xFFF8,
      32 => cluster >= 0x0FFFFFF8,
      _ => true,
    };

    public int ReadFatEntry(Stream image, int cluster) {
      var fatOffset = (long)this.ReservedSectors * this.BytesPerSector;
      Span<byte> bytes = stackalloc byte[4];
      switch (this.FatType) {
        case 12: {
          image.Position = fatOffset + (long)cluster * 3 / 2;
          image.ReadExactly(bytes[..2]);
          var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
          return (cluster & 1) == 0 ? value & 0xFFF : value >> 4;
        }
        case 16:
          image.Position = fatOffset + (long)cluster * 2;
          image.ReadExactly(bytes[..2]);
          return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        case 32:
          image.Position = fatOffset + (long)cluster * 4;
          image.ReadExactly(bytes);
          return BinaryPrimitives.ReadInt32LittleEndian(bytes) & 0x0FFFFFFF;
        default:
          throw new InvalidDataException($"Unsupported FAT type {this.FatType}.");
      }
    }

    public static Geometry Read(Stream image) {
      if (image.Length < 512)
        throw new InvalidDataException("FAT image is too small.");
      Span<byte> boot = stackalloc byte[512];
      image.Position = 0;
      image.ReadExactly(boot);

      var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
      var sectorsPerCluster = boot[13];
      var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[14..]);
      var fatCount = boot[16];
      var rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(boot[17..]);
      int totalSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[19..]);
      if (totalSectors == 0)
        totalSectors = BinaryPrimitives.ReadInt32LittleEndian(boot[32..]);
      var fat16Size = BinaryPrimitives.ReadUInt16LittleEndian(boot[22..]);
      var fatSize = fat16Size != 0 ? fat16Size : BinaryPrimitives.ReadInt32LittleEndian(boot[36..]);

      if (bytesPerSector is < 512 or > 4096 || (bytesPerSector & (bytesPerSector - 1)) != 0
          || sectorsPerCluster == 0 || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0
          || reservedSectors == 0 || fatCount == 0 || fatSize <= 0 || totalSectors <= 0)
        throw new InvalidDataException("FAT BPB contains invalid geometry.");

      var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
      var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
      var totalDataClusters = (totalSectors - firstDataSector) / sectorsPerCluster;
      var fatType = fat16Size == 0 ? 32
        : totalDataClusters < 4085 ? 12
        : totalDataClusters < 65525 ? 16
        : 32;
      var rootCluster = fatType == 32
        ? BinaryPrimitives.ReadInt32LittleEndian(boot[44..])
        : 0;

      return new Geometry(
        bytesPerSector, sectorsPerCluster, reservedSectors, fatCount,
        rootEntryCount, fatSize, firstDataSector, totalDataClusters, fatType, rootCluster);
    }
  }
}
