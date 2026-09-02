#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Mfs1;

/// <summary>
/// Read-only descriptor for Acorn MFS-1 (Master File System v1) disk images —
/// the catalog-compatible evolution of Acorn DFS used on early Acorn / BBC Master
/// systems. The on-disk catalog matches DFS (256-byte sectors, two-sector catalog
/// at track 0 sectors 0-1, up to 31 entries with 7-char names + 1-char directory),
/// so MFS-1 is parsed by walking those sectors directly.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://beebwiki.mdfs.net/Acorn_DFS_disc_format</c> — the DFS catalog layout MFS-1 shares (two-sector catalog at track 0)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Disc_Filing_System</c> — Wikipedia article on the DFS family</description></item>
///   <item><description>Acorn "Disc Filing System User Guide" (vendor manual)</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Detection</b>: weak — magic is the optional <c>0x00 0x80</c> boot
/// pattern at offsets 0-1, low confidence (0.20). Stronger magic'd formats win.
/// Real detection is extension-led (<c>.mfs</c> / <c>.mfsd</c>).</para>
/// <para><b>Write</b> is supported via the DFS-tier catalog layout (sector 0
/// names + sector 1 metadata + contiguous data area from sector 2 onwards).
/// Writer emits a self-consistent catalog with packed-high-bits encoding;
/// the in-place modifier re-packs through the same writer so the outer
/// sector count is preserved.</para>
/// <para>Distinct from <c>FileSystem.Mfs</c>, which targets the Macintosh File
/// System with a strong <c>0xD2D7</c> magic.</para>
/// </remarks>
public sealed class Mfs1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The writer-honoured knob is the 12-char disk title, stored across the
  /// two-sector DFS catalog (first 8 chars in sector 0, last 4 in sector 1).
  /// The image geometry defaults to the 200 KB 80-track SSD and is not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 12),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Mfs1";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "MFS-1 (Acorn Master File System v1)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  // WORM, not R/W: Add/Remove rebuild the whole image (read-all -> re-create),
  // so the verb works via rebuild but nothing is modified in place. CanModify
  // must not be advertised. See Compression.Registry/FormatCapabilities.cs.
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  // ".mfs" is shared with the classic Mac MFS filesystem (FileSystem.Mfs, strong
  // 0xD2D7 magic) whose reader rejects an Acorn MFS-1 image. MFS-1's own boot
  // pattern is a weak 0x0080, so detection is extension-driven; default to the
  // Mfs1-unique ".mfsd" so a freshly-written image re-detects as Mfs1, not Mfs.
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".mfsd";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".mfsd", ".mfs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0x00 0x80 at offsets 0-1 is the MFS-1 boot pattern. Extremely weak
    // (matches many empty/sparse buffers) so confidence 0.20 lets stronger
    // magic-bearing formats win — detection here is really driven by the
    // .mfs / .mfsd extension.
    new([0x00, 0x80], Offset: 0, Confidence: 0.20),
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
  public string Description => "Acorn MFS-1 (BBC Master) — DFS-tier catalog walker with in-place R/W (Mfs1Writer + Mfs1InPlaceModifier).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.mfs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    // Real catalog entries when the catalog parses. The whole-image and
    // metadata surfaces are for a volume nothing can be read out of: listed
    // alongside real files they are fed back into the writer on a rebuild,
    // which then needs the volume's own size again and refuses for want of
    // space.
    var catalog = new List<ArchiveEntryInfo>();
    try {
      var r = new Mfs1Reader(image);
      foreach (var e in r.Entries)
        catalog.Add(new ArchiveEntryInfo(catalog.Count, e.FullName, e.Size, e.Size, "stored", false, e.IsLocked, null));
    } catch {
      // best-effort: opaque-only surface
    }

    if (catalog.Count > 0) return catalog;

    entries.Add(new ArchiveEntryInfo(0, "FULL.mfs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    var ok = image.Length >= 2 && image[0] == 0x00 && image[1] == 0x80;
    var label = TryExtractLabel(image);

    // Catalog walk + per-file extract.
    var entriesParsed = 0;
    try {
      var r = new Mfs1Reader(image);
      foreach (var e in r.Entries) {
        if (files != null && files.Length > 0 && !MatchesFilter(e.FullName, files)) continue;
        var data = r.Extract(e);
        WriteFile(outputDir, e.FullName, data);
      }
      entriesParsed = r.Entries.Count;
      if (string.IsNullOrEmpty(label)) label = r.DiskTitle;
    } catch {
      // best-effort
    }

    // The whole image is only worth writing out when nothing was read from it.
    if (entriesParsed == 0)
      WriteIfMatch(outputDir, "FULL.mfs", image, files);

    // Likewise the metadata note: written next to real files, it becomes a
    // real file of its own the next time the volume is rebuilt from what was
    // extracted.
    if (entriesParsed > 0) return;

    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(ok ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"detected_label={label}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"catalog_entries={entriesParsed}\n");
    bldr.Append("note=Acorn MFS-1 catalog walk produced no entries; image surfaced as opaque blob.\n");
    WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(bldr.ToString()), files);
  }

  /// <summary>
  /// Opens a single catalog entry as a bounded stream over its sector extent.
  /// Reads past <see cref="Mfs1Entry.Size"/> return 0 (EOF). The FULL/metadata
  /// placeholders are intentionally NOT openable via this path; use Extract.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    try {
      var image = ReadAll(archive);
      var r = new Mfs1Reader(image);
      foreach (var e in r.Entries) {
        if (!string.Equals(e.FullName, entryName, StringComparison.OrdinalIgnoreCase)
          && !string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
        var data = r.Extract(e);
        return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
      }
    } catch {
      // fall through
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  private static string TryExtractLabel(byte[] image) {
    if (image.Length < 14) return "";
    Span<char> chars = stackalloc char[12];
    var count = 0;
    for (var i = 2; i < 14; i++) {
      var b = image[i];
      if (b is < 0x20 or > 0x7E) break;
      chars[count++] = (char)b;
    }
    return new string(chars[..count]);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  /// <summary>
  /// Creates a fresh MFS-1 image from the given inputs. The catalog and
  /// data area are emitted by <see cref="Mfs1Writer"/>; the resulting
  /// image is round-trip-readable by <see cref="Mfs1Reader"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new Mfs1Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    output.Write(string.IsNullOrEmpty(label) ? w.Build() : w.Build(label));
  }

  /// <summary>
  /// Adds — or replaces by name — files in an existing MFS-1 image. The
  /// image is re-packed via <see cref="Mfs1InPlaceModifier.AddFiles"/>
  /// from its existing files plus the new ones; the outer sector count
  /// is preserved.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var pairs = new List<(string Name, byte[] Data)>();
    foreach (var (name, data) in FlatFiles(inputs))
      pairs.Add((name, data));
    Mfs1InPlaceModifier.AddFiles(archive, pairs);
  }

  /// <summary>
  /// Removes the named entries from an existing MFS-1 image. The data
  /// area of the removed file is zeroed on rebuild — no forensic trace
  /// of the removed bytes remains.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    Mfs1InPlaceModifier.RemoveFiles(archive, entryNames);
  }

  private const int HeaderReadCap = 1 << 20; // 1 MiB cap — a 40-track SSD is 100k, 80-track is 200k.

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }

  // ── IArchiveDefragmentable ─────────────────────────────────────────────

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Lays the disk out again. A file here is one run of sectors and its catalog
  /// slot records where that run starts, so a move is the copy plus two bytes —
  /// cheaper than reading every file out and writing a fresh disk, which is
  /// what the inherited default did for the one mode it offered.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // The in-place pass is kept only if every payload still reads back: it can
    // refuse partway, and a rebuild is the honest answer when it does.
    DefragContentGuard.RunOrRebuild(archive,
      readContents: stream => ReadEntries(stream).Select(e => e.Data).ToList(),
      inPlace: () => this.DefragmentWithPlanner(archive, options),
      rebuild: () => DefragRebuilder.Rebuild(archive, options,
        readEntries: stream => ReadEntries(stream),
        buildImage: files => {
          var writer = new Mfs1Writer();
          foreach (var (name, data) in files) writer.AddFile(name, data);
          var built = writer.Build();
          if (built.Length >= archive.Length) return built;
          var padded = new byte[archive.Length];
          Array.Copy(built, padded, built.Length);
          return padded;
        }));
  }

  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Mfs1BlockMover();
    mover.Init(archive);

    var extents = Mfs1ExtentMap.Enumerate(archive).ToList();
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
    var postExtents = Mfs1ExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>Every file's name and bytes, for the rebuild and the guard.</summary>
  private static List<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var reader = new Mfs1Reader(stream);
    return reader.Entries.Select(e => (e.FullName, reader.Extract(e))).ToList();
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => Mfs1ExtentMap.Enumerate(image);

  /// <summary>
  /// Zero-fills every sector no catalog slot claims — which is where a removed
  /// file's bytes stay until something else takes them.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = Mfs1ExtentMap.Enumerate(image).ToList();
    if (extents.Count == 0) return 0;

    Func<string, long>? sizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new Mfs1Reader(image);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries) sizes[entry.FullName] = entry.Size;
        sizeLookup = name => sizes.TryGetValue(name, out var size) ? size : -1;
      } catch {
        sizeLookup = null;
      }
    }

    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length, wipeClusterTips, sizeLookup);
  }
}
