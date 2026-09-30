#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.HfsPlus;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>https://developer.apple.com/library/archive/technotes/tn/tn1150.html</c> — Apple Technical Note TN1150 "HFS Plus Volume Format", the canonical spec (incl. HFSX and the journal)</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/hfsplus</c> — Linux kernel implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/HFS_Plus</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class HfsPlusFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// HFS+ creation knobs: HFSX case-sensitivity toggle, journal enable +
  /// journal-size selector, volume name and allocation block size. The block
  /// size dropdown offers Auto (slack + table-overhead minimisation) plus the
  /// power-of-two sizes 4 KB … 64 KB that the writer supports; the
  /// journal-size knob is gated on Journal=true via DependsOn.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "CaseSensitive", DisplayName: "Case-sensitive (HFSX)", Kind: FormatOptionKind.Boolean, Default: "false",
      Description: "Make filename comparison case-sensitive (emit the HFSX 'HX' signature + binary comparator)."),
    new FormatOptionDescriptor(
      Key: "Journal", DisplayName: "Enable journal", Kind: FormatOptionKind.Boolean, Default: "true",
      Description: "Enable the volume journal."),
    new FormatOptionDescriptor(
      Key: "JournalSize", DisplayName: "Journal size", Kind: FormatOptionKind.Integer, Default: "8388608",
      AllowedValues: ["8388608", "16777216", "33554432", "67108864"],
      Description: "Journal size in bytes (8/16/32/64 MiB).",
      DependsOn: "Journal=true"),
    FilesystemSchemaPresets.VolumeLabel(),
    FilesystemSchemaPresets.ClusterSize(
      key: "BlockSize",
      displayName: "Allocation block size",
      min: 4096, max: 65536,
      description: "HFS+ allocation block size (power of two, 4 KB … 64 KB). " +
        "Auto picks the size that minimises slack + allocation-bitmap and B-tree overhead."),
  ];

  /// <summary>
  /// Walks the HFS+ catalog B-tree leaf chain and yields the actual on-disk
  /// byte layout — reserved boot region + volume header + allocation file +
  /// catalog file as <see cref="DefragBlockKind.MetadataReserved"/>, every
  /// file record's first data-fork extent
  /// (<c>HFSPlusForkData.extents[0]</c>) as
  /// <see cref="DefragBlockKind.Used"/>.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => HfsPlusExtentMap.Enumerate(image);

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in the HFS+ image: free allocation blocks, gaps
  /// between files and the block-tip slack between a file's logical size and
  /// the end of its last allocated block. The catalog extent map clamps each
  /// file's first-fork run to its logical byte length, so trailing slack
  /// inside the final block presents as a free gap that the generic
  /// <see cref="UnusedSpaceWiper"/> zero-fills. The size lookup is keyed by the
  /// reader's full path, matching the extent map's FileName.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new HfsPlusReader(image, leaveOpen: true);
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries)
          if (!entry.IsDirectory)
            sizeMap[entry.FullPath] = entry.Size;
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = HfsPlusExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new HfsPlusBlockMover();
    image.Position = 0;
    mover.Init(image); // reads only the 512-byte volume header
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new HfsPlusBlockMover();
    image.Position = 0;
    mover.Init(image); // reads only the 512-byte volume header
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "HfsPlus";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "HFS+";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing HFS+ image via
  /// <see cref="HfsPlusModifier.AddFile"/>. The modifier mutates the catalog
  /// leaf, allocation bitmap, and volume header in place; what it cannot express is
  /// refused.
  /// </summary>
  /// <remarks>
  /// Only the root folder of a single-leaf catalog is edited in place. A path into a
  /// folder is refused rather than flattened to its leaf name, and so is a volume the
  /// in-place editor cannot handle — there is no rebuild behind it any more.
  /// </remarks>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    RequireInPlaceSize(archive);
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      if (input.ArchiveName.Replace('\\', '/').Trim('/').Contains('/'))
        throw new NotSupportedException(
          $"HFS+: '{input.ArchiveName}' is inside a folder; only the root folder is edited in place.");
    foreach (var (name, data) in FlatFiles(inputs))
      HfsPlusModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// The in-place editor holds the volume in memory; larger volumes are refused
  /// rather than unpacked and written out again without their metadata.
  /// </summary>
  private static void RequireInPlaceSize(Stream archive) {
    if (ModifyRebuilder.NeedsLargeVolumePath(archive))
      throw new NotSupportedException(
        $"HFS+: in-place editing holds the volume in memory; a {archive.Length:N0}-byte volume is refused.");
  }

  /// <summary>
  /// Removes the named entries from an existing HFS+ image via
  /// <see cref="HfsPlusModifier.RemoveFile"/>. File data blocks are wiped and
  /// the catalog records are excised from the leaf node; a missing name is reported.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    RequireInPlaceSize(archive);
    foreach (var raw in entryNames ?? []) {
      var name = raw.Replace('\\', '/').Trim('/');
      if (name.Contains('/'))
        throw new NotSupportedException($"HFS+: '{raw}' is inside a folder; only the root folder is edited in place.");
      if (!HfsPlusModifier.RemoveFile(archive, name, wipeData: true))
        throw new FileNotFoundException($"HFS+: '{raw}' is not in the root folder.", raw);
    }
  }
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".dmg";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".dmg", ".hfsx", ".hfs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x48, 0x2B], Offset: 1024, Confidence: 0.85)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("hfsplus", "HFS+")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Apple HFS+ filesystem image. Writer emits full 248-byte TN1150
  /// HFSPlusCatalogFile records with HFSPlusForkData at offsets 88/168.
  /// </summary>
  public string Description => "Apple HFS+ filesystem image";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new HfsPlusReader(stream, leaveOpen: true);
    var entries = r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "Stored", e.IsDirectory, false, e.LastModified,
      Kind: null, IsSymlink: e.IsSymlink, LinkTarget: e.LinkTarget)).ToList();
    return SymlinkResolver.Resolve(entries);
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
    var r = new HfsPlusReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.FullPath, entryName, StringComparison.OrdinalIgnoreCase)) continue;
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
    var caseSensitive = options.GetOptionBool("CaseSensitive", false);
    var journal = options.GetOptionBool("Journal", true);
    var journalSize = options.GetOptionInt("JournalSize", 8 * 1024 * 1024);
    // Honour VolumeLabel (preset key) first; fall back to VolumeName for the
    // earlier stash schema; finally default to "Untitled".
    var volumeName = options.GetOption("VolumeLabel", options.GetOption("VolumeName", "Untitled"));

    var w = new HfsPlusWriter(caseSensitive, journal, journalSize, volumeName);
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the volume out; reading a large input
      // into a byte[] would cap the volume at what an array can hold.
      var name = Path.GetFileName(info.ArchiveName);
      if (info.InMemoryContent is { } bytes)
        w.AddFile(name, bytes);
      else
        w.AddStreamingFile(name, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
    }

    // "BlockSize" → bytes (0 = Auto). The writer's optimizer confirms or bumps.
    var blockSize = FilesystemSchemaPresets.ParseSize(
      options.GetString("BlockSize"));
    if (output.CanSeek) w.BuildToStreamingAutoSized(output, blockSize);
    else output.Write(w.BuildAutoSized(blockSize));
  }

  /// <summary>
  /// Two-pass streaming creation: pre-known per-input sizes drive
  /// allocation-block geometry + catalog B-tree planning in pass 1;
  /// pass 2 emits the volume header + allocation bitmap + extents B-tree
  /// + catalog B-tree (with file records pointing at single-extent
  /// allocations) + the alternate volume header, then streams each
  /// input's bytes from its
  /// <see cref="Compression.Registry.Streaming.StreamingArchiveInput.OpenStream"/>
  /// factory into its allocated extent run via 64 KB chunks. Block tail
  /// past each entry's exact <c>Size</c> stays sparse-zero.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var caseSensitive = options.GetOptionBool("CaseSensitive", false);
    var journal = options.GetOptionBool("Journal", true);
    var journalSize = options.GetOptionInt("JournalSize", 8 * 1024 * 1024);
    var volumeName = options.GetOption("VolumeLabel", options.GetOption("VolumeName", "Untitled"));
    var w = new HfsPlusWriter(caseSensitive, journal, journalSize, volumeName);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    var blockSize = FilesystemSchemaPresets.ParseSize(
      options.GetString("BlockSize"));
    if (output.CanSeek) {
      w.BuildToStreamingAutoSized(output, blockSize);
      return;
    }
    output.Write(w.BuildAutoSized(blockSize));
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new HfsPlusReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  /// <inheritdoc/>
  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new HfsPlusBlockMover();
    mover.Init(archive);

    archive.Position = 0;
    var extents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.FirstDataByte, archive.Length, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement,
      movableMetadata: (mover as IFilesystemMetadataMover)?.RelocatableMetadata);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves,
      archive.Length, reinitAfterMove: null, metadataMover: mover as IFilesystemMetadataMover);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();

    // Whichever runs moved, the bitmap is right only once they all have: a
    // run's old home is routinely another run's new one.
    mover.SettleAllocationBitmap(archive, postExtents
      .Where(e => e.Kind != DefragBlockKind.Free)
      .Select(e => (e.Offset, e.Length)));

    archive.Position = 0;
    postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>Every file's bytes, for the guard to compare across the pass.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    var reader = new HfsPlusReader(stream, leaveOpen: true);
    return reader.Entries.Where(e => !e.IsDirectory).Select(reader.Extract).ToList();
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Defragments in place: the planner moves data-fork runs and the mover repoints
  /// the extent descriptor that names each one; the pass is kept only if every file
  /// reads back unchanged. Anything else is refused and the volume left as it was —
  /// the rebuild that used to follow renamed the volume "Untitled", dropped the
  /// journal, permissions, Finder info and resource forks, and restamped every date.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    DefragSupport.Require(options, DefragFeature.Packing | DefragFeature.CarveHole, "HFS+");
    if (!archive.CanSeek || archive.Length > MaxBufferedImageBytes)
      throw new NotSupportedException(
        $"HFS+: in-place defragmentation checks the result against a snapshot held in memory; volumes over {MaxBufferedImageBytes:N0} bytes are refused.");
    DefragContentGuard.RunOrRebuild(archive,
      readContents: stream => ReadPayloadsForGuard(stream),
      inPlace: () => DefragmentWithPlanner(archive, options),
      rebuild: () => throw new NotSupportedException("HFS+: the volume cannot be laid out in place; it was left unchanged."));
  }


  /// <summary>Largest volume the in-place defragmentation snapshots.</summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;
}
