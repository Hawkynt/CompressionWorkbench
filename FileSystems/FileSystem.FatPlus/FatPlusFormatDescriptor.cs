#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.FatPlus;

/// <summary>
/// FAT+ (also called FAT32+ / FAT16+) format descriptor. FAT+ is an open
/// extension to standard FAT that lifts the per-file 4 GiB size cap to 256 GiB
/// by repurposing previously-reserved bytes in the 32-byte directory entry to
/// hold the upper bits of file size.
///
/// References:
/// <list type="bullet">
///   <item><description>FAT+ draft revision 2 (FATPLUS.TXT, Udo Kuhnt / Luchezar Georgiev / Jeremy Davis, 2007) — the defining spec, historically hosted at fdos.org/kernel/fatplus.txt</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Design_of_the_FAT_file_system</c> — Wikipedia's FAT reference, which documents the FAT+ extension</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Specification source.</b> FAT+ draft revision 2/3 (FATPLUS.TXT, 2007)
/// by Udo Kuhnt, Luchezar Georgiev and Jeremy Davis, historically hosted at
/// fdos.org/kernel/fatplus.txt. Cited from the Wikipedia
/// "File Allocation Table" and "Large-file support" articles.</para>
///
/// <para><b>Detection.</b> A FAT+ volume is identified by an OEM-name signature
/// in the BPB: the 8 ASCII bytes at offset 3 of the boot sector read
/// <c>"FAT+    "</c> (4 chars + 4 spaces). This descriptor uses that as a
/// magic signature with high confidence — the standard FAT descriptor has
/// no magic and falls back to extension matching, so this descriptor is
/// always tried first.</para>
///
/// <para><b>Implemented operations.</b> List, extract, create, add, remove, and
/// defragment. Creation produces a FAT32 image with the FAT+ OEM signature and
/// per-file 38-bit size encoding (low 32 bits at <c>DIR_FileSize</c>, high 6
/// bits in the low 6 bits of <c>DIR_NTRes</c>; top 2 bits of NTRes remain
/// clear to preserve the Windows NT case-flag convention). Add/Remove operate
/// genuinely in place via <see cref="FatPlusInPlaceAdder"/> (Add allocates free
/// clusters, links the chain, inserts the dirent and patches the FAT+
/// extended-size bits; Remove frees the chain + wipes the dirent), with a
/// verified <see cref="FatPlusWriter"/> rebuild as the structural-edge-case
/// fallback. Defragment goes through the standard
/// <see cref="DefragRebuilder"/> rebuild path.</para>
/// </remarks>
public sealed class FatPlusFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  // FAT+ is always a FAT32 volume aimed at large media, so we expose only the
  // knobs the writer actually honours: image size (large presets + Auto),
  // cluster size, and the volume label (plumbed through to the inner FatWriter).
  // FAT type / root-entry count are NOT exposed — FAT+ is fixed to FAT32 and the
  // writer does not accept those parameters.
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.ImageSize(
      ["512 MB", "1 GB", "2 GB", "4 GB", "16 GB", "64 GB"],
      "Total image capacity. Auto fits the files (minimum 100 MB to stay in FAT32). " +
      "FAT+ targets large volumes, so the fixed presets start at 512 MB."),
    FilesystemSchemaPresets.ClusterSize(
      description: "Allocation unit size. Auto picks the size that minimises slack + FAT overhead."),
    FilesystemSchemaPresets.VolumeLabel(),
  ];

  public string Id => "FatPlus";
  public string DisplayName => "FAT+ Filesystem Image (large-file extension)";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: Add/Remove edit the FAT, clusters and directory in place
  // (FatPlusInPlaceAdder reusing FatModifier/FatRemover, plus the FAT+
  // extended-size dirent patch); existing files and the boot sector stay
  // byte-identical. A verified FatPlusWriter rebuild is only a
  // structural-edge-case fallback.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".img";

  // Empty extensions list: FAT+ shares .img with FAT/exFAT. Detection is
  // strictly by the BPB OEM signature so we don't grab unrelated .img files.
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];

  // Magic: OEM signature "FAT+    " at offset 3 of the boot sector.
  // High confidence — this is the defining mark of a FAT+ volume.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new MagicSignature(FatPlusReader.OemSignature, Offset: 3, Confidence: 0.95),
  ];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "FAT32/FAT16 image with the FAT+ 256 GiB-file extension (FATPLUS.TXT draft rev 2/3).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new FatPlusReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new FatPlusReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;

      // Streaming path: handles files larger than 2 GiB which would otherwise
      // overflow a byte[].
      var safeName = e.Name.Replace('\\', '/').TrimStart('/');
      if (safeName.Contains("..")) safeName = Path.GetFileName(safeName);
      var fullPath = Path.Combine(outputDir, safeName);
      var dir = Path.GetDirectoryName(fullPath);
      if (dir != null) Directory.CreateDirectory(dir);
      using var fs = File.Create(fullPath);
      r.ExtractTo(e, fs);
    }
  }

  /// <summary>
  /// Builds a fresh FAT+ image at <paramref name="output"/> from the supplied inputs.
  /// Image size defaults to 100 MB (200_000 sectors) — enough to land in the FAT32
  /// cluster-count range that FAT+ extends. For larger payloads the writer
  /// automatically scales.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new FatPlusWriter();
    // Streaming inputs: only a length is needed to lay the volume out, and the
    // writer places file data by seek. Reading each input into a byte[] first
    // capped the volume at what an array can hold.
    var streaming = TotalInputBytes(inputs) > StreamingCreateThreshold;
    foreach (var (name, size, open) in AsStreamingInputs(inputs))
      if (streaming) w.AddStreamingFile(name, size, open);
      else using (var src = open()) { using var ms = new MemoryStream(); src.CopyTo(ms); w.AddFile(name, ms.ToArray()); }

    var specific = options.FormatSpecific;
    var totalSectors = ParseImageSizeSectors(specific?.GetValueOrDefault("ImageSize"));
    // ClusterSize uses the standard FormatSize labels, so the shared inverse
    // parser handles it (same as NTFS/F2fs/exFAT). ImageSize needs its own parser
    // because it offers GB presets and must yield sectors, not bytes.
    var clusterBytes = FilesystemSchemaPresets.ParseSize(specific?.GetValueOrDefault("ClusterSize"));
    var label        = specific?.GetValueOrDefault("VolumeLabel");

    // Fixed image size + cluster on Auto: optimise the cluster size *within* that
    // fixed size to minimise slack waste instead of using the default heuristic.
    if (totalSectors > 0 && clusterBytes == 0) {
      var picked = w.PickClusterForFixedImage(totalSectors);
      if (picked > 0) clusterBytes = picked;
    }

    // A fixed size streams: Build() materialises the whole volume as one byte[] and
    // so caps FAT+ at the ~2 GB array limit, while BuildTo leaves free space sparse.
    if (totalSectors > 0 && output.CanSeek) {
      w.BuildTo(output, totalSectors, requestedClusterSize: clusterBytes, volumeLabel: label);
      return;
    }

    // An auto-sized volume goes the same way: BuildAutoSized materialises the
    // whole thing, so a payload past the array limit could not be built at all.
    if (output.CanSeek && streaming) {
      w.BuildToStreamingAutoSized(output, requestedClusterSize: clusterBytes, volumeLabel: label);
      return;
    }

    var disk = totalSectors > 0
      ? w.Build(totalSectors, requestedClusterSize: clusterBytes, volumeLabel: label)
      : w.BuildAutoSized(requestedClusterSize: clusterBytes, volumeLabel: label);
    output.Write(disk);
  }

  private static int ParseImageSizeSectors(string? s) => s?.Trim() switch {
    "512 MB" => 1048576,
    "1 GB"   => 2097152,
    "2 GB"   => 4194304,
    "4 GB"   => 8388608,
    "16 GB"  => 33554432,
    "64 GB"  => 134217728,
    _        => 0,  // "Auto (fit to files)" or anything else → auto-size
  };

  /// <summary>
  /// Adds files to an existing FAT+ image. Implemented as full rebuild via
  /// <see cref="FatPlusWriter"/> — preserves existing file extended-size
  /// encodings as reported by <see cref="FatPlusReader"/>.
  /// </summary>
  /// <summary>
  /// Applies an edit by reading every surviving entry out of <paramref name="archive" />
  /// and writing a fresh volume of the same declared size back over it. Used when the
  /// in-place editor cannot take the volume -- it works on a byte[] of the whole image,
  /// which a FAT+ volume (a FAT32 extension) is under no obligation to fit.
  /// </summary>
  private static void RebuildInPlaceStreaming(
      Stream archive,
      IReadOnlyList<(string Name, byte[] Data)> additions,
      ISet<string>? drop) {
    var totalSectors = (int)Math.Min(int.MaxValue, archive.Length / 512);
    var combined = new FatPlusWriter();

    archive.Position = 0;
    var reader = new FatPlusReader(archive, leaveOpen: true);
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory)) {
      if (drop != null && (drop.Contains(entry.Name) || drop.Contains(Path.GetFileName(entry.Name))))
        continue;
      combined.AddFile(entry.Name, reader.Extract(entry));
    }
    foreach (var (name, data) in additions)
      combined.AddFile(name, data);

    archive.Position = 0;
    archive.SetLength(0);
    combined.BuildTo(archive, totalSectors);
  }

  /// <summary>
  /// Largest volume the byte[]-based in-place editors and buffered rebuild can take.
  /// </summary>
  private const long MaxBufferedImageBytes = 1L << 31;

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var items = FormatHelpers.FilesOnly(inputs).ToList();
    try {
      foreach (var (name, data) in items)
        FatPlusModifier.AddFile(archive, name, data);
    } catch (Exception ex) when (ex is not FileNotFoundException
                                 && ex is NotSupportedException or IOException
                                 or InvalidDataException or InvalidOperationException) {
      // FileNotFoundException means the caller asked for something absent -- that is
      // the answer, not a reason to rebuild.
      RebuildInPlaceStreaming(archive, items, drop: null);
    }
  }

  /// <summary>
  /// Removes the named entries from a FAT+ image with full secure wipe (cluster
  /// data bytes, cluster-tip slack, FAT chain entries, and directory entries).
  /// Preserves the BPB OEM signature so detection still flags the image as
  /// FAT+ afterwards.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    try {
      foreach (var name in entryNames)
        FatPlusModifier.RemoveFile(archive, name);
    } catch (Exception ex) when (ex is not FileNotFoundException
                                 && ex is NotSupportedException or IOException
                                 or InvalidDataException or InvalidOperationException) {
      RebuildInPlaceStreaming(archive, [], new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase));
    }
  }

  /// <summary>
  /// Rebuilds <paramref name="archive"/> in place so every file occupies a contiguous
  /// cluster run. Outer byte size is preserved. Uses
  /// <see cref="DefragRebuilder"/> via <see cref="FatPlusReader"/> (read path) and
  /// <see cref="FatPlusWriter"/> (write path) — the writer always start-packs
  /// from cluster 2, which is exactly the defragmented layout.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware FAT+ defragmentor — delegates to the rebuild path in
  /// <see cref="DefragRebuilder.Rebuild"/>. Supports all four
  /// <see cref="DefragMode"/> values via the rebuilder's listing-order
  /// dispatch.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var totalSectors = (int)(archive.Length / 512);

    // Capture per-file extended sizes (preserved across rebuild) by walking the
    // image once up front. The rebuilder hands the writer (name, byte[]) pairs
    // and has no notion of "declared size > actual bytes", so we plumb the
    // extended sizes via this side-channel.
    archive.Position = 0;
    Dictionary<string, long> declaredSizes;
    using (var pre = new FatPlusReader(archive, leaveOpen: true)) {
      declaredSizes = pre.Entries
        .Where(e => !e.IsDirectory)
        .ToDictionary(e => e.Name, e => e.Size, StringComparer.OrdinalIgnoreCase);
    }

    // A volume too large to materialise goes through the streaming rebuilder: the
    // buffered path's buildImage callback returns a byte[] of the whole image, which
    // caps FAT+ at the ~2 GB array limit.
    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes
        && options.Mode is DefragMode.ConsolidateAtStart or DefragMode.FillHolesLazy) {
      FatPlusWriter? streamWriter = null;
      Stream? target = null;
      DefragRebuilder.RebuildStreaming(
        archive,
        options,
        readEntries: stream => {
          using var r = new FatPlusReader(stream, leaveOpen: true);
          return r.Entries.Where(e => !e.IsDirectory)
                          .Select(e => (e.Name, r.Extract(e)))
                          .ToList();
        },
        beginWrite: s2 => { streamWriter = new FatPlusWriter(); target = s2; },
        writeEntry: (name, data) => streamWriter!.AddFile(
          name, data, extendedSize: declaredSizes.TryGetValue(name, out var d) ? d : data.Length),
        finishWrite: () => streamWriter!.BuildTo(target!, Math.Max(totalSectors, 200_000)));
      return;
    }

    DefragRebuilder.Rebuild(
      archive,
      options,
      readEntries: stream => {
        using var r = new FatPlusReader(stream, leaveOpen: true);
        var list = new List<(string Name, byte[] Data)>();
        foreach (var e in r.Entries) {
          if (e.IsDirectory) continue;
          // Bounded extract: rebuild path can only carry int.MaxValue bytes
          // per file. For oversize declared entries we still rewrite using
          // the bytes we can fit, and reconstitute the declared size below.
          if (e.Size <= int.MaxValue) {
            list.Add((e.Name, r.Extract(e)));
          } else {
            using var ms = new MemoryStream();
            r.ExtractTo(e, ms);
            list.Add((e.Name, ms.ToArray()));
          }
        }
        return list;
      },
      buildImage: files => {
        var w = new FatPlusWriter();
        foreach (var (name, data) in files) {
          var declared = declaredSizes.TryGetValue(name, out var d) ? d : data.Length;
          w.AddFile(name, data, extendedSize: declared);
        }
        return w.Build(totalSectors: Math.Max(totalSectors, 200_000));
      });
  }
  /// <summary>
  /// Turns buffered inputs into streaming ones. Only a length is needed to lay a
  /// volume out; reading each input into a byte[] first caps the volume at what
  /// an array can hold even though the writer places file data by seek.
  /// </summary>
  private static List<(string Name, long Size, Func<Stream> Open)> AsStreamingInputs(
      IReadOnlyList<ArchiveInputInfo> inputs) {
    var result = new List<(string, long, Func<Stream>)>();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      var size = info.InMemoryContent?.LongLength
                 ?? (File.Exists(info.FullPath) ? new FileInfo(info.FullPath).Length : 0L);
      result.Add((Path.GetFileName(info.ArchiveName), size,
        () => info.InMemoryContent is { } bytes
          ? new MemoryStream(bytes, writable: false)
          : File.OpenRead(info.FullPath)));
    }
    return result;
  }

  /// <summary>
  /// Payload above which creation takes the streaming route. Below it the
  /// buffered writer is used, which is what honours the format-specific options
  /// (NTFS compression, explicit geometry) the streaming path cannot express.
  /// </summary>
  private const long StreamingCreateThreshold = 1024L * 1024 * 1024;

  /// <summary>Total bytes the inputs will contribute to the volume.</summary>
  private static long TotalInputBytes(IReadOnlyList<ArchiveInputInfo> inputs) {
    var total = 0L;
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      try {
        total += i.InMemoryContent?.LongLength
                 ?? (File.Exists(i.FullPath) ? new FileInfo(i.FullPath).Length : 0L);
      } catch { /* unreadable input — the writer will report it */ }
    }
    return total;
  }


  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>
  /// FAT+ keeps FAT's on-disk layout — same BPB, same FATs, same cluster
  /// chains — so the FAT walker maps it as it stands.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => FileSystem.Fat.FatExtentMap.Enumerate(image);

  /// <inheritdoc />
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var imageSize = image.Length;

    // Cluster tips need each file's true length; without it the wiper would
    // treat a whole cluster as live and leave the slack behind.
    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in this.List(image, null))
          if (!entry.IsDirectory)
            sizes[entry.Name] = entry.OriginalSize;
        fileSizeLookup = n => sizes.TryGetValue(n, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

}
