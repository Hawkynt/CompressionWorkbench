#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Cromemco;

/// <summary>
/// Descriptor for Cromemco RDOS volumes (Z-80 CP/M-derived system used
/// on Cromemco Z2 / System Three machines, late 1970s). Detection by the
/// 0xC3 (Z-80 JP) prefix at offset 0 plus an embedded "CROMEMCO" ASCII
/// tag inside the first 64 bytes of the boot block.
///
/// <para>RDOS is a flat-only filesystem (CP/M-style): all entries live in
/// a single 16-sector directory area starting at sector 2 with no support
/// for subdirectories. The <see cref="List"/> output therefore never
/// contains a directory entry.</para>
///
/// <para>Capabilities: read + write (write-once, no in-place add/remove),
/// defragment via extract-and-rebuild, free-space wiping driven by the
/// extent map, and creation-options schema for density/track-count
/// selection through the Convert Archive dialog.</para>
/// </summary>
public sealed class CromemcoFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
  IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  public string Id => "Cromemco";
  public string DisplayName => "Cromemco RDOS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".rdos";
  public IReadOnlyList<string> Extensions => [".rdos", ".crom"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // We only emit one magic — the "CROMEMCO" 8-byte ASCII tag at the most
    // common offset (0x0B). Reader scans the first 64 bytes for tolerance.
    new("CROMEMCO"u8.ToArray(), Offset: 0x0B, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Cromemco RDOS Z-80 disk filesystem — CP/M-derived flat-only 8.3 filenames, 128-byte sectors.";

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs the Convert Archive dialog / CLI exposes for Cromemco
  /// creation: floppy density and track count select the disk geometry
  /// (sector size is locked at 128 B by the reader).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Density",
      DisplayName: "Density",
      Kind: FormatOptionKind.Enum,
      Default: "Auto",
      AllowedValues: ["Auto", "Single", "Double"],
      Description: "Single density = 18 sectors/track. Double density = 26 sectors/track (System Three)."),
    new FormatOptionDescriptor(
      Key: "Tracks",
      DisplayName: "Tracks",
      Kind: FormatOptionKind.Enum,
      Default: "Auto",
      AllowedValues: ["Auto", "35", "77"],
      Description: "35 tracks = original Cromemco Z2 floppy. 77 tracks = System Three drives."),
    new FormatOptionDescriptor(
      Key: "SectorSize",
      DisplayName: "Sector size",
      Kind: FormatOptionKind.Enum,
      Default: "128",
      AllowedValues: ["128"],
      Description: "Cromemco RDOS uses 128-byte sectors (CP/M convention)."),
  ];

  // ── IArchiveFormatOperations ────────────────────────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new CromemcoReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new CromemcoReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  // ── IArchiveCreatable ───────────────────────────────────────────────────

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var w = new CromemcoWriter();
    var density = options?.GetOption("Density", "Auto") ?? "Auto";
    var tracksStr = options?.GetOption("Tracks", "Auto") ?? "Auto";

    // Auto picks the smallest geometry that fits via CromemcoOptimizer.
    var fileSizes = inputs.Where(i => !i.IsDirectory).Select(i => (long)i.ReadContent().Length).ToList();
    var auto = CromemcoOptimizer.Find(fileSizes);

    var tracks = tracksStr == "Auto" || !int.TryParse(tracksStr, out var t) ? auto.Tracks : t;
    var spt = density switch {
      "Single" => 18,
      "Double" => 26,
      _        => auto.SectorsPerTrack,
    };
    w.SetGeometry(tracks, spt);

    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  // ── IArchiveDefragmentable ─────────────────────────────────────────────

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    // Capture the source's geometry so the rebuilt image is the same physical
    // size — otherwise the optimizer would happily shrink the disk and break
    // the defrag contract ("size must not change").
    var sourceLength = archive.Length;
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new CromemcoReader(stream);
        return r.Entries.Where(e => !e.IsDirectory)
                        .Select(e => (e.Name, r.Extract(e)))
                        .ToList();
      },
      buildImage: files => {
        var w = new CromemcoWriter();
        // Prefer the source image's geometry. Otherwise the optimizer can pick
        // a tighter disk and shrink the rebuilt image below the original size.
        var srcTracks = (int)(sourceLength / (CromemcoReader.SectorSize * 26L));
        if (srcTracks < 1) srcTracks = 1;
        // Derive sectors-per-track from the residue assumption (Cromemco
        // standard layout: 26 sectors/track, 77 tracks = 256256 bytes).
        var srcSpt = sourceLength > 0 && srcTracks > 0
          ? (int)(sourceLength / CromemcoReader.SectorSize / srcTracks)
          : 26;
        if (srcSpt < 1) srcSpt = 26;
        w.SetGeometry(srcTracks, srcSpt);
        foreach (var (n, d) in files) w.AddFile(n, d);
        var built = w.Build();
        // Belt-and-suspenders: pad to exactly the source length if the writer
        // returned slightly fewer bytes (e.g. trailing-sector elision).
        if (built.Length < sourceLength) {
          var padded = new byte[sourceLength];
          Array.Copy(built, padded, built.Length);
          return padded;
        }
        return built;
      });
  }

  // ── IFilesystemExtentMap ───────────────────────────────────────────────

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => CromemcoExtentMap.Enumerate(image);

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros every byte in the image not claimed by the boot block, the
  /// directory area, or a live file's contiguous sector run. Cluster-tip
  /// wiping (slack between the file's real size and its rounded-up sector
  /// allocation) is honoured when <paramref name="wipeClusterTips"/> is
  /// true, using the directory entry's record count.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? sizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new CromemcoReader(image);
        var sizeMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in reader.Entries)
          if (!e.IsDirectory)
            sizeMap[e.Name] = e.Size;
        sizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        sizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = CromemcoExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, sizeLookup);
  }
}
