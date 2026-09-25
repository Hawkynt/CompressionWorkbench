using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ExFat;

/// <summary>
/// Sorts exFAT File entry sets in place. A 0x85 primary and all secondaries
/// named by SecondaryCount move as one unit, so stream/name metadata and the
/// entry-set checksum remain byte-identical.
/// </summary>
internal static class ExFatDirectoryOrderer {
  private const uint EndOfChain = 0xFFFFFFFFu;

  public static void Sort(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("exFAT directory ordering requires a readable, writable, seekable stream.", nameof(image));

    var geometry = Geometry.Read(image);
    SortDirectory(image, geometry, geometry.RootDirectoryCluster, flags: 0, dataLength: -1, path: "", []);
    image.Flush();
  }

  private static void SortDirectory(
      Stream image,
      Geometry geometry,
      uint firstCluster,
      byte flags,
      long dataLength,
      string path,
      HashSet<uint> visited) {
    if (firstCluster < 2 || firstCluster > geometry.ClusterCount + 1 || !visited.Add(firstCluster))
      return;

    var clusters = geometry.ReadAllocation(image, firstCluster, flags, dataLength);
    var bytes = checked(clusters.Count * geometry.ClusterSize);
    var directory = new byte[bytes];
    for (var i = 0; i < clusters.Count; ++i) {
      image.Position = geometry.ClusterToOffset(clusters[i]);
      image.ReadExactly(directory.AsSpan(i * geometry.ClusterSize, geometry.ClusterSize));
    }

    var children = SortBuffer(directory, path);
    for (var i = 0; i < clusters.Count; ++i) {
      image.Position = geometry.ClusterToOffset(clusters[i]);
      image.Write(directory.AsSpan(i * geometry.ClusterSize, geometry.ClusterSize));
    }

    foreach (var child in children)
      SortDirectory(image, geometry, child.FirstCluster, child.Flags, child.DataLength, child.Path, visited);
  }

  private static IReadOnlyList<ChildDirectory> SortBuffer(byte[] directory, string path) {
    var pinned = new List<byte[]>();
    var files = new List<FileSet>();
    var inactive = new List<byte[]>();
    var children = new List<ChildDirectory>();
    var ordinal = 0;

    for (var offset = 0; offset + 32 <= directory.Length;) {
      var type = directory[offset];
      if (type == 0x00)
        break;

      if ((type & 0x80) == 0) {
        inactive.Add(directory.AsSpan(offset, 32).ToArray());
        offset += 32;
        continue;
      }

      if (type != 0x85) {
        pinned.Add(directory.AsSpan(offset, 32).ToArray());
        offset += 32;
        continue;
      }

      var secondaryCount = directory[offset + 1];
      if (secondaryCount < 2)
        throw new InvalidDataException($"exFAT file entry at byte {offset} has only {secondaryCount} secondary entries.");
      var setLength = checked((secondaryCount + 1) * 32);
      if (offset + setLength > directory.Length)
        throw new InvalidDataException($"exFAT file entry set at byte {offset} is truncated.");

      var raw = directory.AsSpan(offset, setLength).ToArray();
      var expectedChecksum = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(2, 2));
      var actualChecksum = ComputeEntrySetChecksum(raw);
      if (expectedChecksum != actualChecksum)
        throw new InvalidDataException(
          $"exFAT entry set at byte {offset} has checksum 0x{expectedChecksum:X4}; expected 0x{actualChecksum:X4}.");

      if (raw[32] != 0xC0)
        throw new InvalidDataException($"exFAT file entry set at byte {offset} has no leading Stream Extension.");

      var name = DecodeName(raw);
      var record = new FileSet(name, raw, ordinal++);
      files.Add(record);

      var attributes = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(4, 2));
      if ((attributes & 0x10) != 0) {
        var streamFlags = raw[33];
        var childCluster = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(52, 4));
        var childLengthRaw = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(56, 8));
        if (childLengthRaw > long.MaxValue)
          throw new NotSupportedException($"exFAT directory '{name}' exceeds the signed 64-bit length model.");
        if (childCluster >= 2) {
          var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
          children.Add(new ChildDirectory(childCluster, streamFlags, (long)childLengthRaw, fullPath));
        }
      }

      offset += setLength;
    }

    var ordered = files
      .OrderBy(static e => e.Name, StringComparer.OrdinalIgnoreCase)
      .ThenBy(static e => e.Name, StringComparer.Ordinal)
      .ThenBy(static e => e.Ordinal)
      .ToArray();

    var cursor = 0;
    foreach (var raw in pinned.Concat(ordered.Select(static e => e.Raw))) {
      if (cursor + raw.Length > directory.Length)
        throw new InvalidDataException("exFAT directory entry sets exceed their allocation.");
      raw.CopyTo(directory, cursor);
      cursor += raw.Length;
    }
    foreach (var raw in inactive) {
      if (cursor + raw.Length > directory.Length)
        break;
      raw.CopyTo(directory, cursor);
      cursor += raw.Length;
    }
    if (cursor < directory.Length)
      directory.AsSpan(cursor).Clear();

    return children;
  }

  private static string DecodeName(ReadOnlySpan<byte> entrySet) {
    var nameLength = entrySet[35];
    var nameEntries = (nameLength + 14) / 15;
    if (64 + nameEntries * 32 > entrySet.Length)
      throw new InvalidDataException("exFAT entry set does not contain enough File Name secondaries.");

    var result = new StringBuilder(nameLength);
    for (var n = 0; n < nameEntries; ++n) {
      var nameOffset = 64 + n * 32;
      if (entrySet[nameOffset] != 0xC1)
        throw new InvalidDataException("exFAT entry set contains a non-FileName secondary in its name sequence.");
      var count = Math.Min(15, nameLength - n * 15);
      for (var i = 0; i < count; ++i) {
        var value = (char)BinaryPrimitives.ReadUInt16LittleEndian(entrySet.Slice(nameOffset + 2 + i * 2, 2));
        if (value == '\0')
          throw new InvalidDataException("exFAT filename contains NUL inside the declared name length.");
        result.Append(value);
      }
    }
    return result.ToString();
  }

  private static ushort ComputeEntrySetChecksum(ReadOnlySpan<byte> set) {
    ushort checksum = 0;
    for (var i = 0; i < set.Length; ++i) {
      if (i is 2 or 3)
        continue;
      checksum = (ushort)((((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + set[i]) & 0xFFFF);
    }
    return checksum;
  }

  private sealed record FileSet(string Name, byte[] Raw, int Ordinal);
  private readonly record struct ChildDirectory(uint FirstCluster, byte Flags, long DataLength, string Path);

  private sealed record Geometry(
      int BytesPerSector,
      int SectorsPerCluster,
      long FatOffset,
      long FatLength,
      long ClusterHeapOffset,
      uint ClusterCount,
      uint RootDirectoryCluster) {
    public int ClusterSize => checked(this.BytesPerSector * this.SectorsPerCluster);

    public long ClusterToOffset(uint cluster)
      => checked(this.ClusterHeapOffset + (long)(cluster - 2) * this.ClusterSize);

    public List<uint> ReadAllocation(Stream image, uint firstCluster, byte flags, long dataLength) {
      var result = new List<uint>();
      if ((flags & 0x02) != 0 && dataLength >= 0) {
        var count = dataLength == 0 ? 0 : checked((dataLength + this.ClusterSize - 1) / this.ClusterSize);
        if ((ulong)firstCluster + (ulong)count > (ulong)this.ClusterCount + 2)
          throw new InvalidDataException("exFAT contiguous directory allocation extends beyond the cluster heap.");
        for (long i = 0; i < count; ++i)
          result.Add(firstCluster + checked((uint)i));
      } else {
        var seen = new HashSet<uint>();
        var cluster = firstCluster;
        while (true) {
          if (cluster < 2 || cluster > this.ClusterCount + 1 || !seen.Add(cluster))
            throw new InvalidDataException($"exFAT directory allocation contains invalid or cyclic cluster {cluster}.");
          result.Add(cluster);
          var next = this.ReadFatEntry(image, cluster);
          if (next == EndOfChain)
            break;
          cluster = next;
        }
      }

      if (result.Count == 0)
        throw new InvalidDataException("exFAT directory has no allocated clusters.");
      return result;
    }

    private uint ReadFatEntry(Stream image, uint cluster) {
      var offset = checked(this.FatOffset + (long)cluster * 4);
      if (offset < this.FatOffset || offset + 4 > this.FatOffset + this.FatLength)
        throw new InvalidDataException($"exFAT FAT entry {cluster} lies outside the declared FAT.");
      Span<byte> bytes = stackalloc byte[4];
      image.Position = offset;
      image.ReadExactly(bytes);
      return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    public static Geometry Read(Stream image) {
      if (image.Length < 512)
        throw new InvalidDataException("exFAT image is too small.");
      Span<byte> boot = stackalloc byte[512];
      image.Position = 0;
      image.ReadExactly(boot);
      if (!boot[3..11].SequenceEqual("EXFAT   "u8))
        throw new InvalidDataException("exFAT signature is missing.");

      var sectorShift = boot[108];
      var clusterShift = boot[109];
      if (sectorShift is < 9 or > 12 || clusterShift > 25 - sectorShift)
        throw new InvalidDataException("exFAT sector/cluster geometry is invalid.");
      var bytesPerSector = 1 << sectorShift;
      var sectorsPerCluster = 1 << clusterShift;
      var fatOffset = checked((long)BinaryPrimitives.ReadUInt32LittleEndian(boot[80..]) * bytesPerSector);
      var fatLength = checked((long)BinaryPrimitives.ReadUInt32LittleEndian(boot[84..]) * bytesPerSector);
      var heapOffset = checked((long)BinaryPrimitives.ReadUInt32LittleEndian(boot[88..]) * bytesPerSector);
      var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot[92..]);
      var root = BinaryPrimitives.ReadUInt32LittleEndian(boot[96..]);
      return new Geometry(bytesPerSector, sectorsPerCluster, fatOffset, fatLength, heapOffset, clusterCount, root);
    }
  }
}
