#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.F2fs;

public sealed class F2fsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable, IFormatOptionsSchema {

  // A F2FS segment is 2 MiB; image size in bytes = segment count × 2 MiB.
  private const long SegmentSizeBytes = 2L * 1024 * 1024;

  // ── IFormatOptionsSchema ────────────────────────────────────────────────
  // Image-size presets all map to a segment count (MB / 2 = segments). The smallest
  // offered preset (64 MB = 32 segments) is well above the writer's 16-segment floor.
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.ImageSize(["64 MB", "128 MB", "256 MB", "512 MB", "1 GB", "2 GB"]),
    FilesystemSchemaPresets.VolumeLabel(16),
  ];

  public string Id => "F2fs";
  public string DisplayName => "F2FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  public string DefaultExtension => ".f2fs";
  public IReadOnlyList<string> Extensions => [".f2fs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x10, 0x20, 0xF5, 0xF2], Offset: 1024, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// F2FS flash-friendly filesystem image — WORM. The writer emits a real
  /// kernel-spec multi-segment image (superblock + checkpoint pack +
  /// SIT/NAT/SSA + Main area with HOT/WARM/COLD nodes and inline-dentry
  /// root). True in-flight Add/Remove would require mutating the NAT and SIT
  /// journals, allocating from the segment-typed valid_map, walking inline
  /// dentries, and recomputing the checkpoint CRC — multi-week work.
  /// Per project policy, WORM = create only; no in-flight modification.
  /// </summary>
  public string Description => "F2FS flash-friendly filesystem image (WORM)";

  // --- WORM write constraints ---
  // F2FS minimum image = ~30 MB in the real-world mkfs.f2fs tool; our writer emits 64 MB by
  // default. No per-file ceiling is imposed at the descriptor level — the writer rejects
  // individual files > 923 × 4096 ≈ 3.6 MB (single-extent direct-block limit).
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 64L * 1024 * 1024;
  public string AcceptedInputsDescription =>
    "F2FS filesystem image (flat root directory, inline dentries; per-file max ≈ 3.6 MB).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = null; return true; }
    try {
      var info = new FileInfo(input.FullPath);
      if (info.Length > 923L * 4096L) {
        reason = $"F2FS writer supports only direct-pointer files (max {923 * 4096} bytes per file).";
        return false;
      }
    } catch {
      // If we can't stat it, let Create fail with the real reason.
    }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new F2fsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new F2fsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var specific = options.FormatSpecific;
    var segments = ParseImageSizeSegments(specific?.GetValueOrDefault("ImageSize"));
    var label = specific?.GetValueOrDefault("VolumeLabel");

    var w = new F2fsWriter();
    w.SetVolumeLabel(label);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);

    var image = segments > 0 ? w.Build(segments) : w.BuildAutoSized();
    output.Write(image, 0, image.Length);
  }

  // Maps an image-size preset label to a F2FS segment count (2 MiB per segment).
  // "Auto (fit to files)" / unknown → 0, signalling BuildAutoSized().
  private static int ParseImageSizeSegments(string? s) => s?.Trim() switch {
    "64 MB"  => (int)(64L * 1024 * 1024 / SegmentSizeBytes),    // 32
    "128 MB" => (int)(128L * 1024 * 1024 / SegmentSizeBytes),   // 64
    "256 MB" => (int)(256L * 1024 * 1024 / SegmentSizeBytes),   // 128
    "512 MB" => (int)(512L * 1024 * 1024 / SegmentSizeBytes),   // 256
    "1 GB"   => (int)(1024L * 1024 * 1024 / SegmentSizeBytes),  // 512
    "2 GB"   => (int)(2L * 1024 * 1024 * 1024 / SegmentSizeBytes), // 1024
    _        => 0, // Auto (fit to files)
  };

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware F2FS defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The writer always emits a fresh
  /// contiguous-from-start multi-segment image (SIT/NAT journals, checkpoint
  /// pack, inline-dentry root).
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options, ReadEntries, BuildImage);

  // ── IArchiveModifiable (rebuild-based add / replace / remove) ──────────
  // F2FS in-place mutation needs NAT/SIT journal updates + checkpoint CRC
  // recompute; instead we read every file and rebuild a fresh fsck.f2fs-clean
  // image with the writer, the same path the defragmentor uses.

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs, ReadEntries, BuildImage);

  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames, ReadEntries, BuildImage);

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new F2fsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new F2fsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }
}
