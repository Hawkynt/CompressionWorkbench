using Compression.Registry;

namespace Compression.Core.DiskImage;

/// <summary>
/// Partition-aware archive surface for raw disk image streams (VHD/VHDX/VMDK/Qcow2/VDI
/// guest-disk views). When a valid MBR or GPT partition table is detected, presents
/// each partition as a top-level directory <c>PartitionN_TypeName/</c> whose contents
/// are the inner filesystem's entries (delegated through <see cref="InnerFsDetector"/>).
/// When no partition table is present, returns <c>null</c> so the caller can fall
/// through to the existing single-FS-at-offset-0 path.
/// </summary>
public static class PartitionedDiskLister {

  /// <summary>
  /// Lists entries across every partition. Returns <c>null</c> if no partition
  /// table is present so the caller can fall through to the unpartitioned path.
  /// </summary>
  public static List<ArchiveEntryInfo>? List(Stream disk, string? password) {
    var pt = PartitionTableDetector.Detect(disk);
    if (pt.Partitions.Count == 0) return null;

    var result = new List<ArchiveEntryInfo>();
    var idx = 0;
    foreach (var part in pt.Partitions) {
      var prefix = MakePartitionPrefix(part);

      // A BSD slice (MBR 0xA5/0xA6/0xA9 or GPT FreeBSD) holds a disklabel that
      // sub-divides it into filesystem partitions — expand those nested slices
      // instead of treating the whole slice as one opaque partition.
      if (TryListBsdSlices(disk, part, prefix, password, pt.Scheme, result, ref idx))
        continue;

      ListWindow(disk, part.StartOffset, part.Size, prefix, pt.Scheme, password, result, ref idx);
    }
    return result;
  }

  /// <summary>
  /// Lists a single partition window (inner-FS aware) under <paramref name="prefix"/>,
  /// falling back to a single raw <c>{prefix}.raw</c> blob entry when no inner
  /// filesystem is recognised.
  /// </summary>
  private static void ListWindow(Stream disk, long startOffset, long size, string prefix,
      string scheme, string? password, List<ArchiveEntryInfo> result, ref int idx) {
    using var window = new PartitionWindowStream(disk, startOffset, size);

    var inner = InnerFsDetector.Detect(window);
    if (inner is IArchiveFormatOperations ops) {
      try {
        window.Position = 0;
        foreach (var e in ops.List(window, password))
          result.Add(e with { Index = idx++, Name = $"{prefix}/{e.Name}" });
        return;
      } catch {
        // inner-FS read failed — fall through to raw partition entry
      }
    }

    result.Add(new ArchiveEntryInfo(
      idx++, $"{prefix}.raw", size, size, "Stored",
      IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: $"{scheme}-partition"));
  }

  /// <summary>
  /// When <paramref name="part"/> carries a BSD disklabel, lists each nested
  /// disklabel slice under <c>{prefix}/{sliceName}</c> and returns <c>true</c>;
  /// returns <c>false</c> when the partition is not a BSD-disklabel container.
  /// </summary>
  private static bool TryListBsdSlices(Stream disk, PartitionEntry part, string prefix,
      string? password, string scheme, List<ArchiveEntryInfo> result, ref int idx) {
    if (!TryReadBsdSlices(disk, part, out var slices))
      return false;

    foreach (var slice in slices) {
      var slicePrefix = $"{prefix}/{MakePartitionPrefix(slice)}";
      ListWindow(disk, slice.StartOffset, slice.Size, slicePrefix, scheme, password, result, ref idx);
    }
    return true;
  }

  /// <summary>
  /// Probes <paramref name="part"/> for a BSD disklabel and, if present, parses
  /// its slices as absolute parent-disk offsets. Returns <c>false</c> (and empty)
  /// when no usable disklabel is found.
  /// </summary>
  private static bool TryReadBsdSlices(Stream disk, PartitionEntry part, out List<PartitionEntry> slices) {
    slices = [];
    if (part.Size <= 0 || part.StartOffset < 0 || part.StartOffset >= disk.Length)
      return false;
    try {
      using var window = new PartitionWindowStream(disk, part.StartOffset, part.Size);
      if (!BsdDisklabelParser.IsDisklabel(window))
        return false;
      window.Position = 0;
      var parsed = BsdDisklabelParser.Parse(window, part.StartOffset);
      // Keep only slices that resolve to a real range on the parent disk.
      slices = parsed
        .Where(s => s.StartOffset >= 0 && s.StartOffset < disk.Length && s.Size > 0)
        .ToList();
      return slices.Count > 0;
    } catch {
      slices = [];
      return false;
    }
  }

  /// <summary>
  /// Extracts entries across every partition into per-partition subdirectories of
  /// <paramref name="outputDir"/>. Returns <c>true</c> if a partition table was
  /// detected and handled; <c>false</c> if no partition table was found (caller
  /// falls through to the unpartitioned path).
  /// </summary>
  public static bool Extract(Stream disk, string outputDir, string? password, string[]? files) {
    var pt = PartitionTableDetector.Detect(disk);
    if (pt.Partitions.Count == 0) return false;

    foreach (var part in pt.Partitions) {
      var prefix = MakePartitionPrefix(part);

      // BSD-disklabel container: extract each nested slice under its own subdir.
      if (TryReadBsdSlices(disk, part, out var slices)) {
        foreach (var slice in slices)
          ExtractWindow(disk, slice.StartOffset, slice.Size,
            $"{prefix}/{MakePartitionPrefix(slice)}", outputDir, password, files);
        continue;
      }

      ExtractWindow(disk, part.StartOffset, part.Size, prefix, outputDir, password, files);
    }
    return true;
  }

  /// <summary>
  /// Extracts one partition window (inner-FS aware) into <c>{outputDir}/{prefix}</c>,
  /// honouring the optional <paramref name="files"/> filter and falling back to a
  /// raw <c>raw.bin</c> dump when no inner filesystem is recognised.
  /// </summary>
  private static void ExtractWindow(Stream disk, long startOffset, long size, string prefix,
      string outputDir, string? password, string[]? files) {
    var partFilter = files?.Where(f => f.StartsWith(prefix + "/", StringComparison.Ordinal))
                            .Select(f => f[(prefix.Length + 1)..]).ToArray();
    // If a filter was supplied and nothing targets this partition, skip it entirely.
    if (files != null && (partFilter?.Length ?? 0) == 0 && !files.Contains($"{prefix}.raw")) return;

    using var window = new PartitionWindowStream(disk, startOffset, size);
    var partOut = Path.Combine(outputDir, prefix);

    var inner = InnerFsDetector.Detect(window);
    if (inner is IArchiveFormatOperations ops) {
      try {
        window.Position = 0;
        ops.Extract(window, partOut, password, partFilter);
        return;
      } catch {
        // fall through to raw dump
      }
    }

    // Unrecognised FS or read failure — dump the partition bytes raw.
    Directory.CreateDirectory(partOut);
    using var raw = File.Create(Path.Combine(partOut, "raw.bin"));
    window.Position = 0;
    window.CopyTo(raw);
  }

  private static string MakePartitionPrefix(PartitionEntry part) {
    var safeName = string.Concat(part.TypeName.Select(c =>
      char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
    if (string.IsNullOrEmpty(safeName)) safeName = "raw";
    return $"Partition{part.Index + 1}_{safeName}";
  }

  // ── Add / Remove (partition-aware) ────────────────────────────────────

  /// <summary>
  /// Partition-aware <see cref="IArchiveModifiable.Add"/>. Returns <c>true</c>
  /// when a partition table was present and at least one input was dispatched
  /// through the partition-aware path; <c>false</c> when no partition table
  /// exists so the caller can fall through to the existing single-FS path.
  /// </summary>
  /// <remarks>
  /// <para>Two input shapes are recognised:</para>
  /// <list type="bullet">
  ///   <item><description><c>Partition&lt;N&gt;_&lt;Type&gt;/&lt;inner&gt;</c> —
  ///     add the file to partition N's inner filesystem (delegated to its
  ///     <see cref="IArchiveModifiable"/>).</description></item>
  ///   <item><description>No prefix — treat the input bytes as a raw filesystem
  ///     image, find free space past the last existing partition, write a new
  ///     partition entry (FS type detected via <see cref="InnerFsDetector"/>),
  ///     and copy the bytes into the new partition window.</description></item>
  /// </list>
  /// </remarks>
  public static bool TryAdd(Stream disk, IReadOnlyList<ArchiveInputInfo> inputs) {
    if (!disk.CanWrite) return false;
    var detection = PartitionTableDetector.Detect(disk);
    if (detection.Partitions.Count == 0) return false;

    PartitionEditor? editor = null;
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var data = input.InMemoryContent ?? File.ReadAllBytes(input.FullPath);
      var name = input.ArchiveName.Replace('\\', '/');

      if (TryParsePartitionPrefix(name, out var partIdx, out var innerPath)
          && partIdx < detection.Partitions.Count) {
        var part = detection.Partitions[partIdx];
        using var window = new PartitionWindowStream(disk, part.StartOffset, part.Size);
        var innerDesc = InnerFsDetector.Detect(window);
        if (innerDesc is not IArchiveModifiable mod)
          throw new InvalidOperationException(
            $"Partition {partIdx + 1} ({part.TypeName}) has no writable filesystem reader; cannot Add '{innerPath}'.");
        window.Position = 0;
        mod.Add(window, new[] { ArchiveInputInfo.InMemory(innerPath, data) });
        continue;
      }

      // Root-level FS image drop → new partition past the last existing one.
      editor ??= new PartitionEditor(disk);
      var ptype = DetectInputPartitionType(data);
      var lengthAligned = AlignUp(data.LongLength, SectorSize);
      var startOffset = FindAppendOffset(editor.ListPartitions(), disk.Length, lengthAligned);
      if (startOffset < 0)
        throw new InvalidOperationException(
          $"Not enough free space at the end of the disk to land a {data.LongLength}-byte filesystem image.");
      editor.AddPartition(startOffset, lengthAligned, ptype, label: null);
      disk.Position = startOffset;
      disk.Write(data, 0, data.Length);
      // Pad the partition window with zeros to its sector-aligned length.
      var pad = lengthAligned - data.Length;
      if (pad > 0) {
        var zeros = new byte[512];
        var remaining = pad;
        while (remaining > 0) {
          var chunk = (int)Math.Min(remaining, zeros.Length);
          disk.Write(zeros, 0, chunk);
          remaining -= chunk;
        }
      }
    }
    return true;
  }

  /// <summary>
  /// Partition-aware <see cref="IArchiveModifiable.Remove"/>. Returns <c>true</c>
  /// when a partition table was present and at least one entry was removed;
  /// <c>false</c> if no partition table was detected. Entry name shapes:
  /// <c>Partition&lt;N&gt;_&lt;Type&gt;/&lt;inner&gt;</c> deletes an inner-FS
  /// file; <c>Partition&lt;N&gt;_&lt;Type&gt;</c> or
  /// <c>Partition&lt;N&gt;_&lt;Type&gt;.raw</c> deletes the whole partition.
  /// </summary>
  public static bool TryRemove(Stream disk, string[] entryNames) {
    if (!disk.CanWrite) return false;
    var detection = PartitionTableDetector.Detect(disk);
    if (detection.Partitions.Count == 0) return false;

    PartitionEditor? editor = null;
    foreach (var rawName in entryNames) {
      var name = rawName.Replace('\\', '/');

      if (TryParsePartitionPrefix(name, out var partIdx, out var innerPath)
          && partIdx < detection.Partitions.Count) {
        var part = detection.Partitions[partIdx];
        using var window = new PartitionWindowStream(disk, part.StartOffset, part.Size);
        var innerDesc = InnerFsDetector.Detect(window);
        if (innerDesc is IArchiveModifiable mod) {
          window.Position = 0;
          mod.Remove(window, new[] { innerPath });
        }
        continue;
      }

      // Whole-partition delete: "Partition3_FAT12" or "Partition3_FAT12.raw"
      var stripped = name.EndsWith(".raw", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
      if (TryParsePartitionIndex(stripped, out var idx) && idx < detection.Partitions.Count) {
        editor ??= new PartitionEditor(disk);
        editor.DeletePartition(idx);
      }
    }
    return true;
  }

  private static bool TryParsePartitionPrefix(string entryName, out int partitionIndex, out string innerPath) {
    partitionIndex = -1;
    innerPath = string.Empty;
    var slash = entryName.IndexOf('/');
    if (slash <= 0) return false;
    var head = entryName[..slash];
    if (!TryParsePartitionIndex(head, out partitionIndex)) return false;
    innerPath = entryName[(slash + 1)..];
    return innerPath.Length > 0;
  }

  private static bool TryParsePartitionIndex(string head, out int zeroBasedIndex) {
    zeroBasedIndex = -1;
    if (!head.StartsWith("Partition", StringComparison.Ordinal)) return false;
    var us = head.IndexOf('_');
    if (us <= 9) return false;
    if (!int.TryParse(head[9..us], out var oneBased) || oneBased <= 0) return false;
    zeroBasedIndex = oneBased - 1;
    return true;
  }

  private const int SectorSize = 512;

  private static long AlignUp(long value, int alignment)
    => (value + alignment - 1) / alignment * alignment;

  private static long FindAppendOffset(IReadOnlyList<PartitionEntry> existing, long diskLength, long lengthBytes) {
    long highest = SectorSize; // skip MBR
    foreach (var e in existing) {
      var end = e.StartOffset + e.Size;
      if (end > highest) highest = end;
    }
    highest = AlignUp(highest, SectorSize);
    return highest + lengthBytes <= diskLength ? highest : -1;
  }

  /// <summary>
  /// Maps the input bytes to a logical <see cref="PartitionType"/> by running
  /// <see cref="InnerFsDetector"/> over a wrapping memory stream and translating
  /// the resulting descriptor ID.
  /// </summary>
  private static PartitionType DetectInputPartitionType(byte[] data) {
    if (data.Length < 512) return PartitionType.Unknown;
    using var ms = new MemoryStream(data, writable: false);
    var desc = InnerFsDetector.Detect(ms);
    if (desc is null) return PartitionType.Unknown;
    return desc.Id switch {
      "Fat" => PartitionType.Fat32Lba,
      "ExFat" => PartitionType.NtfsExfat,
      "Ntfs" => PartitionType.NtfsExfat,
      "Hpfs" => PartitionType.NtfsExfat,
      "Ext" or "Ext2" or "Ext3" or "Ext4" => PartitionType.Linux,
      "Xfs" or "Btrfs" or "Jfs" or "ReiserFs" or "F2fs" => PartitionType.Linux,
      "HfsPlus" or "Hfs" or "Mfs" => PartitionType.AppleHfsPlus,
      "Apfs" => PartitionType.AppleApfs,
      "Ufs" => PartitionType.AppleUfs,
      _ => PartitionType.Linux, // safe MBR-mappable default for unknown POSIX-shaped images
    };
  }
}
