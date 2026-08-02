#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ti99;

/// <summary>
/// Descriptor for Texas Instruments TI-99/4A disks (DSR filesystem) — both
/// sector-dump (.dsk) images and TIFiles wrappers (.tifd, .tifiles). The VIB
/// sits at sector 0 with the "DSK" tag at offset 0x0D; FDIR at sector 1 lists
/// File Descriptor Records.
///
/// <para><b>Flat by spec.</b> The TI-99/4A DSR filesystem has no
/// subdirectories — the FDIR is a flat array of FDR pointers. Hierarchy
/// inputs collapse to their leaf names on write; the hierarchy test exercises
/// flat round-trip only.</para>
///
/// <para><b>Two on-disk wrappers.</b> Choose with the <c>Mode</c> creation
/// option: <c>TIFiles</c> = single-file wrapper (the geometry knobs don't
/// apply); <c>SectorDump</c> = full DSR disk image with VIB + FDIR + FDR per
/// file + the data area.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.unige.ch/medecine/nouspikel/ti99/disks.htm</c> — Thierry Nouspikel's TI-99/4A Tech Pages — disk structure (VIB/FDIR/FDR)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Texas_Instruments_TI-99/4A</c> — Wikipedia article on the platform</description></item>
///   <item><description>TIFiles wrapper format description (TI-99/4A community documentation, e.g. Ninerpedia)</description></item>
/// </list>
/// </summary>
public sealed class Ti99FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  public string Id => "Ti99";
  public string DisplayName => "TI-99/4A DSR";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".tifd";
  public IReadOnlyList<string> Extensions => [".tifd", ".tifiles"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // TIFiles wrapper magic — 0x07 then ASCII "TIFILES" at offset 0.
    new([0x07, 0x54, 0x49, 0x46, 0x49, 0x4C, 0x45, 0x53], Offset: 0, Confidence: 0.95),
    // Sector dump "DSK" tag at offset 0x0D.
    new("DSK"u8.ToArray(), Offset: 0x0D, Confidence: 0.80),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Texas Instruments TI-99/4A DSR filesystem — sector dump (VIB + FDIR + FDR) or single-file TIFiles wrapper; flat (no subdirs).";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Mode",
      DisplayName: "Mode",
      Kind: FormatOptionKind.Enum,
      Default: "SectorDump",
      AllowedValues: ["SectorDump", "TIFiles"],
      Description: "SectorDump = full DSR disk image (multi-file); TIFiles = single-file wrapper (geometry knobs are ignored)."),
    new FormatOptionDescriptor(
      Key: "Tracks",
      DisplayName: "Tracks per side",
      Kind: FormatOptionKind.Enum,
      Default: "40",
      AllowedValues: ["35", "40", "80"],
      Description: "35 or 40 for standard floppies, 80 for high-density.",
      DependsOn: "Mode=SectorDump"),
    new FormatOptionDescriptor(
      Key: "Sectors",
      DisplayName: "Sectors per track",
      Kind: FormatOptionKind.Enum,
      Default: "9",
      AllowedValues: ["8", "9", "18"],
      Description: "9 = SD, 18 = DD (256-byte sectors).",
      DependsOn: "Mode=SectorDump"),
    new FormatOptionDescriptor(
      Key: "Sides",
      DisplayName: "Sides",
      Kind: FormatOptionKind.Enum,
      Default: "2",
      AllowedValues: ["1", "2"],
      Description: "1 = single-sided, 2 = double-sided.",
      DependsOn: "Mode=SectorDump"),
    new FormatOptionDescriptor(
      Key: "DiskName",
      DisplayName: "Disk name",
      Kind: FormatOptionKind.String,
      Default: "DISK",
      Description: "Disk name in the VIB (10 chars, space-padded, uppercased).",
      DependsOn: "Mode=SectorDump"),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new Ti99Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new Ti99Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

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
    var r = new Ti99Reader(archive);
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
    options ??= new FormatCreateOptions();

    var mode = options.GetOption("Mode", "SectorDump");
    var w = new Ti99Writer();
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);

    byte[] img;
    if (mode.Equals("TIFiles", StringComparison.OrdinalIgnoreCase)) {
      img = w.BuildTifiles();
    } else {
      var tracks = options.GetOptionInt("Tracks", 40);
      var sectors = options.GetOptionInt("Sectors", 9);
      var sides = options.GetOptionInt("Sides", 2);
      var diskName = options.GetOption("DiskName", "DISK");
      img = w.BuildSectorDump(tracks, sectors, sides, diskName);
    }
    output.Write(img);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing TI-99 sector-dump
  /// image. Uses <see cref="Ti99Modifier"/> for genuine O(touched bytes)
  /// in-place I/O — only the VIB (allocation bitmap), the FDIR, the new
  /// file's FDR sector, and its contiguous data run are touched. TIFiles
  /// single-file wrappers have no allocation map, so they fall back to a
  /// full rebuild.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    archive.Position = 0;
    if (Ti99Modifier.IsSectorDump(archive)) {
      foreach (var (name, data) in FilesOnly(inputs)) {
        Ti99Modifier.RemoveFile(archive, name, wipeData: true);
        Ti99Modifier.AddFile(archive, name, data);
      }
      return;
    }
    AddViaRebuild(archive, inputs);
  }

  /// <summary>
  /// Removes the named entries from an existing TI-99 sector-dump image
  /// in place: frees the data + FDR sectors in the VIB bitmap, wipes them,
  /// and clears the FDIR pointer slot. TIFiles wrappers fall back to rebuild.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    archive.Position = 0;
    if (Ti99Modifier.IsSectorDump(archive)) {
      foreach (var name in entryNames)
        Ti99Modifier.RemoveFile(archive, name, wipeData: true);
      return;
    }
    RemoveViaRebuild(archive, entryNames);
  }

  private void AddViaRebuild(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    archive.Position = 0;
    using var r = new Ti99Reader(archive);
    var keep = r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
    var adds = FilesOnly(inputs).ToList();
    var w = new Ti99Writer();
    foreach (var (n, d) in keep)
      if (!adds.Any(a => Leaf(a.Name).Equals(Leaf(n), StringComparison.OrdinalIgnoreCase)))
        w.AddFile(n, d);
    foreach (var (n, d) in adds) w.AddFile(n, d);
    var img = w.BuildTifiles();
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(img);
  }

  private void RemoveViaRebuild(Stream archive, string[] entryNames) {
    archive.Position = 0;
    using var r = new Ti99Reader(archive);
    var keep = r.Entries.Where(e => !e.IsDirectory)
      .Where(e => !entryNames.Any(n => Leaf(n).Equals(Leaf(e.Name), StringComparison.OrdinalIgnoreCase)))
      .Select(e => (e.Name, r.Extract(e))).ToList();
    var w = new Ti99Writer();
    foreach (var (n, d) in keep) w.AddFile(n, d);
    if (keep.Count == 0) return;
    var img = w.BuildTifiles();
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(img);
  }

  private static string Leaf(string name) {
    var s = (name ?? "").Replace('\\', '/');
    var slash = s.LastIndexOf('/');
    return slash >= 0 ? s[(slash + 1)..] : s;
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => Ti99ExtentMap.Enumerate(image);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the disk out again: a file here
    // is one run named by its descriptor, so a move is the copy and a two-byte
    // repack. The pass is kept only if every payload still reads back — it can
    // refuse partway, and the rebuild below is the honest answer when it does.
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd
        or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      var planned = false;
      DefragContentGuard.RunOrRebuild(archive,
        readContents: stream => {
          using var reader = new Ti99Reader(stream);
          return reader.Entries.Where(e => !e.IsDirectory).Select(reader.Extract).ToList();
        },
        inPlace: () => { this.DefragmentWithPlanner(archive, options); planned = true; },
        rebuild: () => planned = false);
      if (planned) return;
      archive.Position = 0;
    }

    // Detect mode once upfront so the rebuilt image matches the source.
    archive.Position = 0;
    var isTifiles = archive.Length >= 8
      && archive.ReadByte() == 0x07
      && archive.ReadByte() == 'T';
    archive.Position = 0;

    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new Ti99Reader(stream);
        return r.Entries.Where(e => !e.IsDirectory)
                        .Select(e => (e.Name, r.Extract(e)))
                        .ToList();
      },
      buildImage: files => {
        var w = new Ti99Writer();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return isTifiles ? w.BuildTifiles() : w.BuildSectorDump();
      });
  }

  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? lookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var r = new Ti99Reader(image);
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in r.Entries) if (!e.IsDirectory) map[e.Name] = e.Size;
        lookup = n => map.TryGetValue(n, out var s) ? s : -1;
      } catch { lookup = null; }
    }

    image.Position = 0;
    var extents = Ti99ExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, lookup);
  }

  /// <summary>
  /// Moves only the files that are out of place, repointing each one's
  /// descriptor as its sectors arrive.
  /// </summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Ti99BlockMover();

    var extents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    var runsPerOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var extent in extents) {
      if (extent.Kind != DefragBlockKind.Used || extent.FileName is not { } owner) continue;
      runsPerOwner.TryGetValue(owner, out var count);
      runsPerOwner[owner] = count + 1;
    }
    var fragmented = runsPerOwner.Count(kv => kv.Value > 1);
    if (fragmented > 0)
      throw new NotSupportedException(
        $"TI-99: {fragmented} file(s) are in more than one piece, which this pass cannot restate.");

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.FirstDataByte, archive.Length, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);
    if (moves.Count == 0) return;

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves,
      archive.Length, reinitAfterMove: null);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

}
