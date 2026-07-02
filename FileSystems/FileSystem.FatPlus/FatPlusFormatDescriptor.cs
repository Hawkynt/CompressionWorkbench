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
    IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable {

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
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);

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
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      FatPlusModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from a FAT+ image with full secure wipe (cluster
  /// data bytes, cluster-tip slack, FAT chain entries, and directory entries).
  /// Preserves the BPB OEM signature so detection still flags the image as
  /// FAT+ afterwards.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      FatPlusModifier.RemoveFile(archive, name);
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
}
