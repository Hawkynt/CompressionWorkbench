#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Adfs;

/// <summary>
/// Descriptor for Acorn Advanced Disc Filing System (ADFS) images. Read works
/// for both old-map (S/M/L, 256-byte sectors) and new-map (D/E/F, 1024-byte
/// sectors, fragment-mapped). Create emits a new-map volume by default, which
/// is the layout a real ADFS driver mounts — Linux's has no code path for an
/// old map at all; pass <c>Variant=old</c> for the ADFS-L 640 KB layout.
/// Detected by the "Hugo" or "Nick" directory marker at sector 2 — root dir
/// magic at file offset 0x200 (old map) or 0x400 (new map).
///
/// References:
/// <list type="bullet">
///   <item><description>Acorn "Advanced Disc Filing System User Guide" (Acorn Computers) — the original vendor format documentation</description></item>
///   <item><description>RISC OS Programmer's Reference Manual, FileCore chapter — new-map (D/E/F) on-disk structures</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Advanced_Disc_Filing_System</c> — Wikipedia overview of the ADFS variants</description></item>
/// </list>
/// </summary>
public sealed class AdfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, IFilesystemExtentMap, IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>
  /// Largest disc the in-place pass is offered for. Its guard holds a copy of
  /// the image to compare payloads across the pass; an ADFS disc is far below
  /// this, but a truncated or padded image need not be.
  /// </summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  // ── IFilesystemExtentMap ────────────────────────────────────────────────

  /// <summary>
  /// Where an old-map disc keeps its bytes: the free-space map, the root
  /// directory, and each file's contiguous run of sectors. A new-map disc
  /// describes nothing here — see <see cref="AdfsExtentMap" />.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => AdfsExtentMap.Enumerate(image);

  // ── IArchiveDefragmentable ──────────────────────────────────────────────

  /// <summary>
  /// Lays the disc out again by moving what is out of place. An old-map file
  /// is one contiguous run and its directory entry says which sector it starts
  /// at, so a move is the copy plus three bytes; the free-space map is written
  /// once from the finished layout.
  /// </summary>
  /// <remarks>
  /// New-map discs fall through to the rebuild the default gives: there a file
  /// is a fragment identifier resolved through a zone bitmap, and moving one
  /// means rewriting that map rather than the entry.
  /// </remarks>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    if (archive.CanSeek && archive.Length <= MaxBufferedImageBytes) {
      var planned = false;
      // The in-place pass is kept only if every payload still reads back: it
      // can refuse partway, and a rebuild is the honest answer when it does.
      DefragContentGuard.RunOrRebuild(archive,
        readContents: ReadPayloadsForGuard,
        inPlace: () => { DefragmentWithPlanner(archive, options); planned = true; },
        rebuild: () => planned = false);
      if (planned) return;
      archive.Position = 0;
    }

    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException(
        $"ADFS can only rebuild a disc packed from the start; got {options.Mode}.");

    RebuildVerb.RebuildInPlace(archive, this, this);
  }

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    using var reader = new AdfsReader(stream);
    return reader.Entries.Where(e => !e.IsDirectory).Select(reader.Extract).ToList();
  }

  /// <summary>Plans the new layout and moves the runs into it, repointing as it goes.</summary>
  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new AdfsBlockMover();
    mover.Init(archive);

    archive.Position = 0;
    var extents = AdfsExtentMap.Enumerate(archive).ToList();
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
    var postExtents = AdfsExtentMap.Enumerate(archive).ToList();

    // Whichever runs moved, the free map is right only once they all have: a
    // run's old home is routinely another run's new one.
    mover.SettleFreeMap(archive, postExtents
      .Where(e => e.Kind != DefragBlockKind.Free)
      .Select(e => (e.Offset, e.Length)));

    archive.Position = 0;
    postExtents = AdfsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }


  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The writer-honoured knob is the disc title, written as the 19-byte ASCII
  /// title in the root directory tail. The writer always emits the ADFS-L
  /// 640 KiB / 256-byte-sector geometry, so disc size is not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 19),
    new("Variant", "Map variant", FormatOptionKind.Enum, "new",
      AllowedValues: ["new", "old"],
      Description: "new = the E/F-style map a real ADFS driver mounts; " +
        "old = the S/M/L free-space-list layout of a 640 KB ADFS-L floppy."),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Adfs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Acorn ADFS";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".adl";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".adl", ".adf"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "Hugo" at 0x200 (old map S/M/L) — confidence kept moderate because
    // .adf collides with Amiga ADF (which begins with "DOS" at offset 0).
    new([(byte)'H', (byte)'u', (byte)'g', (byte)'o'], Offset: 0x200, Confidence: 0.75),
    new([(byte)'N', (byte)'i', (byte)'c', (byte)'k'], Offset: 0x200, Confidence: 0.75),
    // New map (D/E/F): root dir at 0x400.
    new([(byte)'H', (byte)'u', (byte)'g', (byte)'o'], Offset: 0x400, Confidence: 0.70),
    new([(byte)'N', (byte)'i', (byte)'c', (byte)'k'], Offset: 0x400, Confidence: 0.70),
    // A new-map volume puts its root wherever its map says, so the marker is at
    // no fixed offset; the disc record at sector 0 + 4 is what identifies it.
    // Matched fields: log2secsize (1024-byte sectors), idlen, log2bpmb and a
    // single zone — the geometry AdfsNewMapWriter emits.
    new([0x0A, 0, 0, 0, 0x0D, 0x0A, 0, 0, 0, 0x01],
      Offset: 4, Confidence: 0.80,
      Mask: [0xFF, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0xFF]),
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
public string Description => "Acorn ADFS (BBC Micro / Archimedes / RISC OS) filesystem — read + R/W (ADFS-L variant; in-place Add/Remove against the old-map FSM and Hugo-bracketed root directory).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new AdfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new AdfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the open entry operation.
  /// </summary>
public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new AdfsReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  /// <summary>
  /// Performs the extract entry to memory operation.
  /// </summary>
public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  // ── IArchiveCreatable (WORM) ─────────────────────────────────────────────

  /// <summary>
  /// Emits a fresh ADFS-L disc image (640 KiB, old-map, 256-byte sectors)
  /// containing the supplied inputs at the root directory. Capacity is
  /// validated up-front against the 2 553 usable data sectors (total 2 560
  /// minus 2 for the FSM and 5 for the root directory).
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var title = options?.GetOption("VolumeLabel", "") ?? "";
    var variant = options?.GetOption("Variant", "new") ?? "new";

    if (variant.Equals("old", StringComparison.OrdinalIgnoreCase)) {
      var oldWriter = new AdfsWriter();
      if (!string.IsNullOrEmpty(title)) oldWriter.DiscTitle = title;
      foreach (var (name, data) in FlatFiles(inputs))
        oldWriter.AddFile(name, data);
      output.Write(oldWriter.Build());
      return;
    }

    var writer = new AdfsNewMapWriter();
    if (!string.IsNullOrEmpty(title)) writer.DiscTitle = title;
    foreach (var (name, data) in FlatFiles(inputs))
      writer.AddFile(name, data);
    output.Write(writer.Build());
  }

  /// <summary>True when the image at hand carries a new-map disc record.</summary>
  private static bool IsNewMap(Stream archive) {
    var position = archive.CanSeek ? archive.Position : 0;
    try {
      if (archive.CanSeek) archive.Position = 0;
      using var reader = new AdfsReader(archive);
      return reader.IsNewMap;
    } catch {
      return false;
    } finally {
      if (archive.CanSeek) archive.Position = position;
    }
  }

  /// <summary>
  /// Rewrites a new-map image from its current contents plus/minus the given
  /// changes. The in-place modifier speaks the old map's free-space list, so a
  /// new-map volume is rebuilt instead.
  /// </summary>
  private static void RebuildNewMap(Stream archive, IReadOnlyList<(string Name, byte[] Data)> add,
      string[] remove) {
    var files = new List<(string Name, byte[] Data)>();
    archive.Position = 0;
    using (var reader = new AdfsReader(archive)) {
      foreach (var e in reader.Entries) {
        if (e.IsDirectory) continue;
        if (remove.Any(n => string.Equals(n, e.Name, StringComparison.OrdinalIgnoreCase))) continue;
        if (add.Any(a => string.Equals(a.Name, e.Name, StringComparison.OrdinalIgnoreCase))) continue;
        files.Add((e.Name, reader.Extract(e)));
      }
    }
    files.AddRange(add);

    var writer = new AdfsNewMapWriter();
    foreach (var (name, data) in files)
      writer.AddFile(name, data);
    var image = writer.Build();

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
    archive.Flush();
  }

  // ── IArchiveModifiable (R/W) ────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by name) files inside an existing ADFS-L image. Uses
  /// <see cref="AdfsModifier"/> for in-place mutation against the old-map
  /// FSM and Hugo-bracketed root directory — only the FSM sectors, the root
  /// directory, and the file's freshly-allocated data sectors are touched.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    if (IsNewMap(archive)) {
      RebuildNewMap(archive, FlatFiles(inputs).ToList(), []);
      return;
    }
    foreach (var (name, data) in FlatFiles(inputs)) {
      AdfsModifier.RemoveFile(archive, name);
      AdfsModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing ADFS-L image. Each entry's
  /// data sectors are wiped and returned to the FSM with adjacent-region
  /// merging, and the root directory's entry slot is compacted so the
  /// trailing zero sentinel re-engages.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (IsNewMap(archive)) {
      RebuildNewMap(archive, [], entryNames);
      return;
    }
    foreach (var name in entryNames)
      AdfsModifier.RemoveFile(archive, name);
  }
}
