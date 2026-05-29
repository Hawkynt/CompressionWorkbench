#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Zfs;

public sealed class ZfsFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveDefragmentable {

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
      w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
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
    // Capture original image size before the rebuild rewrites the archive —
    // ZFS labels live at fixed start + end positions so the rebuilt image
    // must keep the same overall footprint.
    var originalSize = archive.Length;
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new ZfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new ZfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        using var ms = new MemoryStream();
        w.WriteTo(ms, originalSize);
        return ms.ToArray();
      });
  }
}
