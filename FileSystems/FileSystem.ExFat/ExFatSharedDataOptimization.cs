using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;

namespace FileSystem.ExFat;

/// <summary>
/// Opt-in exFAT shared-cluster deduplication. exFAT has no native link count, so
/// duplicate files are marked read-only and their Stream Extension entries point to
/// the canonical file's FAT chain. The Allocation Bitmap remains correct because the
/// shared clusters are allocated once, not once per directory entry.
/// </summary>
internal static class ExFatSharedDataOptimization {
  private sealed record SourceFile(string Name, string TempPath, long Length, string Digest);
  private sealed record SharedGroup(SourceFile Canonical, IReadOnlyList<SourceFile> Aliases);
  private readonly record struct Layout(int ClusterSize, long FatOffset, long HeapOffset, uint ClusterCount, uint RootCluster);
  private sealed record EntryRecord(
    string Name,
    long[] Slots,
    bool IsDirectory,
    ushort Attributes,
    byte StreamFlags,
    uint FirstCluster,
    long DataLength);

  [ModuleInitializer]
  internal static void Register()
    => FilesystemOptimizationAdapters.RegisterHardLinkDeduplicator<ExFatFormatDescriptor>(
      HardLinkDeduplicationSemantics.ReadOnlySharedData,
      RebuildWithSharedData);

  private static void RebuildWithSharedData(
    ILayoutOptimizable layout,
    Stream source,
    Stream target,
    LayoutRebuildOptions options) {
    if (options.MakeSparse)
      throw new NotSupportedException("exFAT shared-cluster deduplication cannot be combined with sparse-file conversion.");

    var descriptor = (ExFatFormatDescriptor)layout;
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb-exfat-share-" + Guid.NewGuid().ToString("N"));
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

      foreach (var alias in groups.SelectMany(g => g.Aliases))
        File.WriteAllBytes(alias.TempPath, []);

      var inputs = new List<ArchiveInputInfo>();
      foreach (var entry in entries.Where(e => e.IsDirectory))
        inputs.Add(new ArchiveInputInfo("", entry.Name, true));
      foreach (var file in files)
        inputs.Add(new ArchiveInputInfo(file.TempPath, file.Name, false));

      target.Position = 0;
      target.SetLength(0);
      descriptor.Create(target, inputs, ToCreateOptions(options));
      target.Flush();

      PatchSharedDirectoryEntries(target, groups);
      Verify(descriptor, target, files);
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
    4096 => "4 KB",
    8192 => "8 KB",
    16384 => "16 KB",
    32768 => "32 KB",
    65536 => "64 KB",
    131072 => "128 KB",
    _ => bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
  };

  private static List<SourceFile> MaterializeAndHash(
    ExFatFormatDescriptor descriptor,
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
    foreach (var digestGroup in files.Where(f => f.Length > 0).GroupBy(f => (f.Length, f.Digest))) {
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
    var ab = new byte[64 * 1024];
    var bb = new byte[64 * 1024];
    while (true) {
      var ar = a.Read(ab, 0, ab.Length);
      var br = b.Read(bb, 0, bb.Length);
      if (ar != br) return false;
      if (ar == 0) return true;
      if (!ab.AsSpan(0, ar).SequenceEqual(bb.AsSpan(0, br))) return false;
    }
  }

  private static void PatchSharedDirectoryEntries(Stream image, IReadOnlyList<SharedGroup> groups) {
    var layout = ReadLayout(image);
    var entries = EnumerateEntries(image, layout);
    foreach (var group in groups) {
      if (!entries.TryGetValue(group.Canonical.Name, out var canonical))
        throw new InvalidDataException($"exFAT rebuild lost canonical file '{group.Canonical.Name}'.");
      SetReadOnlyAndChecksum(image, canonical);
      foreach (var aliasSource in group.Aliases) {
        if (!entries.TryGetValue(aliasSource.Name, out var alias))
          throw new InvalidDataException($"exFAT rebuild lost duplicate file '{aliasSource.Name}'.");
        PatchAlias(image, alias, canonical);
      }
    }
    image.Flush();
  }

  private static void SetReadOnlyAndChecksum(Stream image, EntryRecord record) {
    var set = ReadSet(image, record.Slots);
    var attributes = BinaryPrimitives.ReadUInt16LittleEndian(set.AsSpan(4));
    BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(4), (ushort)(attributes | 0x0001));
    StampChecksum(set);
    WriteSet(image, record.Slots, set);
  }

  private static void PatchAlias(Stream image, EntryRecord alias, EntryRecord canonical) {
    var set = ReadSet(image, alias.Slots);
    var attributes = BinaryPrimitives.ReadUInt16LittleEndian(set.AsSpan(4));
    BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(4), (ushort)(attributes | 0x0001));
    const int stream = 32;
    set[stream + 1] = canonical.StreamFlags;
    BinaryPrimitives.WriteInt64LittleEndian(set.AsSpan(stream + 8), canonical.DataLength);
    BinaryPrimitives.WriteUInt32LittleEndian(set.AsSpan(stream + 20), canonical.FirstCluster);
    BinaryPrimitives.WriteInt64LittleEndian(set.AsSpan(stream + 24), canonical.DataLength);
    StampChecksum(set);
    WriteSet(image, alias.Slots, set);
  }

  private static byte[] ReadSet(Stream image, IReadOnlyList<long> slots) {
    var result = new byte[slots.Count * 32];
    for (var i = 0; i < slots.Count; ++i) {
      image.Position = slots[i];
      image.ReadExactly(result.AsSpan(i * 32, 32));
    }
    return result;
  }

  private static void WriteSet(Stream image, IReadOnlyList<long> slots, ReadOnlySpan<byte> set) {
    for (var i = 0; i < slots.Count; ++i) {
      image.Position = slots[i];
      image.Write(set.Slice(i * 32, 32));
    }
  }

  private static void StampChecksum(Span<byte> set) {
    ushort checksum = 0;
    for (var i = 0; i < set.Length; ++i) {
      if (i is 2 or 3) continue;
      checksum = (ushort)((((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + set[i]) & 0xFFFF);
    }
    BinaryPrimitives.WriteUInt16LittleEndian(set[2..], checksum);
  }

  private static Layout ReadLayout(Stream image) {
    Span<byte> vbr = stackalloc byte[120];
    image.Position = 0;
    image.ReadExactly(vbr);
    if (!vbr[3..11].SequenceEqual("EXFAT   "u8))
      throw new InvalidDataException("exFAT: invalid signature.");
    var bytesPerSector = 1 << vbr[108];
    var clusterSize = bytesPerSector << vbr[109];
    var fatOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(vbr[80..]) * bytesPerSector;
    var heapOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(vbr[88..]) * bytesPerSector;
    var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(vbr[92..]);
    var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(vbr[96..]);
    return new Layout(clusterSize, fatOffset, heapOffset, clusterCount, rootCluster);
  }

  private static Dictionary<string, EntryRecord> EnumerateEntries(Stream image, Layout layout) {
    var result = new Dictionary<string, EntryRecord>(StringComparer.OrdinalIgnoreCase);
    ScanDirectory(image, layout, layout.RootCluster, "", result, new HashSet<uint>());
    return result;
  }

  private static void ScanDirectory(
    Stream image,
    Layout layout,
    uint firstCluster,
    string path,
    Dictionary<string, EntryRecord> result,
    HashSet<uint> seenDirectories) {
    if (firstCluster < 2 || !seenDirectories.Add(firstCluster)) return;
    var slots = ChainSlotOffsets(image, layout, firstCluster);
    for (var index = 0; index < slots.Count;) {
      Span<byte> primary = stackalloc byte[32];
      image.Position = slots[index];
      image.ReadExactly(primary);
      var type = primary[0];
      if (type == 0x00) break;
      if (type != 0x85) { ++index; continue; }
      var secondaryCount = primary[1];
      var count = 1 + secondaryCount;
      if (index + count > slots.Count) break;
      var setSlots = slots.Skip(index).Take(count).ToArray();
      var set = ReadSet(image, setSlots);
      if (set.Length < 64 || set[32] != 0xC0) { index += count; continue; }

      var attributes = BinaryPrimitives.ReadUInt16LittleEndian(set.AsSpan(4));
      var isDirectory = (attributes & 0x0010) != 0;
      var streamFlags = set[33];
      var nameLength = set[35];
      var first = BinaryPrimitives.ReadUInt32LittleEndian(set.AsSpan(52));
      var dataLength = BinaryPrimitives.ReadInt64LittleEndian(set.AsSpan(56));
      var name = DecodeName(set, nameLength);
      var fullName = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
      var record = new EntryRecord(fullName, setSlots, isDirectory, attributes, streamFlags, first, dataLength);
      result[fullName] = record;
      if (isDirectory && first >= 2)
        ScanDirectory(image, layout, first, fullName, result, seenDirectories);
      index += count;
    }
  }

  private static string DecodeName(ReadOnlySpan<byte> set, int nameLength) {
    var builder = new StringBuilder(nameLength);
    var remaining = nameLength;
    for (var offset = 64; offset + 32 <= set.Length && remaining > 0; offset += 32) {
      if (set[offset] != 0xC1) break;
      var count = Math.Min(15, remaining);
      for (var i = 0; i < count; ++i) {
        var ch = BinaryPrimitives.ReadUInt16LittleEndian(set[(offset + 2 + i * 2)..]);
        if (ch == 0) break;
        builder.Append((char)ch);
      }
      remaining -= count;
    }
    return builder.ToString();
  }

  private static List<long> ChainSlotOffsets(Stream image, Layout layout, uint firstCluster) {
    var clusters = WalkChain(image, layout, firstCluster);
    var slots = new List<long>(clusters.Count * (layout.ClusterSize / 32));
    foreach (var cluster in clusters) {
      var baseOffset = layout.HeapOffset + (long)(cluster - 2) * layout.ClusterSize;
      for (var offset = 0; offset < layout.ClusterSize; offset += 32)
        slots.Add(baseOffset + offset);
    }
    return slots;
  }

  private static List<uint> WalkChain(Stream image, Layout layout, uint firstCluster) {
    var result = new List<uint>();
    var seen = new HashSet<uint>();
    var cluster = firstCluster;
    Span<byte> value = stackalloc byte[4];
    while (cluster >= 2 && cluster <= layout.ClusterCount + 1 && seen.Add(cluster)) {
      result.Add(cluster);
      image.Position = layout.FatOffset + cluster * 4L;
      image.ReadExactly(value);
      var next = BinaryPrimitives.ReadUInt32LittleEndian(value);
      if (next >= 0xFFFFFFF8) break;
      cluster = next;
    }
    return result;
  }

  private static void Verify(ExFatFormatDescriptor descriptor, Stream image, IReadOnlyList<SourceFile> files) {
    foreach (var source in files) {
      image.Position = 0;
      using var actual = descriptor.OpenEntry(image, source.Name, null);
      using var expected = File.OpenRead(source.TempPath);
      if (!StreamsEqual(expected, actual, source.Length))
        throw new InvalidDataException($"exFAT shared-data rebuild changed '{source.Name}'.");
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
