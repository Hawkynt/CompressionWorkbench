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
///
/// References:
/// <list type="bullet">
///   <item><description>Cromemco RDOS Instruction Manual (Cromemco Inc.) — the original vendor documentation</description></item>
///   <item><description><c>https://bitsavers.org/pdf/cromemco/</c> — Bitsavers' scanned Cromemco manual archive</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Cromemco</c> — Wikipedia overview of the machines</description></item>
/// </list>
/// </summary>
public sealed class CromemcoFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
  IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Cromemco";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Cromemco RDOS";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".rdos";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".rdos", ".crom"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // We only emit one magic — the "CROMEMCO" 8-byte ASCII tag at the most
    // common offset (0x0B). Reader scans the first 64 bytes for tolerance.
    new("CROMEMCO"u8.ToArray(), Offset: 0x0B, Confidence: 0.90),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
    /// <summary>
  /// Gets the description.
  /// </summary>
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

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new CromemcoReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new CromemcoReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  // ── IArchiveCreatable ───────────────────────────────────────────────────

    /// <summary>
  /// Performs the create operation.
  /// </summary>
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

  // ── IArchiveModifiable ─────────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Cromemco RDOS image
  /// using <see cref="CromemcoModifier"/> for genuine O(touched bytes)
  /// in-place I/O — only the directory area and the new file's contiguous
  /// data run are touched.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs)) {
      CromemcoModifier.RemoveFile(archive, name, wipeData: true);
      CromemcoModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes the named entries in place: marks the directory entry
  /// deleted (user code 0xE5) and wipes the data run.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      CromemcoModifier.RemoveFile(archive, name, wipeData: true);
  }

  // ── IArchiveDefragmentable ─────────────────────────────────────────────

    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the volume out again: a file
    // here is one contiguous run named by its directory entry, so a move is the
    // copy and one write. What the planner will not commit to falls through to
    // the rebuild below.
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd
        or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch (Exception planFailure) {
        options.OnProgress?.Invoke(new DefragProgressEvent(
          "fallback", 0, -1, -1, archive.Length, null,
          $"In-place planning declined ({planFailure.GetType().Name}: " +
          $"{PlannerFallbackLine(planFailure.Message)}); rebuilding instead"));
        archive.Position = 0;
      }
    }
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

    /// <summary>
  /// Enumerates the extents.
  /// </summary>
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

  /// <summary>
  /// Moves only the files that are out of place, repointing each one's
  /// directory entry as its run arrives.
  /// </summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new CromemcoBlockMover();
    mover.Init(archive);

    var extents = CromemcoExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.FirstDataByte, archive.Length, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves,
      archive.Length, reinitAfterMove: null);

    archive.Position = 0;
    var postExtents = CromemcoExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>The first line of a message, for a one-line progress note.</summary>
  private static string PlannerFallbackLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }

}
