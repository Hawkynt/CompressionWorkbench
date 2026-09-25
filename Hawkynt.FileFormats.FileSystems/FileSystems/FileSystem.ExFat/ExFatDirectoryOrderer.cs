using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ExFat;

/// <summary>
/// In-place exFAT directory-entry-set sorter. File data, FAT chains, allocation
/// bitmap bits, timestamps and attributes are left untouched.
/// </summary>
public static class ExFatDirectoryOrderer {
  private readonly record struct Layout(
    int ClusterSize,
    long FatOffset,
    long ClusterHeapOffset,
    uint ClusterCount,
    uint RootDirCluster
  );

  private sealed record EntrySet(
    byte[] Bytes,
    string Name,
    bool IsDirectory,
    uint FirstCluster,
    byte GeneralSecondaryFlags,
    long DataLength,
    bool IsPinned
  );

  public static void Sort(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("exFAT directory sorting requires a readable, writable, seekable stream.", nameof(image));

    var layout = ReadLayout(image);
    var visited = new HashSet<uint>();
    SortDirectory(image, layout, layout.RootDirCluster, flags: 0, dataLength: -1, visited);
    image.Flush();
  }

  private static Layout ReadLayout(Stream image) {
    Span<byte> header = stackalloc byte[120];
    image.Position = 0;
    image.ReadExactly(header);
    if (Encoding.ASCII.GetString(header.Slice(3, 8)) != "EXFAT   ")
      throw new InvalidDataException("exFAT: invalid signature.");

    var bytesPerSector = 1 << header[108];
    var sectorsPerCluster = 1 << header[109];
    var fatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
    var heapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(header[88..]);
    return new Layout(
      checked(bytesPerSector * sectorsPerCluster),
      checked((long)fatOffsetSectors * bytesPerSector),
      checked((long)heapOffsetSectors * bytesPerSector),
      BinaryPrimitives.ReadUInt32LittleEndian(header[92..]),
      BinaryPrimitives.ReadUInt32LittleEndian(header[96..]));
  }

  private static void SortDirectory(
      Stream image,
      Layout layout,
      uint firstCluster,
      byte flags,
      long dataLength,
      HashSet<uint> visited) {
    if (firstCluster < 2 || firstCluster > layout.ClusterCount + 1 || !visited.Add(firstCluster))
      return;

    var allocation = ResolveAllocation(image, layout, firstCluster, flags, dataLength);
    if (allocation.Count == 0) return;

    var byteLength = dataLength > 0
      ? checked((int)Math.Min(dataLength, checked((long)allocation.Count * layout.ClusterSize)))
      : checked(allocation.Count * layout.ClusterSize);
    var data = new byte[byteLength];
    var copied = 0;
    foreach (var cluster in allocation) {
      if (copied >= data.Length) break;
      var take = Math.Min(layout.ClusterSize, data.Length - copied);
      image.Position = ClusterOffset(layout, cluster);
      image.ReadExactly(data.AsSpan(copied, take));
      copied += take;
    }

    var sets = ParseEntrySets(data);
    foreach (var set in sets)
      if (set.IsDirectory && !set.IsPinned && set.FirstCluster >= 2)
        SortDirectory(image, layout, set.FirstCluster, set.GeneralSecondaryFlags, set.DataLength, visited);

    Rewrite(data, sets);

    copied = 0;
    foreach (var cluster in allocation) {
      if (copied >= data.Length) break;
      var take = Math.Min(layout.ClusterSize, data.Length - copied);
      image.Position = ClusterOffset(layout, cluster);
      image.Write(data.AsSpan(copied, take));
      copied += take;
    }
  }

  private static List<EntrySet> ParseEntrySets(byte[] data) {
    var result = new List<EntrySet>();
    for (var offset = 0; offset + 32 <= data.Length;) {
      var type = data[offset];
      if (type == 0x00) break;

      if (type != 0x85) {
        var pinned = data.AsSpan(offset, 32).ToArray();
        result.Add(new EntrySet(pinned, "", false, 0, 0, 0, true));
        offset += 32;
        continue;
      }

      var secondaryCount = data[offset + 1];
      var byteCount = checked((secondaryCount + 1) * 32);
      if (secondaryCount < 2 || offset + byteCount > data.Length)
        throw new InvalidDataException("exFAT directory contains a truncated file entry set.");

      var bytes = data.AsSpan(offset, byteCount).ToArray();
      if (bytes[32] != 0xC0)
        throw new InvalidDataException("exFAT file entry set has no Stream Extension.");

      var attributes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
      var nameLength = bytes[35];
      var name = ReadName(bytes, nameLength);
      var flags = bytes[33];
      var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(52));
      var lengthRaw = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(56));
      if (lengthRaw > long.MaxValue)
        throw new NotSupportedException("exFAT directory length exceeds the signed 64-bit contract.");

      result.Add(new EntrySet(
        bytes,
        name,
        (attributes & 0x10) != 0,
        firstCluster,
        flags,
        (long)lengthRaw,
        false));
      offset += byteCount;
    }
    return result;
  }

  private static string ReadName(ReadOnlySpan<byte> set, int nameLength) {
    var result = new StringBuilder(nameLength);
    var needed = (nameLength + 14) / 15;
    for (var n = 0; n < needed; ++n) {
      var offset = 64 + n * 32;
      if (offset + 32 > set.Length || set[offset] != 0xC1)
        throw new InvalidDataException("exFAT file entry set has a malformed File Name sequence.");
      var chars = Math.Min(15, nameLength - n * 15);
      for (var i = 0; i < chars; ++i)
        result.Append((char)BinaryPrimitives.ReadUInt16LittleEndian(set[(offset + 2 + i * 2)..]));
    }
    return result.ToString();
  }

  private static void Rewrite(Span<byte> directory, List<EntrySet> sets) {
    var pinned = sets.Where(set => set.IsPinned).ToArray();
    var movable = sets.Where(set => !set.IsPinned)
      .OrderBy(set => set.Name, StringComparer.OrdinalIgnoreCase)
      .ThenBy(set => set.Name, StringComparer.Ordinal)
      .ToArray();

    directory.Clear();
    var cursor = 0;
    foreach (var set in pinned.Concat(movable)) {
      if (cursor + set.Bytes.Length > directory.Length)
        throw new InvalidDataException("Sorted exFAT directory no longer fits its original allocation.");
      set.Bytes.AsSpan().CopyTo(directory[cursor..]);
      cursor += set.Bytes.Length;
    }
  }

  private static List<uint> ResolveAllocation(
      Stream image,
      Layout layout,
      uint firstCluster,
      byte flags,
      long dataLength) {
    if ((flags & 0x02) != 0 && dataLength >= 0) {
      var count = dataLength == 0 ? 0 : checked((int)((dataLength + layout.ClusterSize - 1) / layout.ClusterSize));
      var result = new List<uint>(count);
      for (var i = 0; i < count; ++i) {
        var candidate = firstCluster + (uint)i;
        if (candidate > layout.ClusterCount + 1)
          throw new InvalidDataException("exFAT contiguous directory allocation exceeds the cluster heap.");
        result.Add(candidate);
      }
      return result;
    }

    var chain = new List<uint>();
    var seen = new HashSet<uint>();
    var cluster = firstCluster;
    while (cluster >= 2 && cluster <= layout.ClusterCount + 1) {
      if (!seen.Add(cluster))
        throw new InvalidDataException("exFAT directory FAT chain contains a cycle.");
      chain.Add(cluster);
      var next = ReadFat(image, layout, cluster);
      if (next >= 0xFFFFFFF8) return chain;
      if (next < 2 || next > layout.ClusterCount + 1)
        throw new InvalidDataException("exFAT directory FAT chain references an invalid cluster.");
      cluster = next;
    }
    return chain;
  }

  private static uint ReadFat(Stream image, Layout layout, uint cluster) {
    Span<byte> value = stackalloc byte[4];
    image.Position = layout.FatOffset + (long)cluster * 4;
    image.ReadExactly(value);
    return BinaryPrimitives.ReadUInt32LittleEndian(value);
  }

  private static long ClusterOffset(Layout layout, uint cluster)
    => layout.ClusterHeapOffset + (long)(cluster - 2) * layout.ClusterSize;
}
