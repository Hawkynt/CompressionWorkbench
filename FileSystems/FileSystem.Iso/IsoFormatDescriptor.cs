#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Iso;

/// <summary>
/// Format descriptor for ISO 9660 optical disc images.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://ecma-international.org/publications-and-standards/standards/ecma-119/</c> — ECMA-119 (the freely available equivalent of ISO 9660), the defining standard</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/isofs</c> — Linux kernel implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/ISO_9660</c> — Wikipedia overview (incl. Joliet / Rock Ridge extensions)</description></item>
/// </list>
/// </summary>
public sealed class IsoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for ISO 9660 creation: ECMA-119 volume identifier, system
  /// identifier, publisher, application, plus the Joliet extension toggle.
  /// All identifier fields follow the ECMA-119 d/a-character rules and are
  /// truncated to the field length defined by the spec (32 for vol/sys,
  /// 128 for publisher/application).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "VolumeLabel",
      DisplayName: "Volume identifier",
      Kind: FormatOptionKind.String,
      Default: "CDROM",
      Description: "ECMA-119 Volume Identifier shown by file managers as the disc " +
        "label. Max 32 d-characters (A-Z, 0-9, _)."),
    new FormatOptionDescriptor(
      Key: "SystemId",
      DisplayName: "System identifier",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "ECMA-119 System Identifier. Max 32 a-characters."),
    new FormatOptionDescriptor(
      Key: "Publisher",
      DisplayName: "Publisher",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "ECMA-119 Publisher Identifier. Max 128 a-characters."),
    new FormatOptionDescriptor(
      Key: "Application",
      DisplayName: "Application",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "ECMA-119 Application Identifier. Max 128 a-characters."),
    new FormatOptionDescriptor(
      Key: "Joliet",
      DisplayName: "Joliet (long names)",
      Kind: FormatOptionKind.Boolean,
      Default: "true",
      Description: "Emit a Joliet Supplementary Volume Descriptor with a parallel " +
        "UCS-2 directory tree preserving long/mixed-case filenames. Disable for " +
        "strict ECMA-119 8.3 uppercase only."),
  ];

  /// <summary>
  /// Walks the 32 KiB system area, the volume descriptor sequence, the path
  /// tables, and the directory tree, and yields each file's contiguous
  /// extent (LBA, length) as a single Used run — ECMA-119 mandates contiguous
  /// allocation per file. Directories surface as MetadataReserved.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => IsoExtentMap.Enumerate(image);

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in the ISO 9660 image: the unused remainder of the
  /// system area, free sectors and the sector-tip slack at the tail of each
  /// file's last 2048-byte sector. ECMA-119 stores every file contiguously and
  /// pads its final sector with zeros — the bytes between the file's logical
  /// length and the sector boundary are the cluster tip. The extent map clamps
  /// each Used run to the file's logical length, so the tip presents as a free
  /// gap that the generic <see cref="UnusedSpaceWiper"/> zero-fills. The size
  /// lookup is keyed by the reader's full path, matching the extent FileName.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new IsoReader(image, leaveOpen: true);
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries)
          if (!entry.IsDirectory)
            sizeMap[entry.Name] = entry.Size;
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = IsoExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
    /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new IsoBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new IsoBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Iso";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "ISO 9660";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc/>
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <inheritdoc/>
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".iso";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".iso"];
  /// <inheritdoc/>
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc/>
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("CD001"u8.ToArray(), Offset: 0x8001, Confidence: 0.95),
    new("CD001"u8.ToArray(), Offset: 0x8801, Confidence: 0.90),
    new("CD001"u8.ToArray(), Offset: 0x9001, Confidence: 0.85),
  ];
  /// <inheritdoc/>
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <inheritdoc/>
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <inheritdoc/>
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "ISO 9660 optical disc image";

  /// <inheritdoc/>
    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new IsoReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  /// <inheritdoc/>
    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new IsoWriter {
      VolumeIdentifier      = options?.GetOption("VolumeLabel", "CDROM") ?? "CDROM",
      SystemIdentifier      = options?.GetOption("SystemId", "") ?? "",
      PublisherIdentifier   = options?.GetOption("Publisher", "") ?? "",
      ApplicationIdentifier = options?.GetOption("Application", "") ?? "",
      EnableJoliet          = options?.GetOptionBool("Joliet", true) ?? true,
    };
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the image out; reading a large input
      // into a byte[] would cap the image at what an array can hold.
      var name = Path.GetFileName(info.ArchiveName);
      if (info.InMemoryContent is { } bytes)
        w.AddFile(name, bytes);
      else
        w.AddStreamingFile(name, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
    }
    if (output.CanSeek) w.BuildToStreaming(output);
    else output.Write(w.Build());
  }

  /// <summary>
  /// Two-pass streaming creation: the pre-known per-input sizes drive the
  /// ISO 9660 path table + directory + file-extent layout in pass 1 (identical
  /// to <see cref="Create"/>, which flattens to leaf filenames); pass 2 streams
  /// each file's bytes from its
  /// <see cref="Compression.Registry.Streaming.StreamingArchiveInput.OpenStream"/>
  /// factory into its data extent via 64 KB chunks — no file is ever buffered
  /// as a <c>byte[]</c>. Output is byte-identical to <see cref="Create"/> for
  /// the same inputs (the ECMA-119 volume/record timestamps are sampled once
  /// per <c>Build</c>). Falls back to a buffered build when the target stream
  /// is not seekable.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new IsoWriter {
      VolumeIdentifier      = options?.GetOption("VolumeLabel", "CDROM") ?? "CDROM",
      SystemIdentifier      = options?.GetOption("SystemId", "") ?? "",
      PublisherIdentifier   = options?.GetOption("Publisher", "") ?? "",
      ApplicationIdentifier = options?.GetOption("Application", "") ?? "",
      EnableJoliet          = options?.GetOptionBool("Joliet", true) ?? true,
    };
    if (!output.CanSeek) {
      foreach (var input in inputs) {
        if (input.IsDirectory) continue;
        using var src = input.OpenStream();
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        w.AddFile(Path.GetFileName(input.Name), ms.ToArray());
      }
      output.Write(w.Build());
      return;
    }
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      // Match Create's FlatFiles flattening: ISO records the leaf filename.
      w.AddStreamingFile(Path.GetFileName(input.Name), input.Size, input.OpenStream);
    }
    w.BuildToStreaming(output);
  }

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new IsoReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      // Streamed, not buffered: an ISO entry may be larger than a byte[] can hold.
      using var target = CreateEntryFile(outputDir, e.Name);
      r.ExtractTo(e, target);
    }
  }

  /// <summary>
  /// Opens a single ISO 9660 file entry as a bounded read-only <see cref="Stream"/>.
  /// ISO 9660 stores file data in contiguous extents — the reader's extract
  /// returns those bytes verbatim; they are wrapped in a
  /// <see cref="BoundedEntryStream"/> sized to the entry's logical size.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new IsoReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false),
        bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false),
      0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Adds or replaces files at the root of an existing ISO 9660 image. Uses
  /// <see cref="IsoModifier"/> for true random-access I/O — only the PVD
  /// (sector 16), the root directory's existing extent, and the new file's
  /// data sectors are touched. The 32 KB system area, VDST, path tables, and
  /// existing file data sectors are left untouched. Names are sanitized to
  /// the ISO 9660 8.3 d-characters identifier set; ';1' versions are added
  /// automatically by the modifier.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs))
      IsoModifier.AddFile(archive, name, data);
  }

    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new IsoBlockMover();
    mover.Init(archive);

    archive.Position = 0;
    var descriptor = new IsoFormatDescriptor();
    var extents = descriptor.EnumerateExtents(archive).ToList();
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
    var postExtents = descriptor.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>Every file's bytes, for the guard to compare across the pass.</summary>
  private static IReadOnlyList<byte[]> ReadEntriesForGuard(Stream stream) {
    stream.Position = 0;
    var reader = new IsoReader(stream);
    return reader.Entries.Where(e => !e.IsDirectory).Select(reader.Extract).ToList();
  }

  /// <summary>
  /// Mode-aware ISO 9660 defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. All four <see cref="DefragMode"/> values supported;
  /// image is repacked with files reordered per mode.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the image out again: a file is
    // one contiguous extent by the standard, and its directory record names the
    // sector it starts at, so a move is the copy plus that field.
    //
    // This was tried once and pulled back out, because the mover walked the
    // directory from the root for every move and the cost grew with the square
    // of the move count — a half-megabyte image had not finished planning after
    // ten minutes. The records are located once now.
    // The guard below snapshots the image to compare payloads across the pass,
    // so it is only offered where a snapshot fits; a volume past the cap takes
    // the streaming path.
    if (archive.CanSeek && archive.Length <= MaxBufferedImageBytes
        && options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd
        or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      var planned = false;
      // The in-place pass is kept only if every payload still reads back: it
      // can refuse partway, and a rebuild is the honest answer when it does.
      DefragContentGuard.RunOrRebuild(archive,
        readContents: stream => ReadEntriesForGuard(stream),
        inPlace: () => { DefragmentWithPlanner(archive, options); planned = true; },
        rebuild: () => planned = false);
      if (planned) return;
      archive.Position = 0;
    }

    // An image too large to materialise goes through the streaming rebuilder;
    // buildImage returns a byte[] of the whole image, which Build refuses to
    // produce once it passes the array limit.
    // Every mode streams above the cap: end-pack and carve-hole order their
    // entries from scratch inside the rebuilder, so none of them falls back
    // to a buffered rebuild the volume is too large for.
    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes) {
      IsoWriter? streamWriter = null;
      Stream? target = null;
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: stream => {
          var r = new IsoReader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
        },
        beginWrite: s2 => { streamWriter = new IsoWriter(); target = s2; },
        // As a stream factory, not inline: an inline payload is materialised
        // inside the image buffer, which is what a large image cannot afford.
        writeEntry: (name, data) => streamWriter!.AddStreamingFile(
          name, data.LongLength, () => new MemoryStream(data, writable: false)),
        finishWrite: () => streamWriter!.BuildToStreaming(target!));
      return;
    }

    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new IsoReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new IsoWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  /// <summary>Largest image a defrag will rebuild through a byte[].</summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  /// <summary>
  /// Removes the named entries from an existing ISO 9660 image. Uses
  /// <see cref="IsoModifier"/> for O(touched bytes) random-access I/O — the
  /// directory record is shifted out of its sector and the file's data
  /// sectors are zero-wiped. Names match case-insensitively after stripping
  /// any ';N' version suffix (ISO 9660 stores uppercase IDs).
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      IsoModifier.RemoveFile(archive, name, wipeData: true);
  }
}
