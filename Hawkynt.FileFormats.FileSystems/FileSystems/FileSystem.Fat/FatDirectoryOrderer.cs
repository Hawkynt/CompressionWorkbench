using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Fat;

/// <summary>
/// In-place FAT12/16/32 directory-entry sorter. It never touches file data,
/// FAT allocation chains, timestamps or attributes; only complete directory
/// record groups are reordered.
/// </summary>
public static class FatDirectoryOrderer {
  private readonly record struct Layout(
    int FatType,
    int BytesPerSector,
    int SectorsPerCluster,
    int ReservedSectors,
    int FatCount,
    int FatSize,
    int RootEntryCount,
    int RootCluster,
    int FirstDataSector
  ) {
    public int ClusterSize => this.BytesPerSector * this.SectorsPerCluster;
    public long FatOffset => (long)this.ReservedSectors * this.BytesPerSector;
    public long RootOffset => (long)(this.ReservedSectors + this.FatCount * this.FatSize) * this.BytesPerSector;
  }

  private sealed record EntryGroup(byte[] Bytes, string Name, bool IsDirectory, int StartCluster, bool IsPinned);

  /// <summary>Sorts every directory recursively using ordinal-ignore-case name order.</summary>
  public static void Sort(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("FAT directory sorting requires a readable, writable, seekable stream.", nameof(image));

    var layout = ReadLayout(image);
    var visited = new HashSet<int>();
    if (layout.FatType == 32)
      SortClusterDirectory(image, layout, layout.RootCluster, visited);
    else
      SortFixedRoot(image, layout, visited);
    image.Flush();
  }

  private static Layout ReadLayout(Stream image) {
    Span<byte> bpb = stackalloc byte[90];
    image.Position = 0;
    image.ReadExactly(bpb);

    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bpb[11..]);
    var sectorsPerCluster = bpb[13];
    var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(bpb[14..]);
    var fatCount = bpb[16];
    var rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(bpb[17..]);
    var totalSectors = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb[19..]);
    if (totalSectors == 0)
      totalSectors = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bpb[32..]));
    var fat16 = BinaryPrimitives.ReadUInt16LittleEndian(bpb[22..]);
    var isFat32 = fat16 == 0;
    var fatSize = isFat32
      ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bpb[36..]))
      : (int)fat16;
    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    var clusterCount = (totalSectors - firstDataSector) / sectorsPerCluster;
    var fatType = isFat32 ? 32 : clusterCount < 4085 ? 12 : clusterCount < 65525 ? 16 : 32;
    var rootCluster = fatType == 32
      ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bpb[44..]))
      : 0;
    return new Layout(fatType, bytesPerSector, sectorsPerCluster, reservedSectors,
      fatCount, fatSize, rootEntryCount, rootCluster, firstDataSector);
  }

  private static void SortFixedRoot(Stream image, Layout layout, HashSet<int> visited) {
    var byteCount = checked(layout.RootEntryCount * 32);
    var data = new byte[byteCount];
    image.Position = layout.RootOffset;
    image.ReadExactly(data);
    var groups = ParseGroups(data, layout.FatType);
    RecurseChildren(image, layout, groups, visited);
    Rewrite(data, groups);
    image.Position = layout.RootOffset;
    image.Write(data);
  }

  private static void SortClusterDirectory(Stream image, Layout layout, int firstCluster, HashSet<int> visited) {
    if (firstCluster < 2 || !visited.Add(firstCluster)) return;
    var chain = ReadChain(image, layout, firstCluster);
    if (chain.Count == 0) return;

    var data = new byte[checked(chain.Count * layout.ClusterSize)];
    for (var i = 0; i < chain.Count; ++i) {
      image.Position = ClusterOffset(layout, chain[i]);
      image.ReadExactly(data.AsSpan(i * layout.ClusterSize, layout.ClusterSize));
    }

    var groups = ParseGroups(data, layout.FatType);
    RecurseChildren(image, layout, groups, visited);
    Rewrite(data, groups);

    for (var i = 0; i < chain.Count; ++i) {
      image.Position = ClusterOffset(layout, chain[i]);
      image.Write(data.AsSpan(i * layout.ClusterSize, layout.ClusterSize));
    }
  }

  private static void RecurseChildren(
      Stream image,
      Layout layout,
      IReadOnlyList<EntryGroup> groups,
      HashSet<int> visited) {
    foreach (var group in groups)
      if (group.IsDirectory && !group.IsPinned && group.StartCluster >= 2)
        SortClusterDirectory(image, layout, group.StartCluster, visited);
  }

  private static List<EntryGroup> ParseGroups(byte[] data, int fatType) {
    var result = new List<EntryGroup>();
    var pendingLfn = new List<byte[]>();
    for (var offset = 0; offset + 32 <= data.Length; offset += 32) {
      var first = data[offset];
      if (first == 0x00) break;
      if (first == 0xE5) { pendingLfn.Clear(); continue; }

      var attr = data[offset + 11];
      if ((attr & 0x3F) == 0x0F) {
        pendingLfn.Add(data.AsSpan(offset, 32).ToArray());
        continue;
      }

      var shortEntry = data.AsSpan(offset, 32).ToArray();
      var bytes = new byte[(pendingLfn.Count + 1) * 32];
      for (var i = 0; i < pendingLfn.Count; ++i)
        pendingLfn[i].CopyTo(bytes, i * 32);
      shortEntry.CopyTo(bytes, pendingLfn.Count * 32);

      var volumeLabel = (attr & 0x08) != 0;
      var name = pendingLfn.Count == 0 ? ReadShortName(shortEntry) : ReadLongName(pendingLfn);
      var isPinned = volumeLabel || name is "." or "..";
      var isDirectory = (attr & 0x10) != 0 && !volumeLabel;
      var startCluster = (int)BinaryPrimitives.ReadUInt16LittleEndian(shortEntry.AsSpan(26));
      if (fatType == 32)
        startCluster |= BinaryPrimitives.ReadUInt16LittleEndian(shortEntry.AsSpan(20)) << 16;

      result.Add(new EntryGroup(bytes, name, isDirectory, startCluster, isPinned));
      pendingLfn.Clear();
    }
    return result;
  }

  private static void Rewrite(Span<byte> directory, List<EntryGroup> groups) {
    var pinned = groups.Where(g => g.IsPinned).ToArray();
    var movable = groups.Where(g => !g.IsPinned)
      .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
      .ThenBy(g => g.Name, StringComparer.Ordinal)
      .ToArray();

    directory.Clear();
    var cursor = 0;
    foreach (var group in pinned.Concat(movable)) {
      if (cursor + group.Bytes.Length > directory.Length)
        throw new InvalidDataException("Sorted FAT directory no longer fits its original allocation.");
      group.Bytes.AsSpan().CopyTo(directory[cursor..]);
      cursor += group.Bytes.Length;
    }
    // Clear() already supplied the required 0x00 end marker and prevents stale
    // copies of moved entries from remaining visible after the live region.
  }

  private static string ReadLongName(List<byte[]> entries) {
    var parts = new SortedDictionary<int, string>();
    foreach (var entry in entries) {
      var seq = entry[0] & 0x3F;
      var sb = new StringBuilder(13);
      AppendLfn(entry, 1, 5, sb);
      AppendLfn(entry, 14, 6, sb);
      AppendLfn(entry, 28, 2, sb);
      parts[seq] = sb.ToString();
    }
    return string.Concat(parts.Values).TrimEnd('\0', '\uFFFF');
  }

  private static void AppendLfn(ReadOnlySpan<byte> entry, int offset, int count, StringBuilder target) {
    for (var i = 0; i < count; ++i) {
      var value = BinaryPrimitives.ReadUInt16LittleEndian(entry[(offset + i * 2)..]);
      if (value is 0 or 0xFFFF) break;
      target.Append((char)value);
    }
  }

  private static string ReadShortName(ReadOnlySpan<byte> entry) {
    var name = Encoding.ASCII.GetString(entry[..8]).TrimEnd();
    var extension = Encoding.ASCII.GetString(entry.Slice(8, 3)).TrimEnd();
    if ((entry[12] & 0x08) != 0) name = name.ToLowerInvariant();
    if ((entry[12] & 0x10) != 0) extension = extension.ToLowerInvariant();
    return extension.Length == 0 ? name : $"{name}.{extension}";
  }

  private static List<int> ReadChain(Stream image, Layout layout, int firstCluster) {
    var result = new List<int>();
    var seen = new HashSet<int>();
    for (var cluster = firstCluster; cluster >= 2 && !IsEnd(cluster, layout.FatType) && seen.Add(cluster); cluster = ReadFat(image, layout, cluster))
      result.Add(cluster);
    return result;
  }

  private static int ReadFat(Stream image, Layout layout, int cluster) {
    image.Position = layout.FatOffset + (layout.FatType switch {
      12 => (long)cluster * 3 / 2,
      16 => (long)cluster * 2,
      _ => (long)cluster * 4,
    });
    Span<byte> value = stackalloc byte[4];
    var bytes = layout.FatType == 32 ? 4 : 2;
    image.ReadExactly(value[..bytes]);
    return layout.FatType switch {
      12 => (cluster & 1) == 0
        ? BinaryPrimitives.ReadUInt16LittleEndian(value) & 0x0FFF
        : BinaryPrimitives.ReadUInt16LittleEndian(value) >> 4,
      16 => BinaryPrimitives.ReadUInt16LittleEndian(value),
      _ => checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(value) & 0x0FFFFFFF)),
    };
  }

  private static bool IsEnd(int cluster, int fatType) => fatType switch {
    12 => cluster >= 0xFF8,
    16 => cluster >= 0xFFF8,
    _ => cluster >= 0x0FFFFFF8,
  };

  private static long ClusterOffset(Layout layout, int cluster)
    => ((long)layout.FirstDataSector + (long)(cluster - 2) * layout.SectorsPerCluster) * layout.BytesPerSector;
}
