using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;

namespace FileSystem.Fat;

/// <summary>
/// Opt-in FAT duplicate elimination. FAT has no native hard-link/link-count field,
/// so duplicate directory entries are marked read-only and deliberately point to
/// one canonical cluster chain. This is only safe together with shared-chain-aware
/// mutation/removal code; it is therefore advertised as ReadOnlySharedData rather
/// than <see cref="LayoutReclaim.HardLinks"/>.
/// </summary>
internal static class FatSharedDataOptimization {
  private sealed record SourceFile(string Name, string TempPath, long Length, string Digest);
  private sealed record SharedGroup(SourceFile Canonical, IReadOnlyList<SourceFile> Aliases);
  private readonly record struct Geometry(
    int BytesPerSector, int SectorsPerCluster, int ReservedSectors, int FatCount,
    int RootEntries, int FatSize, int FirstDataSector, int FatType, int RootCluster,
    int TotalClusters) {
    public int ClusterSize => this.BytesPerSector * this.SectorsPerCluster;
  }
  private readonly record struct DirEntry(long Offset, byte Attributes, int FirstCluster, uint Size);

  [ModuleInitializer]
  internal static void Register()
    => FilesystemOptimizationAdapters.RegisterHardLinkDeduplicator<FatFormatDescriptor>(
      HardLinkDeduplicationSemantics.ReadOnlySharedData,
      RebuildWithSharedData);

  private static void RebuildWithSharedData(
    ILayoutOptimizable layout,
    Stream source,
    Stream target,
    LayoutRebuildOptions options) {
    if (options.MakeSparse)
      throw new NotSupportedException("FAT shared-cluster deduplication cannot be combined with sparse-file conversion.");

    var descriptor = (FatFormatDescriptor)layout;
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb-fat-share-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    try {
      source.Position = 0;
      var entries = descriptor.List(source, null);
      var files = MaterializeAndHash(descriptor, source, entries, tempDir);
      var groups = FindDuplicateGroups(files);
      if (groups.Count == 0) {
        layout.RebuildStreaming(source, target, options);
        return;
      }

      var aliases = groups.SelectMany(g => g.Aliases).Select(a => a.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
      var emptyPlaceholder = Path.Combine(tempDir, "empty-placeholder.bin");
      File.WriteAllBytes(emptyPlaceholder, []);

      var inputs = new List<ArchiveInputInfo>();
      foreach (var entry in entries.Where(e => e.IsDirectory))
        inputs.Add(new ArchiveInputInfo("", entry.Name, true));
      foreach (var file in files)
        inputs.Add(new ArchiveInputInfo(
          aliases.Contains(file.Name) ? emptyPlaceholder : file.TempPath,
          file.Name,
          false));

      var createOptions = ToCreateOptions(options);
      target.Position = 0;
      target.SetLength(0);
      descriptor.Create(target, inputs, createOptions);
      target.Flush();

      PatchSharedDirectoryEntries(target, groups);
      Verify(target, files);
    } finally {
      try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
    }
  }

  private static FormatCreateOptions ToCreateOptions(LayoutRebuildOptions options) {
    var formatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (options.Parameters != null)
      foreach (var pair in options.Parameters)
        formatSpecific[pair.Key] = pair.Value;
    if (options.UnitSize > 0 && !formatSpecific.ContainsKey("ClusterSize"))
      formatSpecific["ClusterSize"] = FormatClusterSize(options.UnitSize);
    return new FormatCreateOptions { FormatSpecific = formatSpecific };
  }

  private static string FormatClusterSize(int bytes) => bytes switch {
    512 => "512 B",
    1024 => "1 KB",
    2048 => "2 KB",
    4096 => "4 KB",
    8192 => "8 KB",
    16384 => "16 KB",
    32768 => "32 KB",
    65536 => "64 KB",
    _ => bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
  };

  private static List<SourceFile> MaterializeAndHash(
    FatFormatDescriptor descriptor,
    Stream source,
    IReadOnlyList<ArchiveEntryInfo> entries,
    string tempDir) {
    var result = new List<SourceFile>();
    var index = 0;
    foreach (var entry in entries.Where(e => !e.IsDirectory)) {
      var temp = Path.Combine(tempDir, $"{index++:D8}.bin");
      source.Position = 0;
      using var input = descriptor.OpenEntry(source, entry.Name, null);
      using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      var buffer = new byte[64 * 1024];
      long length = 0;
      while (true) {
        var read = input.Read(buffer, 0, buffer.Length);
        if (read <= 0) break;
        output.Write(buffer, 0, read);
        hash.AppendData(buffer, 0, read);
        length += read;
      }
      output.Flush();
      result.Add(new SourceFile(entry.Name, temp, length, Convert.ToHexString(hash.GetHashAndReset())));
    }
    return result;
  }

  private static List<SharedGroup> FindDuplicateGroups(IReadOnlyList<SourceFile> files) {
    var result = new List<SharedGroup>();
    foreach (var digestGroup in files.Where(f => f.Length > 0)
               .GroupBy(f => (f.Length, f.Digest))) {
      var remaining = digestGroup.ToList();
      while (remaining.Count > 1) {
        var canonical = remaining[0];
        remaining.RemoveAt(0);
        var aliases = remaining.Where(f => FilesEqual(canonical.TempPath, f.TempPath)).ToList();
        if (aliases.Count == 0) continue;
        foreach (var alias in aliases) remaining.Remove(alias);
        result.Add(new SharedGroup(canonical, aliases));
      }
    }
    return result;
  }

  private static bool FilesEqual(string left, string right) {
    using var a = File.OpenRead(left);
    using var b = File.OpenRead(right);
    if (a.Length != b.Length) return false;
    Span<byte> ab = stackalloc byte[4096];
    Span<byte> bb = stackalloc byte[4096];
    while (true) {
      var ar = a.Read(ab);
      var br = b.Read(bb);
      if (ar != br) return false;
      if (ar == 0) return true;
      if (!ab[..ar].SequenceEqual(bb[..br])) return false;
    }
  }

  private static void PatchSharedDirectoryEntries(Stream image, IReadOnlyList<SharedGroup> groups) {
    var geometry = ReadGeometry(image);
    var entries = EnumerateDirectoryEntries(image, geometry);
    foreach (var group in groups) {
      if (!entries.TryGetValue(group.Canonical.Name, out var canonical))
        throw new InvalidDataException($"FAT rebuild lost canonical file '{group.Canonical.Name}'.");
      SetReadOnly(image, canonical.Offset);
      foreach (var aliasFile in group.Aliases) {
        if (!entries.TryGetValue(aliasFile.Name, out var alias))
          throw new InvalidDataException($"FAT rebuild lost duplicate file '{aliasFile.Name}'.");
        PatchAlias(image, alias.Offset, canonical);
      }
    }
    image.Flush();
  }

  private static void SetReadOnly(Stream image, long entryOffset) {
    image.Position = entryOffset + 11;
    var attributes = image.ReadByte();
    if (attributes < 0) throw new EndOfStreamException();
    image.Position = entryOffset + 11;
    image.WriteByte((byte)(attributes | 0x01));
  }

  private static void PatchAlias(Stream image, long entryOffset, DirEntry canonical) {
    Span<byte> entry = stackalloc byte[32];
    image.Position = entryOffset;
    image.ReadExactly(entry);
    entry[11] |= 0x01;
    BinaryPrimitives.WriteUInt16LittleEndian(entry[20..], (ushort)(canonical.FirstCluster >> 16));
    BinaryPrimitives.WriteUInt16LittleEndian(entry[26..], (ushort)canonical.FirstCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], canonical.Size);
    image.Position = entryOffset;
    image.Write(entry);
  }

  private static Geometry ReadGeometry(Stream image) {
    Span<byte> bpb = stackalloc byte[64];
    image.Position = 0;
    image.ReadExactly(bpb);
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(bpb[11..]);
    var spc = bpb[13];
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(bpb[14..]);
    var fatCount = bpb[16];
    var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(bpb[17..]);
    var total16 = BinaryPrimitives.ReadUInt16LittleEndian(bpb[19..]);
    var totalSectors = total16 != 0 ? total16 : BinaryPrimitives.ReadInt32LittleEndian(bpb[32..]);
    var fat16 = BinaryPrimitives.ReadUInt16LittleEndian(bpb[22..]);
    var fatSize = fat16 != 0 ? fat16 : BinaryPrimitives.ReadInt32LittleEndian(bpb[36..]);
    var rootDirSectors = (rootEntries * 32 + bps - 1) / bps;
    var firstData = reserved + fatCount * fatSize + rootDirSectors;
    var clusters = (totalSectors - firstData) / spc;
    var fatType = fat16 == 0 ? 32 : clusters < 4085 ? 12 : clusters < 65525 ? 16 : 32;
    var rootCluster = fatType == 32 ? BinaryPrimitives.ReadInt32LittleEndian(bpb[44..]) : 0;
    return new Geometry(bps, spc, reserved, fatCount, rootEntries, fatSize, firstData, fatType, rootCluster, clusters);
  }

  private static Dictionary<string, DirEntry> EnumerateDirectoryEntries(Stream image, Geometry geometry) {
    var result = new Dictionary<string, DirEntry>(StringComparer.OrdinalIgnoreCase);
    var seenDirectories = new HashSet<int>();
    if (geometry.FatType == 32)
      ScanDirectory(image, geometry, ChainSlotOffsets(image, geometry, geometry.RootCluster), "", result, seenDirectories);
    else {
      var rootOffset = (long)(geometry.ReservedSectors + geometry.FatCount * geometry.FatSize) * geometry.BytesPerSector;
      var offsets = Enumerable.Range(0, geometry.RootEntries).Select(i => rootOffset + i * 32L).ToArray();
      ScanDirectory(image, geometry, offsets, "", result, seenDirectories);
    }
    return result;
  }

  private static void ScanDirectory(
    Stream image,
    Geometry geometry,
    IReadOnlyList<long> offsets,
    string path,
    Dictionary<string, DirEntry> result,
    HashSet<int> seenDirectories) {
    var lfn = new SortedDictionary<int, string>();
    foreach (var offset in offsets) {
      Span<byte> entry = stackalloc byte[32];
      image.Position = offset;
      image.ReadExactly(entry);
      if (entry[0] == 0x00) break;
      if (entry[0] == 0xE5) { lfn.Clear(); continue; }
      var attr = entry[11];
      if ((attr & 0x3F) == 0x0F) {
        var part = new StringBuilder();
        ReadLfn(entry[1..], 5, part);
        ReadLfn(entry[14..], 6, part);
        ReadLfn(entry[28..], 2, part);
        lfn[entry[0] & 0x3F] = part.ToString();
        continue;
      }
      if ((attr & 0x08) != 0) { lfn.Clear(); continue; }

      var name = lfn.Count > 0
        ? string.Concat(lfn.Values).TrimEnd('\0', '\xFFFF')
        : DecodeShortName(entry);
      lfn.Clear();
      if (name is "." or "..") continue;
      var fullName = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
      var firstCluster = BinaryPrimitives.ReadUInt16LittleEndian(entry[26..]);
      if (geometry.FatType == 32)
        firstCluster |= BinaryPrimitives.ReadUInt16LittleEndian(entry[20..]) << 16;
      var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]);
      var dirEntry = new DirEntry(offset, attr, firstCluster, size);
      result[fullName] = dirEntry;
      if ((attr & 0x10) != 0 && firstCluster >= 2 && seenDirectories.Add(firstCluster))
        ScanDirectory(image, geometry, ChainSlotOffsets(image, geometry, firstCluster), fullName, result, seenDirectories);
    }
  }

  private static long[] ChainSlotOffsets(Stream image, Geometry geometry, int firstCluster) {
    var clusters = WalkChain(image, geometry, firstCluster);
    var slotsPerCluster = geometry.ClusterSize / 32;
    var result = new long[clusters.Count * slotsPerCluster];
    var index = 0;
    foreach (var cluster in clusters) {
      var clusterOffset = ((long)geometry.FirstDataSector + (long)(cluster - 2) * geometry.SectorsPerCluster) * geometry.BytesPerSector;
      for (var slot = 0; slot < slotsPerCluster; ++slot)
        result[index++] = clusterOffset + slot * 32L;
    }
    return result;
  }

  private static List<int> WalkChain(Stream image, Geometry geometry, int firstCluster) {
    var result = new List<int>();
    var seen = new HashSet<int>();
    var cluster = firstCluster;
    while (cluster >= 2 && cluster < geometry.TotalClusters + 2 && seen.Add(cluster)) {
      result.Add(cluster);
      var next = ReadFatEntry(image, geometry, cluster);
      if (IsEndOfChain(next, geometry.FatType)) break;
      cluster = next;
    }
    return result;
  }

  private static int ReadFatEntry(Stream image, Geometry geometry, int cluster) {
    var fatStart = (long)geometry.ReservedSectors * geometry.BytesPerSector;
    Span<byte> buffer = stackalloc byte[4];
    switch (geometry.FatType) {
      case 12: {
        image.Position = fatStart + cluster + cluster / 2L;
        image.ReadExactly(buffer[..2]);
        var raw = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        return (cluster & 1) != 0 ? raw >> 4 : raw & 0x0FFF;
      }
      case 16:
        image.Position = fatStart + cluster * 2L;
        image.ReadExactly(buffer[..2]);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
      default:
        image.Position = fatStart + cluster * 4L;
        image.ReadExactly(buffer);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer) & 0x0FFFFFFF;
    }
  }

  private static bool IsEndOfChain(int value, int fatType) => fatType switch {
    12 => value >= 0xFF8,
    16 => value >= 0xFFF8,
    _ => value >= 0x0FFFFFF8,
  };

  private static void ReadLfn(ReadOnlySpan<byte> span, int count, StringBuilder builder) {
    for (var i = 0; i < count; ++i) {
      var value = BinaryPrimitives.ReadUInt16LittleEndian(span[(i * 2)..]);
      if (value is 0 or 0xFFFF) break;
      builder.Append((char)value);
    }
  }

  private static string DecodeShortName(ReadOnlySpan<byte> entry) {
    var baseName = Encoding.ASCII.GetString(entry[..8]).TrimEnd(' ');
    var extension = Encoding.ASCII.GetString(entry[8..11]).TrimEnd(' ');
    if ((entry[12] & 0x08) != 0) baseName = baseName.ToLowerInvariant();
    if ((entry[12] & 0x10) != 0) extension = extension.ToLowerInvariant();
    return extension.Length == 0 ? baseName : $"{baseName}.{extension}";
  }

  private static void Verify(Stream image, IReadOnlyList<SourceFile> files) {
    image.Position = 0;
    using var reader = new FatReader(image, leaveOpen: true);
    var byName = reader.Entries.Where(e => !e.IsDirectory)
      .ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
    foreach (var source in files) {
      if (!byName.TryGetValue(source.Name, out var entry))
        throw new InvalidDataException($"FAT shared-data rebuild lost '{source.Name}'.");
      using var expected = File.OpenRead(source.TempPath);
      using var actual = reader.OpenChainStream(entry);
      if (!StreamsEqual(expected, actual, source.Length))
        throw new InvalidDataException($"FAT shared-data rebuild changed '{source.Name}'.");
    }
  }

  private static bool StreamsEqual(Stream expected, Stream actual, long length) {
    var a = new byte[64 * 1024];
    var b = new byte[64 * 1024];
    long compared = 0;
    while (compared < length) {
      var want = (int)Math.Min(a.Length, length - compared);
      var ar = expected.Read(a, 0, want);
      var br = actual.Read(b, 0, want);
      if (ar != br || ar <= 0) return false;
      if (!a.AsSpan(0, ar).SequenceEqual(b.AsSpan(0, br))) return false;
      compared += ar;
    }
    return expected.ReadByte() < 0;
  }
}
