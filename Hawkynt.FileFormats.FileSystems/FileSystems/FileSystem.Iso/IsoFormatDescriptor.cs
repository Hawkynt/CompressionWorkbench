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
public sealed class IsoFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  /// <inheritdoc />
  public DefragFeature SupportedDefragFeatures => DefragFeature.Packing | DefragFeature.CarveHole;

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
    new FormatOptionDescriptor(
      Key: "RockRidge",
      DisplayName: "Rock Ridge (POSIX names, modes, times)",
      Kind: FormatOptionKind.Boolean,
      Default: "true",
      Description: "Record Rock Ridge (RRIP 1.10) in the primary tree: full POSIX names " +
        "of any length, permissions and modification times, as Linux and the BSDs read them."),
    new FormatOptionDescriptor(
      Key: "BootImage",
      DisplayName: "El Torito BIOS boot image",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "Path of a file in the image to boot from on a PC BIOS (no emulation, " +
        "4 sectors loaded, isolinux boot info table patched in), as mkisofs -b " +
        "-no-emul-boot -boot-load-size 4 -boot-info-table does."),
    new FormatOptionDescriptor(
      Key: "EfiBootImage",
      DisplayName: "El Torito UEFI boot image",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "Path of a FAT image file in the image to boot from on UEFI (platform 0xEF, " +
        "no emulation), as mkisofs -e does."),
    new FormatOptionDescriptor(
      Key: "BootCatalog",
      DisplayName: "El Torito boot catalog file",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "Path the boot catalog is visible under (as mkisofs -c); empty keeps it " +
        "in a sector of its own that no directory lists."),
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
    RefuseInterleaved(image, "wiping");
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
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new IsoBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new IsoBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  /// <inheritdoc/>
  public string Id => "Iso";
  /// <inheritdoc/>
  public string DisplayName => "ISO 9660";
  /// <inheritdoc/>
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc/>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <inheritdoc/>
  public string DefaultExtension => ".iso";
  /// <inheritdoc/>
  public IReadOnlyList<string> Extensions => [".iso"];
  /// <inheritdoc/>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc/>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("CD001"u8.ToArray(), Offset: 0x8001, Confidence: 0.95),
    new("CD001"u8.ToArray(), Offset: 0x8801, Confidence: 0.90),
    new("CD001"u8.ToArray(), Offset: 0x9001, Confidence: 0.85),
  ];
  /// <inheritdoc/>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <inheritdoc/>
  public string? TarCompressionFormatId => null;
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc/>
  public string Description => "ISO 9660 optical disc image";

  /// <inheritdoc/>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new IsoReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Zisofs is null ? e.Size : e.StoredSize, e.Zisofs is null ? "Stored" : "zisofs",
      e.IsDirectory, false, e.LastModified, IsSymlink: e.IsSymlink, LinkTarget: e.LinkTarget
    )).ToList();
  }

  /// <inheritdoc/>
  /// <remarks>
  /// Folders are kept, empty ones included, with Rock Ridge names, permissions and times
  /// (and Joliet names for Windows); El Torito boot entries come from the boot options.
  /// </remarks>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = NewWriter(options);
    foreach (var i in inputs) {
      var name = EntryPath(i.ArchiveName);
      if (name.Length == 0) continue;
      var mode = i.SourceUnixMode is { } unixMode ? (int)unixMode : (int?)null;
      if (i.IsDirectory) {
        w.AddDirectory(name, mode, i.SourceLastModifiedUtc);
        continue;
      }
      var info = i;
      // Only the length is needed to lay the image out; reading a large input
      // into a byte[] would cap the image at what an array can hold.
      if (info.InMemoryContent is { } bytes)
        w.AddFile(name, bytes);
      else
        w.AddStreamingFile(name, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
      w.SetMetadata(name, mode, i.SourceLastModifiedUtc);
    }
    if (output.CanSeek) w.BuildToStreaming(output);
    else output.Write(w.Build());
  }

  /// <summary>
  /// An ISO writer holding <paramref name="inputs"/> in memory with folders, Rock Ridge and
  /// Joliet names and the boot options, for the CD image formats that wrap an ISO 9660
  /// track (CDI, MDF, NRG, BIN).
  /// </summary>
  public static IsoWriter BufferedWriterFor(IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions? options = null) {
    ArgumentNullException.ThrowIfNull(inputs);
    var w = NewWriter(options);
    foreach (var i in inputs) {
      var name = EntryPath(i.ArchiveName);
      if (name.Length == 0) continue;
      var mode = i.SourceUnixMode is { } unixMode ? (int)unixMode : (int?)null;
      if (i.IsDirectory) {
        w.AddDirectory(name, mode, i.SourceLastModifiedUtc);
        continue;
      }
      w.AddFile(name, i.ReadContent());
      w.SetMetadata(name, mode, i.SourceLastModifiedUtc);
    }
    return w;
  }

  private static IsoWriter NewWriter(FormatCreateOptions? options) {
    var w = new IsoWriter {
      VolumeIdentifier      = options?.GetOption("VolumeLabel", "CDROM") ?? "CDROM",
      SystemIdentifier      = options?.GetOption("SystemId", "") ?? "",
      PublisherIdentifier   = options?.GetOption("Publisher", "") ?? "",
      ApplicationIdentifier = options?.GetOption("Application", "") ?? "",
      EnableJoliet          = options?.GetOptionBool("Joliet", true) ?? true,
      EnableRockRidge       = options?.GetOptionBool("RockRidge", true) ?? true,
    };
    if (EntryPath(options?.GetOption("BootImage", "") ?? "") is { Length: > 0 } bios)
      // The isolinux / GRUB convention: no emulation, load 4 virtual sectors, boot info table.
      w.BootEntries.Add(new IsoWriter.BootEntry { Platform = 0x00, SectorCount = 4, ImagePath = bios, PatchBootInfoTable = true });
    if (EntryPath(options?.GetOption("EfiBootImage", "") ?? "") is { Length: > 0 } efi)
      // SectorCount 0 lets the firmware take the whole FAT image the entry points at.
      w.BootEntries.Add(new IsoWriter.BootEntry { Platform = 0xEF, ImagePath = efi });
    if (EntryPath(options?.GetOption("BootCatalog", "") ?? "") is { Length: > 0 } catalog)
      w.BootCatalogPath = catalog;
    return w;
  }

  /// <summary>An input name as a relative path with forward slashes and no trailing slash.</summary>
  private static string EntryPath(string name) => (name ?? "").Replace('\\', '/').Trim('/');

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
    var w = NewWriter(options);
    foreach (var input in inputs) {
      var name = EntryPath(input.Name);
      if (name.Length == 0) continue;
      var mode = input.Metadata?.UnixMode is { } unixMode ? unixMode & 0xFFF : (int?)null;
      var modified = input.Metadata?.LastWriteTimeUtc?.UtcDateTime;
      if (input.IsDirectory) {
        w.AddDirectory(name, mode, modified);
        continue;
      }
      if (output.CanSeek) {
        w.AddStreamingFile(name, input.Size, input.OpenStream);
      } else {
        using var src = input.OpenStream();
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        w.AddFile(name, ms.ToArray());
      }
      w.SetMetadata(name, mode, modified);
    }
    if (output.CanSeek) w.BuildToStreaming(output);
    else output.Write(w.Build());
  }

  /// <inheritdoc/>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new IsoReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      // Folders are created too, so an empty one survives extraction.
      if (e.IsDirectory) {
        ApplyEntryTime(CreateEntryDirectory(outputDir, e.Name), e.LastModified);
        continue;
      }
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
  /// <remarks>
  /// Only the root directory is edited in place. A path into a folder is refused
  /// (<see cref="NotSupportedException"/>) rather than flattened into a mangled root
  /// name, and so is an image the in-place editor would damage — one carrying data
  /// past the volume space, or a Joliet tree with no room for the mirrored entry.
  /// </remarks>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    var items = inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.ArchiveName)).ToList();
    foreach (var item in items)
      if (item.ArchiveName.Replace('\\', '/').Trim('/').Contains('/'))
        throw new NotSupportedException(
          $"ISO 9660: '{item.ArchiveName}' is inside a folder; only the root directory is edited in place.");
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
  /// ISO 9660 defragmentor: the planner moves each file's single extent in place
  /// and repoints its directory record, and the pass is kept only if every file
  /// reads back unchanged. Anything else is refused and the image left as it was.
  /// </summary>
  /// <remarks>
  /// The rebuild that used to stand behind every refusal wrote a new image with the
  /// volume ID "CDROM", fresh dates, no Rock Ridge (modes, owners, symlinks), no El
  /// Torito boot record and no empty folders, at another size.
  /// </remarks>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    DefragSupport.Require(options, this.SupportedDefragFeatures, "ISO 9660");
    RefuseInterleaved(archive, "defragmenting");
    if (!archive.CanSeek || archive.Length > MaxBufferedImageBytes)
      throw new NotSupportedException(
        $"ISO 9660: in-place defragmentation checks the result against a snapshot held in memory; images over {MaxBufferedImageBytes:N0} bytes are refused.");

    DefragContentGuard.RunOrRebuild(archive,
      readContents: stream => ReadEntriesForGuard(stream),
      inPlace: () => DefragmentWithPlanner(archive, options),
      rebuild: () => throw new NotSupportedException(
        "ISO 9660: the image cannot be laid out in place; it was left unchanged."));
  }

  /// <summary>
  /// The in-place editors treat every file section as one contiguous run; an interleaved section
  /// would have its gaps (other files' data) moved or wiped, so such an image is left untouched.
  /// </summary>
  private static void RefuseInterleaved(Stream image, string operation) {
    var position = image.Position;
    image.Position = 0;
    bool interleaved;
    try {
      using var reader = new IsoReader(image, leaveOpen: true);
      interleaved = reader.Entries.Any(e => e.IsInterleaved);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException) {
      interleaved = false; // an image the reader cannot walk is judged by the editor itself
    }
    image.Position = position;
    if (interleaved)
      throw new NotSupportedException($"ISO 9660: {operation} an image with interleaved files is not supported; it was left unchanged.");
  }

  /// <summary>Largest image the in-place defragmentation snapshots.</summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  /// <summary>
  /// Removes the named entries from an existing ISO 9660 image. Uses
  /// <see cref="IsoModifier"/> for O(touched bytes) random-access I/O — the
  /// directory record is shifted out of its sector and the file's data
  /// sectors are zero-wiped. Names match case-insensitively after stripping
  /// any ';N' version suffix (ISO 9660 stores uppercase IDs).
  /// </summary>
  /// <summary>
  /// Removes every file in place. Files inside folders cannot be removed in place,
  /// so an image holding any is refused rather than rebuilt: the rebuild dropped the
  /// volume ID, dates, Rock Ridge attributes and the El Torito boot record.
  /// </summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;
    var files = this.List(archive, null).Where(e => !e.IsDirectory).Select(e => e.Name.Replace('\\', '/').Trim('/')).ToArray();
    if (files.Any(n => n.Contains('/')))
      throw new NotSupportedException("ISO 9660: files inside folders cannot be removed in place; the image was left unchanged.");
    this.Remove(archive, files);
  }

  /// <remarks>
  /// Only the root directory is edited in place: a path into a folder is refused and
  /// a name that is not there is reported, rather than either passing silently.
  /// </remarks>
  public void Remove(Stream archive, string[] entryNames) {
    RefuseInterleaved(archive, "removing files from");
    foreach (var raw in entryNames ?? []) {
      var name = raw.Replace('\\', '/').Trim('/');
      if (name.Contains('/'))
        throw new NotSupportedException($"ISO 9660: '{raw}' is inside a folder; only the root directory is edited in place.");
      if (!IsoModifier.RemoveFile(archive, name, wipeData: true))
        throw new FileNotFoundException($"ISO 9660: '{raw}' is not in the root directory.", raw);
    }
  }
}
