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
    // A slice of a disklabel can start at the label itself; it does not nest a second time.
    if (part.Source == "BSD" || part.Size <= 0 || part.StartOffset < 0 || part.StartOffset >= disk.Length)
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
  /// raw <c>{prefix}.raw</c> dump, named as List reports it, when no inner filesystem is recognised.
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

    // Unrecognised FS or read failure — dump the partition bytes raw, under the same
    // "{prefix}.raw" name List reports, so extracting a listed entry finds its file.
    var rawPath = Path.Combine(outputDir, prefix.Replace('/', Path.DirectorySeparatorChar) + ".raw");
    Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);
    using var raw = File.Create(rawPath);
    window.Position = 0;
    window.CopyTo(raw);
  }

  /// <summary>
  /// The directory name a partition is listed under: <c>Partition{N}_{TypeName}</c>, with N
  /// one-based and the type name reduced to letters, digits, '-' and '_'.
  /// </summary>
  public static string MakePartitionPrefix(PartitionEntry part) {
    var safeName = string.Concat(part.TypeName.Select(c =>
      char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
    if (string.IsNullOrEmpty(safeName)) safeName = "raw";
    return $"Partition{part.Index + 1}_{safeName}";
  }

  // ── Partition addressing ──────────────────────────────────────────────

  /// <summary>
  /// One listed partition: a top-level partition, or a slice of a BSD disklabel nested inside
  /// one, with the prefix its entries are listed under.
  /// </summary>
  /// <param name="Partition">The table entry (for a BSD slice, the slice's own entry).</param>
  /// <param name="Prefix">The listing prefix, e.g. <c>Partition1_FAT32</c> or <c>Partition1_FreeBSD/Partition1_4_2BSD</c>.</param>
  /// <param name="Offset">Byte offset of the partition on the disk.</param>
  /// <param name="Size">Bytes of the partition that lie inside the image.</param>
  /// <param name="IsBsdSlice">Whether this is a slice inside a BSD disklabel rather than a table entry.</param>
  public sealed record PartitionTarget(PartitionEntry Partition, string Prefix, long Offset, long Size, bool IsBsdSlice);

  /// <summary>
  /// Every partition the listing shows a prefix for, in listing order: top-level partitions,
  /// with a BSD-disklabel container replaced by its slices.
  /// </summary>
  public static List<PartitionTarget> EnumerateTargets(Stream disk, PartitionTableDetector.DetectionResult detection) {
    var result = new List<PartitionTarget>();
    foreach (var part in detection.Partitions) {
      var prefix = MakePartitionPrefix(part);
      if (TryReadBsdSlices(disk, part, out var slices)) {
        foreach (var slice in slices)
          result.Add(new PartitionTarget(slice, $"{prefix}/{MakePartitionPrefix(slice)}", slice.StartOffset,
            Clamp(disk, slice), IsBsdSlice: true));
        continue;
      }
      result.Add(new PartitionTarget(part, prefix, part.StartOffset, Clamp(disk, part), IsBsdSlice: false));
    }
    return result;
  }

  private static long Clamp(Stream disk, PartitionEntry part)
    => Math.Max(0, Math.Min(part.Size, disk.Length - part.StartOffset));

  /// <summary>
  /// Finds the partition an entry name addresses. <paramref name="innerPath"/> is the rest of the
  /// name below the partition's prefix — empty when the name is the partition itself
  /// (<c>PartitionN_Type</c> or <c>PartitionN_Type.raw</c>).
  /// </summary>
  public static PartitionTarget? Resolve(IEnumerable<PartitionTarget> targets, string entryName, out string innerPath) {
    var name = entryName.Replace('\\', '/').Trim('/');
    innerPath = string.Empty;
    PartitionTarget? best = null;
    foreach (var t in targets) {
      if (name.Equals(t.Prefix, StringComparison.Ordinal) || name.Equals(t.Prefix + ".raw", StringComparison.Ordinal)) {
        innerPath = string.Empty;
        return t;
      }
      if (name.StartsWith(t.Prefix + "/", StringComparison.Ordinal) && (best is null || t.Prefix.Length > best.Prefix.Length))
        best = t;
    }
    if (best is not null) innerPath = name[(best.Prefix.Length + 1)..];
    return best;
  }

  /// <summary>
  /// Whether <paramref name="modifier"/> edits through its own code for <paramref name="method"/>
  /// (<c>Add</c> or <c>Remove</c>) rather than the interface's generic extract → re-create
  /// default, which re-creates the volume and loses what its writer does not reproduce.
  /// </summary>
  public static bool HasNativeEditor(IArchiveModifiable modifier, string method) {
    var map = modifier.GetType().GetInterfaceMap(typeof(IArchiveModifiable));
    for (var i = 0; i < map.InterfaceMethods.Length; ++i) {
      var m = map.InterfaceMethods[i];
      if (m.Name != method || m.GetParameters().Length != 2) continue;
      return map.TargetMethods[i].DeclaringType != typeof(IArchiveModifiable);
    }
    return false;
  }

  // ── Add / Remove (partition-aware) ────────────────────────────────────

  /// <summary>
  /// Partition-aware <see cref="IArchiveModifiable.Add(System.IO.Stream, System.Collections.Generic.IReadOnlyList{Compression.Registry.ArchiveInputInfo})"/>.
  /// Returns <c>false</c> when no partition table exists (or the stream is read-only) so the
  /// caller can fall through to its single-filesystem path.
  /// </summary>
  /// <remarks>
  /// <para>Two input shapes are recognised:</para>
  /// <list type="bullet">
  ///   <item><description><c>PartitionN_Type/inner/path</c> — adds the file to that partition's
  ///     filesystem through the filesystem's own in-place editor, working on a staged window over
  ///     the partition: it can neither write outside the partition nor grow past it.</description></item>
  ///   <item><description>A root-level filesystem image (recognised by content, or named
  ///     <c>*.raw</c>/<c>*.img</c>/<c>PartitionN_…</c>) — becomes a new partition in the free
  ///     space after the last one, 1 MiB-aligned where room allows, inside the table's usable
  ///     range. GPT and MBR tables only.</description></item>
  /// </list>
  /// <para>
  /// Every edit is planned and staged before anything is written; a refusal — a filesystem with
  /// no lossless in-place editor, a full volume, no free space, a full table — throws
  /// <see cref="InvalidOperationException"/> and leaves the disk byte-identical.
  /// </para>
  /// </remarks>
  public static bool TryAdd(Stream disk, IReadOnlyList<ArchiveInputInfo> inputs) {
    if (!disk.CanWrite) return false;
    var detection = PartitionTableDetector.Detect(disk);
    if (detection.Partitions.Count == 0) return false;
    var targets = EnumerateTargets(disk, detection);

    var innerAdds = new Dictionary<PartitionTarget, List<ArchiveInputInfo>>();
    var newPartitions = new List<ArchiveInputInfo>();
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      var target = Resolve(targets, input.ArchiveName, out var innerPath);
      if (target is not null && innerPath.Length > 0) {
        if (!innerAdds.TryGetValue(target, out var list)) innerAdds[target] = list = [];
        list.Add(input with { ArchiveName = innerPath });
        continue;
      }
      if (target is not null)
        throw new InvalidOperationException(
          $"'{input.ArchiveName}' names an existing partition; remove it first to replace it.");
      if (input.ArchiveName.Replace('\\', '/').Trim('/').Contains('/'))
        throw new InvalidOperationException(
          $"'{input.ArchiveName}' is not inside any partition of this disk.");
      newPartitions.Add(input);
    }

    // Stage every filesystem edit first: nothing reaches the disk until all of them succeed.
    var staged = innerAdds.Select(kv => StageInnerEdit(disk, kv.Key, "Add",
      (mod, window) => mod.Add(window, kv.Value))).ToList();
    var plan = newPartitions.Count > 0 ? PlanNewPartitions(disk, detection, newPartitions) : [];

    foreach (var window in staged) window.Commit();
    if (plan.Count > 0) {
      var editor = new PartitionEditor(disk, detection.SectorSize);
      foreach (var (input, start, length, type, label) in plan) {
        WritePartitionData(disk, input, start, length);
        editor.AddPartition(start, length, type, label);
      }
    }
    disk.Flush();
    return true;
  }

  /// <summary>
  /// Partition-aware <see cref="IArchiveModifiable.Remove(System.IO.Stream, string[])"/>. Returns
  /// <c>false</c> when no partition table exists (or the stream is read-only). Entry name shapes:
  /// <c>PartitionN_Type/inner</c> removes a file — or a folder with everything beneath it —
  /// through the partition filesystem's own in-place editor; <c>PartitionN_Type</c> or
  /// <c>PartitionN_Type.raw</c> deletes the partition's table entry (GPT and MBR, including
  /// logical partitions), leaving its sectors as they were.
  /// </summary>
  /// <remarks>
  /// A name that addresses nothing, a filesystem without a lossless in-place editor, or a
  /// whole-partition delete on a scheme the editor cannot rewrite is refused with the disk left
  /// byte-identical.
  /// </remarks>
  public static bool TryRemove(Stream disk, string[] entryNames) {
    if (!disk.CanWrite) return false;
    var detection = PartitionTableDetector.Detect(disk);
    if (detection.Partitions.Count == 0) return false;
    var targets = EnumerateTargets(disk, detection);

    var innerRemoves = new Dictionary<PartitionTarget, List<string>>();
    var deletions = new List<PartitionTarget>();
    foreach (var rawName in entryNames ?? []) {
      var target = Resolve(targets, rawName, out var innerPath)
        ?? throw new FileNotFoundException($"'{rawName}' is not on this disk.", rawName);
      if (innerPath.Length > 0) {
        if (!innerRemoves.TryGetValue(target, out var list)) innerRemoves[target] = list = [];
        list.Add(innerPath);
        continue;
      }
      if (detection.Scheme is not ("GPT" or "MBR") || target.IsBsdSlice)
        throw new InvalidOperationException(
          $"Deleting '{target.Prefix}' would need a {(target.IsBsdSlice ? "BSD disklabel" : detection.Scheme)} table writer; only GPT and MBR entries can be deleted.");
      if (!deletions.Contains(target)) deletions.Add(target);
    }

    var staged = innerRemoves.Select(kv => StageInnerEdit(disk, kv.Key, "Remove",
      (mod, window) => mod.Remove(window, [.. kv.Value]))).ToList();

    PartitionEditor? editor = null;
    var editorIndices = new List<int>();
    if (deletions.Count > 0) {
      editor = new PartitionEditor(disk, detection.SectorSize);
      foreach (var target in deletions) {
        var entry = editor.ListPartitions().FirstOrDefault(e =>
          e.StartOffset == target.Partition.StartOffset && e.Size == target.Partition.Size
          && e.Source != "MBR (Extended Container)")
          ?? throw new InvalidOperationException($"'{target.Prefix}' has no entry the partition editor can delete.");
        editorIndices.Add(entry.Index);
      }
    }

    foreach (var window in staged) window.Commit();
    if (editor is not null)
      foreach (var index in editorIndices.OrderDescending())
        editor.DeletePartition(index);
    disk.Flush();
    return true;
  }

  /// <summary>
  /// Runs a filesystem edit against a staged window over <paramref name="target"/>. The edit
  /// must go through the filesystem's own in-place editor; one that would only re-create the
  /// volume is refused, as is one that throws. Nothing is written until the caller commits.
  /// </summary>
  private static StagedWindowStream StageInnerEdit(Stream disk, PartitionTarget target, string method,
      Action<IArchiveModifiable, Stream> edit) {
    var window = new StagedWindowStream(disk, target.Offset, target.Size);
    var descriptor = InnerFsDetector.Detect(window);
    if (descriptor is not IArchiveModifiable modifier)
      throw new InvalidOperationException(
        $"{target.Prefix} holds {(descriptor is null ? "no filesystem CompressionWorkbench recognises" : $"a {descriptor.DisplayName} volume CompressionWorkbench cannot edit")}; nothing was changed.");
    if (!HasNativeEditor(modifier, method))
      throw new InvalidOperationException(
        $"{target.Prefix}: {descriptor.DisplayName} can only be edited by re-creating the volume, which would not preserve it; nothing was changed.");
    try {
      window.Position = 0;
      edit(modifier, window);
    } catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidDataException
                                   or ArgumentException or InvalidOperationException) {
      throw new InvalidOperationException($"{target.Prefix} ({descriptor.DisplayName}) refused the edit: {ex.Message}", ex);
    }
    return window;
  }

  private static List<(ArchiveInputInfo Input, long Start, long Length, PartitionType Type, string? Label)> PlanNewPartitions(
      Stream disk, PartitionTableDetector.DetectionResult detection, List<ArchiveInputInfo> inputs) {
    if (detection.Scheme is not ("GPT" or "MBR"))
      throw new InvalidOperationException($"New partitions can only be added to a GPT or MBR table, not {detection.Scheme}.");

    var probe = new PartitionEditor(new ReadOnlyProbe(disk), detection.SectorSize);
    var sectorSize = probe.LogicalSectorSize;
    var (usableStart, usableEnd) = probe.UsableRange;
    var existing = probe.ListPartitions();
    var primaries = existing.Count(e => e.Source != "EBR");
    var capacity = probe.Scheme == PartitionScheme.Gpt ? 128 : 4;
    if ((probe.Scheme == PartitionScheme.Gpt ? existing.Count : primaries) + inputs.Count > capacity)
      throw new InvalidOperationException(
        $"The {detection.Scheme} table has room for {capacity} partitions; adding {inputs.Count} would exceed it.");

    var cursor = Math.Max(usableStart, existing.Count == 0 ? 0 : existing.Max(e => e.StartOffset + e.Size));
    var plan = new List<(ArchiveInputInfo, long, long, PartitionType, string?)>();
    foreach (var input in inputs) {
      using var content = OpenInput(input);
      var type = DetectInputPartitionType(content);
      var nameLooksLikeImage = Path.GetFileName(input.ArchiveName) is var leaf
        && (leaf.EndsWith(".raw", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("Partition", StringComparison.Ordinal));
      if (type == PartitionType.Unknown && !nameLooksLikeImage)
        throw new InvalidOperationException(
          $"'{input.ArchiveName}' is neither a filesystem image nor a raw partition image (*.raw/*.img); a disk holds partitions, not loose files.");
      if (content.Length <= 0)
        throw new InvalidOperationException($"'{input.ArchiveName}' is empty; a partition needs at least one sector.");

      var length = AlignUp(content.Length, sectorSize);
      var aligned = AlignUp(cursor, MiB);
      var start = aligned + length <= usableEnd ? aligned : AlignUp(cursor, sectorSize);
      if (start + length > usableEnd)
        throw new InvalidOperationException(
          $"Not enough free space after the last partition for '{input.ArchiveName}' ({content.Length:N0} bytes); nothing was changed.");
      plan.Add((input, start, length, type == PartitionType.Unknown ? PartitionType.Linux : type,
        Path.GetFileNameWithoutExtension(input.ArchiveName)));
      cursor = start + length;
    }
    return plan;
  }

  private const long MiB = 1024 * 1024;

  private static Stream OpenInput(ArchiveInputInfo input)
    => input.InMemoryContent is { } bytes
      ? new MemoryStream(bytes, writable: false)
      : new FileStream(input.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);

  private static void WritePartitionData(Stream disk, ArchiveInputInfo input, long start, long length) {
    using var content = OpenInput(input);
    disk.Position = start;
    content.CopyTo(disk);
    var pad = length - content.Length;
    if (pad > 0) disk.Write(new byte[pad]);
  }

  /// <summary>A view that satisfies <see cref="PartitionEditor"/>'s writable-stream check but refuses writes — for planning only.</summary>
  private sealed class ReadOnlyProbe(Stream inner) : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Planning view.");
  }

  private static long AlignUp(long value, long alignment)
    => (value + alignment - 1) / alignment * alignment;

  /// <summary>
  /// Maps the input bytes to a logical <see cref="PartitionType"/> by running
  /// <see cref="InnerFsDetector"/> over the input and translating the resulting descriptor ID;
  /// <see cref="PartitionType.Unknown"/> when no filesystem is recognised.
  /// </summary>
  private static PartitionType DetectInputPartitionType(Stream data) {
    if (data.Length < 512) return PartitionType.Unknown;
    var desc = InnerFsDetector.Detect(data);
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
