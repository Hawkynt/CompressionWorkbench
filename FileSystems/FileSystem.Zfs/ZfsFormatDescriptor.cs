#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Zfs;

public sealed class ZfsFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Knobs the WORM pool writer honours. <c>VolumeLabel</c> maps to the pool
  /// name written into the vdev-label NVList <c>name</c> field (and the vdev
  /// <c>path</c>), read back as <c>ZfsReader.PoolName</c>; <c>ImageSize</c> maps
  /// to the total pool image size and must be at least
  /// <see cref="MinTotalArchiveSize"/> (the four 256&#160;KB vdev labels plus a
  /// usable data area). The 512-byte sector size and Fletcher-4 checksum are
  /// fixed, so they are not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "VolumeLabel", DisplayName: "Pool name", Kind: FormatOptionKind.String, Default: "compworkbench",
      Description: "ZFS pool name stored in the vdev-label NVList."),
    FilesystemSchemaPresets.ImageSize(["64 MB", "128 MB", "256 MB"],
      description: "Total pool image size (at least 64 MB)."),
  ];

  public string Id => "Zfs";
  public string DisplayName => "ZFS";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a genuine in-place writer. Add tries copy-on-write in place (new blocks for
  // the changed path only, advance the uberblock) and falls back to a rebuild for the
  // shapes the in-place adder does not handle; Remove is rebuild-based. CanModify is
  // advertised because the in-place add genuinely mutates the image without re-laying
  // untouched data (verified by ZfsReader round-trip + the CoW-offset proof).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".zfs";
  public IReadOnlyList<string> Extensions => [".zfs", ".zpool"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "ZFS pool image — single-vdev, single-dataset, flat root directory (WORM writer). " +
    "Fletcher-4 checksums, NV_BIG_ENDIAN XDR label, pool version 28.";

  // Write constraints.
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 64L * 1024 * 1024; // 64 MB minimum image size.
  public string AcceptedInputsDescription =>
    "ZFS pool image (WORM); flat root directory, no subdirectories, up to 14 files.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = "Flat root only; no subdirectories."; return false; }
    // microzap fits ~14 entries in 1 KB — we don't have a count at CanAccept time, so
    // limit only per-entry here and let the writer throw if over 14.
    if (input.ArchiveName.Length >= 50) {
      reason = "File name exceeds microzap 49-char limit.";
      return false;
    }
    if (input.ArchiveName.Contains('/') || input.ArchiveName.Contains('\\')) {
      reason = "Flat root only; no path separators in names.";
      return false;
    }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ZfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new ZfsWriter();
    var poolName = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(poolName))
      w.SetPoolName(poolName);
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    long sizeBytes = FilesystemSchemaPresets.ParseSize(options?.GetOption("ImageSize", ""));
    if (sizeBytes >= (MinTotalArchiveSize ?? 0))
      w.WriteTo(output, sizeBytes);
    else
      w.WriteTo(output);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware ZFS defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The writer always emits a fresh
  /// contiguous-from-start pool image with single-vdev, single-dataset,
  /// flat root, Fletcher-4 checksums, and NV_BIG_ENDIAN XDR labels.
  /// Image size is preserved from the original archive length so labels
  /// land at the expected start/end positions.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    // ZFS labels live at fixed start + end positions, so keep the original
    // footprint. Capture it before the rebuild rewrites the archive.
    var originalSize = archive.Length;
    DefragRebuilder.Rebuild(archive, options, ReadEntries, files => BuildImage(files, originalSize));
  }

  // ── IArchiveModifiable (genuine copy-on-write add, rebuild fallback) ────
  // Add tries a real in-place CoW add via ZfsModifier (new blocks only for the
  // changed path, then a new uberblock in the next label slot); for shapes the
  // in-place adder cannot do it falls back to the read-all/rebuild path the
  // defragmentor uses. Remove is rebuild-based.

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var toAdd = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (i.ArchiveName, i.ReadContent()))
      .ToList();
    if (toAdd.Count == 0)
      return;
    ZfsModifier.AddOrReplace(archive, toAdd);
  }

  public void Remove(Stream archive, string[] entryNames) {
    ZfsModifier.Remove(archive, entryNames);
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new ZfsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files, long imageSize) {
    var w = new ZfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms, imageSize);
    return ms.ToArray();
  }
}
