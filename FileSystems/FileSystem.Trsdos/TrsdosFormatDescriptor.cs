#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Trsdos;

/// <summary>
/// Descriptor for TRSDOS / LDOS disk images (Radio Shack TRS-80 Model
/// I / III / 4, late 1970s–early 1980s). Detection by the 0xFE GAT
/// signature at track 17, sector 0, offset 0xCD; reader walks the
/// directory records that follow in track 17 sectors 2..N. Sector size
/// is 256 B; default geometry is 40 tracks × 18 spt (Model III/4 DD)
/// with fallback to 10/9/26 spt.
///
/// <para>TRSDOS is a flat-only filesystem (no subdirectories). The
/// <see cref="List"/> output therefore never contains a directory
/// entry.</para>
///
/// <para>Capabilities: read + write, defragment via extract-and-rebuild,
/// free-space wiping driven by the extent map, and creation-options
/// schema for density/track-count selection.</para>
/// </summary>
public sealed class TrsdosFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
  IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  public string Id => "Trsdos";
  public string DisplayName => "TRSDOS / LDOS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".trsdos";
  public IReadOnlyList<string> Extensions => [".trsdos", ".dmk", ".jv1", ".jv3"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // TRSDOS has no top-of-file magic; the 0xFE GAT signature lives at
  // a geometry-dependent offset (track 17 * sectors-per-track * 256 + 0xCD).
  // For the canonical 18-sector DD geometry that's offset 78413.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xFE], Offset: 78413, Confidence: 0.55),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "TRSDOS / LDOS disk filesystem (TRS-80 Model I/III/4) — flat-only GAT+HIT layout at track 17, 256-byte sectors.";

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for TRSDOS creation: density, track count, disk name,
  /// and format date. Granules-per-cylinder is auto-derived from
  /// sectors/track (5 sectors per granule).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Density",
      DisplayName: "Density",
      Kind: FormatOptionKind.Enum,
      Default: "Auto",
      AllowedValues: ["Auto", "Single", "Double"],
      Description: "Single density = 10 sectors/track. Double density = 18 sectors/track."),
    new FormatOptionDescriptor(
      Key: "Tracks",
      DisplayName: "Tracks",
      Kind: FormatOptionKind.Enum,
      Default: "Auto",
      AllowedValues: ["Auto", "35", "40", "80"],
      Description: "35 = Model I 5.25\" SD. 40 = Model III/4 DD. 80 = Model 4 high-density."),
    new FormatOptionDescriptor(
      Key: "DiskName",
      DisplayName: "Disk name",
      Kind: FormatOptionKind.String,
      Default: "WORM",
      Description: "8-character disk name written to the GAT (truncated/padded)."),
    new FormatOptionDescriptor(
      Key: "Date",
      DisplayName: "Format date",
      Kind: FormatOptionKind.String,
      Default: "01/01/26",
      Description: "8-character format date written to the GAT (MM/DD/YY)."),
  ];

  // ── IArchiveFormatOperations ────────────────────────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new TrsdosReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new TrsdosReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  // ── IArchiveCreatable ───────────────────────────────────────────────────

  /// <summary>
  /// Opens a single filesystem entry as a bounded read-only stream. The
  /// reader produces the decoded file bytes by walking the entry's extent
  /// or block chain; the matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length so cluster/extent slack past the entry's
  /// end is physically unreachable through this view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new TrsdosReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var w = new TrsdosWriter();
    var density = options?.GetOption("Density", "Auto") ?? "Auto";
    var tracksStr = options?.GetOption("Tracks", "Auto") ?? "Auto";
    var diskName = options?.GetOption("DiskName", "WORM") ?? "WORM";
    var date = options?.GetOption("Date", "01/01/26") ?? "01/01/26";

    // Auto: pick smallest geometry that fits via TrsdosOptimizer.
    var fileSizes = inputs.Where(i => !i.IsDirectory).Select(i => (long)i.ReadContent().Length).ToList();
    var auto = TrsdosOptimizer.Find(fileSizes);

    var tracks = tracksStr == "Auto" || !int.TryParse(tracksStr, out var t) ? auto.Tracks : t;
    var spt = density switch {
      "Single" => 10,
      "Double" => 18,
      _        => auto.SectorsPerTrack,
    };
    w.SetGeometry(tracks, spt);
    w.SetDiskName(diskName);
    w.SetDate(date);

    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  // ── IArchiveModifiable ─────────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by name) files inside an existing TRSDOS image. Uses
  /// <see cref="TrsdosModifier"/> for genuine O(touched bytes) in-place I/O —
  /// only the GAT, the affected directory sector, and the new file's
  /// granule-aligned data run are touched.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs)) {
      TrsdosModifier.RemoveFile(archive, name, wipeData: true);
      TrsdosModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries in place: frees their granules in the GAT,
  /// wipes the data, and clears the directory records.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      TrsdosModifier.RemoveFile(archive, name, wipeData: true);
  }

  // ── IArchiveDefragmentable ─────────────────────────────────────────────

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    // Capture source geometry so the rebuilt image is the same physical size
    // — otherwise the optimizer would shrink the disk and violate the defrag
    // invariant (stream.Length must not change).
    var sourceLength = archive.Length;
    int srcSpt;
    archive.Position = 0;
    using (var probe = new TrsdosReader(archive)) {
      srcSpt = probe.SectorsPerTrack > 0 ? probe.SectorsPerTrack : 18;
    }
    var srcTracks = srcSpt > 0 ? (int)(sourceLength / (TrsdosReader.SectorSize * (long)srcSpt)) : 40;
    if (srcTracks < 1) srcTracks = 40;
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new TrsdosReader(stream);
        return r.Entries.Where(e => !e.IsDirectory)
                        .Select(e => (e.Name, r.Extract(e)))
                        .ToList();
      },
      buildImage: files => {
        var w = new TrsdosWriter();
        // Reuse the source disk geometry. Without this the optimizer would
        // pick the smallest viable geometry and shrink the image.
        w.SetGeometry(srcTracks, srcSpt);
        foreach (var (n, d) in files) w.AddFile(n, d);
        var built = w.Build();
        // Pad to source length if the writer emits a slightly smaller buffer.
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
    => TrsdosExtentMap.Enumerate(image);

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all sectors not claimed by track 17 (directory metadata) or by
  /// a live file's contiguous sector run. Cluster-tip wiping (trailing
  /// slack inside the file's last sector) honours the directory's EOF
  /// byte count when <paramref name="wipeClusterTips"/> is true.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? sizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new TrsdosReader(image);
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
    var extents = TrsdosExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, sizeLookup);
  }
}
