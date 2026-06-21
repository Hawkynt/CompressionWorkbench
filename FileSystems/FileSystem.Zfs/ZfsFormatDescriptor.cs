#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Zfs;

public sealed class ZfsFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable {

  public string Id => "Zfs";
  public string DisplayName => "ZFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
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
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
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

  // ── IArchiveModifiable (rebuild-based add / replace / remove) ──────────
  // ZFS in-place mutation needs DMU/ZAP/space-map + uberblock advance; instead
  // we read every file and rebuild a fresh fat-ZAP-capable image with the
  // writer (keeping the image footprint), the same path the defragmentor uses.

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var size = archive.Length;
    ModifyRebuilder.Add(archive, inputs, ReadEntries, files => BuildImage(files, size));
  }

  public void Remove(Stream archive, string[] entryNames) {
    var size = archive.Length;
    ModifyRebuilder.Remove(archive, entryNames, ReadEntries, files => BuildImage(files, size));
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
