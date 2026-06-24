#pragma warning disable CS1591
using System.Buffers.Binary;
using FileSystem.Fat;

namespace FileSystem.FatPlus;

/// <summary>
/// In-place modification primitives for FAT+ filesystem images:
/// <see cref="AddFile"/> appends or replaces a file (via full rebuild — same
/// strategy <see cref="FatFormatDescriptor"/> uses for FAT), and
/// <see cref="RemoveFile"/> removes a file by freeing its cluster chain,
/// wiping its data, and marking its directory entries as deleted.
/// </summary>
/// <remarks>
/// <para>Add is rebuild-based because <see cref="FatPlusWriter"/> always packs
/// from cluster 2 onwards and the root directory is rewritten from scratch.
/// Remove is byte-level — it patches in-place exactly like
/// <see cref="FatRemover"/>, with two differences: (a) any rewrite of the
/// short-name dirent honours the FAT+ NTRes-high-6-bits convention (i.e. on
/// deletion the whole byte is zeroed via the standard 0xE5 sentinel path),
/// and (b) the BPB OEM signature is preserved so detection still recognises
/// the image as FAT+.</para>
/// </remarks>
public static class FatPlusModifier {

  /// <summary>
  /// Appends (or replaces by name) a file in an existing FAT+ image. The common
  /// case is a genuine in-place edit via <see cref="FatPlusInPlaceAdder"/>: free
  /// clusters are allocated, the data written into them, the chain linked in
  /// every FAT copy, a directory entry inserted, and the FAT+ extended-size bits
  /// patched — existing files, their clusters and the boot sector stay
  /// byte-identical and the image keeps its length. Structural cases the
  /// in-place path can't handle (nested target, full root directory,
  /// insufficient free space) fall back to the verified
  /// <see cref="FatPlusWriter"/> rebuild, which preserves the existing entries'
  /// extended sizes.
  /// </summary>
  public static void AddFile(Stream archive, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var original = ms.ToArray();

    // Try the genuine in-place edit on a copy; commit only if it succeeds so a
    // structural limit leaves the source untouched for the rebuild path.
    var work = (byte[])original.Clone();
    var inPlace = true;
    try {
      FatPlusInPlaceAdder.AddFile(work, name, data);
    } catch (Exception ex) when (ex is NotSupportedException or IOException
                                 or InvalidDataException or InvalidOperationException) {
      inPlace = false;
    }
    if (inPlace) {
      archive.Position = 0;
      archive.Write(work, 0, work.Length);
      archive.SetLength(work.Length);
      return;
    }

    // Fallback: verified rebuild from the untouched original. Preserves every
    // existing entry's declared extended size.
    using var src = new MemoryStream(original, writable: false);
    using var reader = new FatPlusReader(src, leaveOpen: true);
    var existing = reader.Entries.Where(e => !e.IsDirectory).ToList();
    var w = new FatPlusWriter();
    foreach (var entry in existing) {
      var payload = ExtractPayloadBounded(reader, entry);
      w.AddFile(entry.Name, payload, extendedSize: entry.Size);
    }
    w.AddFile(name, data);

    var totalSectors = (int)(original.Length / 512);
    var rebuilt = w.Build(totalSectors: totalSectors);
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  /// <summary>
  /// Removes the named file from a FAT+ image: walks the FAT chain, zeros every
  /// cluster the file occupies (including cluster-tip slack past the declared
  /// size), zeros the FAT entries in every FAT copy, and marks the directory
  /// entries (short + preceding LFN slots) as deleted (0xE5 sentinel + zero
  /// payload). Preserves the BPB OEM signature so detection still flags this
  /// as a FAT+ image afterwards.
  /// </summary>
  public static void RemoveFile(Stream archive, string name) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    RemoveInImage(image, name);

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  /// <summary>
  /// Reads the file's cluster-chain bytes, capped at <see cref="int.MaxValue"/>
  /// so the rebuild path stays within <see cref="byte"/>[] limits. Files with
  /// declared extended sizes above this cap still rebuild — the rebuilt image
  /// preserves the declared <see cref="FatPlusEntry.Size"/> but only the
  /// available on-disk bytes are re-written. Matches the contract of the
  /// FAT rebuild path.
  /// </summary>
  private static byte[] ExtractPayloadBounded(FatPlusReader reader, FatPlusEntry entry) {
    if (entry.IsDirectory || entry.StartCluster < 2 || entry.Size == 0)
      return [];

    if (entry.Size <= int.MaxValue)
      return reader.Extract(entry);

    // For oversized files: stream into a memory stream capped at int.MaxValue.
    using var sink = new MemoryStream();
    var capped = new CappedStream(sink, int.MaxValue);
    reader.ExtractTo(entry, capped);
    return sink.ToArray();
  }

  /// <summary>
  /// Wrapper that drops writes beyond a configured byte cap. Used by the
  /// rebuild path to keep the materialised payload within <see cref="byte"/>[]
  /// limits even when the source declares an extended size larger than 2 GiB.
  /// </summary>
  private sealed class CappedStream(Stream inner, long cap) : Stream {
    private long _written;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => this._written;
    public override long Position {
      get => this._written;
      set => throw new NotSupportedException();
    }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) {
      var remaining = cap - this._written;
      if (remaining <= 0) return;
      var take = count > remaining ? (int)remaining : count;
      inner.Write(buffer, offset, take);
      this._written += take;
    }
  }

  // ── Byte-level Remove implementation ───────────────────────────────────

  private static void RemoveInImage(byte[] image, string fileName) {
    // Boot-sector fields (same layout as standard FAT — the FAT+ extension lives
    // only in the dirent NTRes byte, not in the BPB).
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;
    var sectorsPerCluster = image[13] == 0 ? 1 : image[13];
    var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(14));
    var fatCount = image[16] == 0 ? 2 : image[16];
    var rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(17));
    var totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(19));
    var totalSectors = totalSectors16 == 0
      ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(32))
      : totalSectors16;
    var fatSize16 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(22));
    var fatSize = fatSize16 == 0
      ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(36))
      : fatSize16;
    var clusterSize = sectorsPerCluster * bytesPerSector;
    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    var totalDataClusters = (totalSectors - firstDataSector) / sectorsPerCluster;
    var fatType = totalDataClusters < 4085 ? 12 : totalDataClusters < 65525 ? 16 : 32;

    var rootDirOffset = fatType == 32
      ? (firstDataSector + (BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(44)) - 2) * sectorsPerCluster) * bytesPerSector
      : (reservedSectors + fatCount * fatSize) * bytesPerSector;
    var rootDirCapacity = fatType == 32 ? clusterSize : rootEntryCount * 32;

    var (entryIndex, firstLfnIndex) = FindEntry(image, rootDirOffset, rootDirCapacity, fileName);
    if (entryIndex < 0)
      throw new FileNotFoundException($"File '{fileName}' not found in FAT+ root directory.");

    var entryOffset = rootDirOffset + entryIndex * 32;
    var firstClusterLow = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(entryOffset + 26));
    var firstClusterHigh = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(entryOffset + 20));
    var firstCluster = (firstClusterHigh << 16) | firstClusterLow;

    var chain = WalkChain(image, firstCluster, reservedSectors, bytesPerSector, fatType, totalDataClusters);

    foreach (var cluster in chain) {
      var dataOffset = (firstDataSector + (cluster - 2) * sectorsPerCluster) * bytesPerSector;
      if (dataOffset + clusterSize <= image.Length)
        image.AsSpan(dataOffset, clusterSize).Clear();
    }

    for (var fatIdx = 0; fatIdx < fatCount; ++fatIdx) {
      var fatStart = (reservedSectors + fatIdx * fatSize) * bytesPerSector;
      foreach (var cluster in chain)
        ClearFatEntry(image, fatStart, cluster, fatType);
    }

    // Zero every byte of the dirent (including NTRes high bits — this entry no
    // longer represents a FAT+ file), then re-stamp 0xE5 in byte 0 so the
    // root-dir walker doesn't truncate at this slot.
    var from = firstLfnIndex >= 0 ? firstLfnIndex : entryIndex;
    for (var i = from; i <= entryIndex; ++i) {
      var off = rootDirOffset + i * 32;
      image.AsSpan(off, 32).Clear();
      image[off] = 0xE5;
    }
  }

  private static (int EntryIndex, int FirstLfnIndex) FindEntry(
      byte[] image, int rootOffset, int capacity, string fileName) {
    var maxEntries = capacity / 32;
    var firstLfn = -1;
    var lfnAccumulator = new SortedDictionary<int, string>();

    for (var i = 0; i < maxEntries; ++i) {
      var off = rootOffset + i * 32;
      var first = image[off];
      if (first == 0x00) break;
      if (first == 0xE5) { firstLfn = -1; lfnAccumulator.Clear(); continue; }

      var attr = image[off + 11];
      if ((attr & 0x3F) == 0x0F) {
        if (firstLfn < 0) firstLfn = i;
        var seq = first & 0x3F;
        lfnAccumulator[seq] = ReadLfn(image, off);
        continue;
      }

      if ((attr & 0x08) != 0) { firstLfn = -1; lfnAccumulator.Clear(); continue; }

      // Try LFN-assembled name first, fall back to 8.3 short name.
      string candidateName;
      if (lfnAccumulator.Count > 0) {
        var sb = new System.Text.StringBuilder();
        foreach (var p in lfnAccumulator.Values) sb.Append(p);
        candidateName = sb.ToString().TrimEnd('\0', '\xFFFF');
      } else {
        candidateName = DecodeShortName(image.AsSpan(off, 11));
      }

      if (candidateName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
        return (i, firstLfn);

      firstLfn = -1;
      lfnAccumulator.Clear();
    }
    return (-1, -1);
  }

  private static string ReadLfn(byte[] image, int off) {
    var sb = new System.Text.StringBuilder();
    AppendLfn(image, off + 1, 5, sb);
    AppendLfn(image, off + 14, 6, sb);
    AppendLfn(image, off + 28, 2, sb);
    return sb.ToString();
  }

  private static void AppendLfn(byte[] image, int offset, int count, System.Text.StringBuilder sb) {
    for (var j = 0; j < count; ++j) {
      var charOff = offset + j * 2;
      if (charOff + 2 > image.Length) break;
      var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(charOff));
      if (c == 0 || c == 0xFFFF) break;
      sb.Append(c);
    }
  }

  private static string DecodeShortName(ReadOnlySpan<byte> entry) {
    var baseName = System.Text.Encoding.ASCII.GetString(entry[..8]).TrimEnd(' ');
    var ext = System.Text.Encoding.ASCII.GetString(entry[8..11]).TrimEnd(' ');
    return ext.Length == 0 ? baseName : $"{baseName}.{ext}";
  }

  private static List<int> WalkChain(byte[] image, int startCluster,
      int reservedSectors, int bytesPerSector, int fatType, int totalDataClusters) {
    var chain = new List<int>();
    var cluster = startCluster;
    var fatStart = reservedSectors * bytesPerSector;
    var seen = new HashSet<int>();
    while (cluster >= 2 && cluster < totalDataClusters + 2 && seen.Add(cluster)) {
      chain.Add(cluster);
      cluster = ReadFatEntry(image, fatStart, cluster, fatType);
      if (IsEndOfChain(cluster, fatType)) break;
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
