#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Human68k;

/// <summary>
/// Descriptor for Sharp X68000 Human68k disk images. The Human68k
/// filesystem is FAT12-derived with Shift_JIS filenames and an "X68K"
/// identifier at boot-sector offset 0x10. Recognised extension is
/// <c>.dim</c> (Disk Image Manager); the <c>.hdf</c> extension that
/// Human68k historically used is intentionally NOT claimed here — it
/// collides with the more-common HDF4 scientific data format, which
/// owns <c>.hdf</c> in this registry.
///
/// <para>Human68k supports subdirectories per the FAT12 model; the
/// current minimal writer emits a single flat root directory only —
/// hierarchical writes are deferred. The reader handles subdirectory
/// dirents at the root by surfacing them as entries with
/// <see cref="ArchiveEntryInfo.IsDirectory"/> set, but does not recurse
/// into them (kept honest in the descriptor capabilities).</para>
///
/// <para>Capabilities: read + write (flat-only writer), defragment via
/// extract-and-rebuild, free-space wiping driven by the extent map, and
/// creation-options schema for bytes-per-sector / sectors-per-cluster /
/// total-sectors / volume label.</para>
///
/// References:
/// <list type="bullet">
///   <item><description>Sharp / Hudson Soft "Human68k" manuals — the original vendor documentation of the FAT12-derived filesystem</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Human68k</c> — Wikipedia overview</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Sharp_X68000</c> — Wikipedia overview of the host platform</description></item>
/// </list>
/// </summary>
public sealed class Human68kFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
  IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Human68k";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Sharp X68000 Human68k";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  // .2hd is the X68000 high-density-floppy image extension used by HxC /
  // X68k emulators; unambiguous and unclaimed by other formats here. .dim
  // is also commonly seen but is claimed by Gemdos (Atari ST Disk Image),
  // which we keep so existing Gemdos workflows aren't disturbed.
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".2hd";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".2hd", ".dim"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("X68K"u8.ToArray(), Offset: 0x10, Confidence: 0.90),
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
    "Sharp X68000 Human68k FAT-derived filesystem with Shift_JIS filenames; identified by 'X68K' tag at boot offset 0x10.";

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for Human68k creation: bytes per sector, sectors per
  /// cluster, total sectors, and volume label. Sector size is locked at
  /// 512 B for safe interop with the reader's Extract path.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "BytesPerSector",
      DisplayName: "Bytes per sector",
      Kind: FormatOptionKind.Enum,
      Default: "512",
      AllowedValues: ["256", "512", "1024"],
      Description: "Bytes per sector. 512 is the default and safest choice for round-tripping with the reader."),
    FilesystemSchemaPresets.PowerOfTwoSize(
      key: "SectorsPerCluster",
      displayName: "Sectors per cluster",
      min: 1, max: 16,
      defaultLabel: "Auto",
      description: "Sectors per cluster (1, 2, 4, 8 or 16). Auto picks the smallest that fits the file set with <= 5 % slack."),
    new FormatOptionDescriptor(
      Key: "TotalSectors",
      DisplayName: "Total sectors",
      Kind: FormatOptionKind.Integer,
      Default: "0",
      Description: "Total sector count. 0 = auto (sized to fit the file set + minimum metadata)."),
    FilesystemSchemaPresets.VolumeLabel(maxChars: 11),
  ];

  // ── IArchiveFormatOperations ────────────────────────────────────────────

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new Human68kReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new Human68kReader(stream);
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
    var r = new Human68kReader(archive);
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

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var w = new Human68kWriter();
    var bpsStr = options?.GetOption("BytesPerSector", "512") ?? "512";
    if (int.TryParse(bpsStr, out var bps)) w.SetBytesPerSector(bps);

    var spcStr = options?.GetOption("SectorsPerCluster", "Auto") ?? "Auto";
    var fileSizes = inputs.Where(i => !i.IsDirectory).Select(i => (long)i.ReadContent().Length).ToList();
    var auto = Human68kOptimizer.Find(fileSizes);
    var spc = spcStr is "Auto" or "0" ? auto.SectorsPerCluster : FilesystemSchemaPresets.ParseSize(spcStr) / 512;
    if (spc <= 0) spc = auto.SectorsPerCluster;
    w.SetSectorsPerCluster(spc);

    var totalStr = options?.GetOption("TotalSectors", "0") ?? "0";
    if (int.TryParse(totalStr, out var tot) && tot > 0) w.SetTotalSectors(tot);

    var label = options?.GetOption("VolumeLabel", "");
    w.SetVolumeLabel(label);

    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  // ── IArchiveModifiable ─────────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Human68k image.
  /// Tries genuine O(touched bytes) in-place I/O via <see cref="Human68kModifier"/>
  /// (allocate contiguous free clusters, chain the FAT, write the dirent);
  /// only when the disk has no room (no free dir slot / no contiguous run)
  /// does it fall back to a growing rebuild that re-sizes the image.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs)) {
      Human68kModifier.RemoveFile(archive, name, wipeData: true);
      if (!Human68kModifier.TryAddFile(archive, name, data))
        AddViaRebuild(archive, name, data);
    }
  }

  /// <summary>Removes the named entries in place: frees the FAT chain, wipes
  /// the clusters, and marks the dirent deleted (0xE5).</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      Human68kModifier.RemoveFile(archive, name, wipeData: true);
  }

  private void AddViaRebuild(Stream archive, string name, byte[] data) {
    archive.Position = 0;
    List<(string Name, byte[] Data)> keep;
    using (var r = new Human68kReader(archive))
      keep = r.Entries.Where(e => !e.IsDirectory)
        .Select(e => (e.Name, r.Extract(e))).ToList();
    var leaf = Leaf(name);
    keep.RemoveAll(k => Leaf(k.Name).Equals(leaf, StringComparison.OrdinalIgnoreCase));
    keep.Add((name, data));

    var w = new Human68kWriter();
    var sizes = keep.Select(k => (long)k.Data.Length).ToList();
    var layout = Human68kOptimizer.Find(sizes);
    w.SetSectorsPerCluster(layout.SectorsPerCluster);
    foreach (var (n, d) in keep) w.AddFile(n, d);
    var img = w.Build();
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(img);
  }

  private static string Leaf(string name) {
    var s = (name ?? "").Replace('\\', '/');
    var slash = s.LastIndexOf('/');
    return slash >= 0 ? s[(slash + 1)..] : s;
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

    // An X68000 volume is laid out like FAT with several BPB fields at its own
    // offsets, so moving what is out of place is a chain rewrite and one write
    // into a directory entry — cheaper than laying the whole volume down again.
    // What the planner will not commit to falls through to the rebuild below.
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
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new Human68kReader(stream);
        return r.Entries.Where(e => !e.IsDirectory)
                        .Select(e => (e.Name, r.Extract(e)))
                        .ToList();
      },
      buildImage: files => {
        var w = new Human68kWriter();
        var sizes = files.Select(f => (long)f.Data.Length).ToList();
        var layout = Human68kOptimizer.Find(sizes);
        w.SetSectorsPerCluster(layout.SectorsPerCluster);
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  // ── IFilesystemExtentMap ───────────────────────────────────────────────

  /// <summary>
  /// Enumerates the extents.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => Human68kExtentMap.Enumerate(image);

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all bytes not claimed by the boot sector, the FAT, the root
  /// directory, or a live file's cluster run. Cluster-tip wiping uses
  /// the directory entry's file size when
  /// <paramref name="wipeClusterTips"/> is true.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? sizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new Human68kReader(image);
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
    var extents = Human68kExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, sizeLookup);
  }

  /// <summary>
  /// Moves only the files that are out of place, relinking each chain and
  /// repointing its directory entry as the clusters arrive.
  /// </summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Human68kBlockMover();
    mover.Init(archive);

    var extents = Human68kExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    // A file in more than one piece needs its whole chain restated, which this
    // pass cannot do; those volumes are rebuilt instead.
    var runsPerOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var extent in extents) {
      if (extent.Kind != DefragBlockKind.Used || extent.FileName is not { } owner) continue;
      runsPerOwner.TryGetValue(owner, out var count);
      runsPerOwner[owner] = count + 1;
    }
    var fragmented = runsPerOwner.Count(kv => kv.Value > 1);
    if (fragmented > 0)
      throw new NotSupportedException(
        $"Human68k: {fragmented} file(s) are in more than one piece; rebuild the volume instead.");

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.FirstDataByte, archive.Length, mover.ClusterSize,
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
    var postExtents = Human68kExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>The first line of a message, for a one-line progress note.</summary>
  private static string PlannerFallbackLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }

}
