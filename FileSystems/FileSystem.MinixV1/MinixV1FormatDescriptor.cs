#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.MinixV1;

/// <summary>
/// Read-only descriptor for the original Minix v1 filesystem (1987,
/// Tanenbaum). 1024-byte blocks, 16-bit zone numbers, 32-byte inodes
/// (7 direct + 1 indirect + 1 double-indirect), magic 0x137F (14-byte
/// names) or 0x138F (30-byte names — Coherent variant). Predecessor to
/// Linux's ext filesystem family.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/torvalds/linux/blob/master/include/uapi/linux/minix_fs.h</c> — canonical on-disk structures (v1 layout + 0x137F/0x138F magics)</description></item>
///   <item><description>Tanenbaum &amp; Woodhull, "Operating Systems: Design and Implementation" — the original Minix FS design</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Minix_file_system</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class MinixV1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {
  /// <summary>
  /// Minix v1 geometry (1024-byte blocks, 32-byte inodes) is fixed, but the
  /// on-disk directory-name width is a genuine format variant the writer
  /// honours: 14-byte names (magic 0x137F) or 30-byte names (magic 0x138F).
  /// Selecting "30" changes both the superblock magic and every directory
  /// entry's size.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "NameLength", DisplayName: "Directory Name Length", Kind: FormatOptionKind.Enum, Default: "14",
      AllowedValues: ["14", "30"],
      Description: "Directory-entry name width: 14 bytes (magic 0x137F) or 30 bytes (magic 0x138F)."),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "MinixV1";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Minix V1 FS";
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
public string DefaultExtension => ".minix1";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".minix1"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // V1 magic at superblock+16 == file offset 1040
    new([0x7F, 0x13], Offset: 1040, Confidence: 0.85),  // 0x137F: 14-char names
    new([0x8F, 0x13], Offset: 1040, Confidence: 0.85),  // 0x138F: 30-char names
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
public string Description => "Minix v1 filesystem image (1987) — read-only.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MinixV1Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MinixV1Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single file entry as a bounded stream over the inode's reassembled
  /// data zones. Reads past the entry's logical size return 0 (EOF).
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new MinixV1Reader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
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

  /// <summary>
  /// Creates a fresh Minix v1 image holding the supplied inputs. Path
  /// separators in an input's archive name produce nested directory inodes,
  /// each with its own <c>"."</c>/<c>".."</c> entries.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var longNames = options.GetOptionInt("NameLength", 14) == 30;
    using var w = new MinixV1Writer(output, leaveOpen: true, longNames: longNames);
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Minix v1 image via
  /// <see cref="MinixV1InPlaceModifier"/> — TRUE in-place O(touched bytes) I/O
  /// (allocate inode + data zones, append zones at EOF when the image is full,
  /// write the directory entry). Falls back to a whole-image rebuild only for
  /// nested paths or payloads beyond the direct + single-indirect ceiling.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    try {
      foreach (var (name, data) in FilesOnly(inputs)) {
        MinixV1InPlaceModifier.RemoveFile(archive, name, wipeData: true);
        MinixV1InPlaceModifier.AddFile(archive, name, data);
      }
    } catch (IOException) {
      archive.Position = 0;
      ModifyRebuilder.Add(archive, inputs,
        readEntries: stream => {
          var r = new MinixV1Reader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
        },
        buildImage: BuildImage, largeVolumeCreator: this);
    }
  }

  /// <summary>Removes the named entries in-place via <see cref="MinixV1InPlaceModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    var leftover = new List<string>();
    foreach (var name in entryNames) {
      var leaf = name.Replace('\\', '/').TrimStart('/');
      if (leaf.Contains('/') || !MinixV1InPlaceModifier.RemoveFile(archive, leaf, wipeData: true))
        leftover.Add(name);
    }
    if (leftover.Count == 0) return;
    archive.Position = 0;
    ModifyRebuilder.Remove(archive, leftover.ToArray(),
      readEntries: stream => {
        var r = new MinixV1Reader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: BuildImage, largeVolumeCreator: this);
  }

  private byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    using var ms = new MemoryStream();
    using var w = new MinixV1Writer(ms, leaveOpen: true);
    foreach (var (n, d) in files) w.AddFile(n, d);
    w.Finish();
    return ms.ToArray();
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Lays the volume out again. A file's bytes are addressed one zone at a time
  /// by two-byte pointers in its inode and the indirect blocks below it, so a
  /// move is the copy, those pointers, and the bit per zone that says whether
  /// it is taken.
  /// </summary>
  /// <remarks>
  /// This used to refuse outright, on the grounds that the volume was read-only
  /// and had no writer. It has had both a writer and an in-place modifier for
  /// some time; what it did not have was a way to say where anything is, which
  /// <see cref="MinixV1ExtentMap" /> now does.
  /// </remarks>
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
          var built = this.BuildImage(files);
          if (built.Length >= archive.Length) return built;
          var padded = new byte[archive.Length];
          Array.Copy(built, padded, built.Length);
          return padded;
        }));
  }

  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new MinixV1BlockMover();
    mover.Init(archive);

    var extents = MinixV1ExtentMap.Enumerate(archive).ToList();
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
    var postExtents = MinixV1ExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>Every file's name and bytes, for the rebuild and the guard.</summary>
  private static List<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var reader = new MinixV1Reader(stream);
    return reader.Entries.Where(e => !e.IsDirectory)
                         .Select(e => (e.Name, reader.Extract(e))).ToList();
  }

  /// <summary>
  /// A zone pointer of zero names no zone, so a run of zeros need not be
  /// allocated; and the inode counts the names pointing at it, so identical
  /// files can share one copy under several of them.
  /// </summary>
  public LayoutReclaim ReclaimSupport => LayoutReclaim.Sparse | LayoutReclaim.HardLinks;

  /// <inheritdoc />
  /// <summary>
  /// Performs the rebuild streaming operation.
  /// </summary>
public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);

    source.Position = 0;
    var files = new List<(string Name, byte[] Data)>();
    using (var reader = new MinixV1Reader(source)) {
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory) continue;
        files.Add((entry.Name, reader.Extract(entry)));
      }
    }

    using var writer = new MinixV1Writer(target, leaveOpen: true) {
      MakeSparse = options.MakeSparse,
      DeduplicateWithLinks = options.DeduplicateWithLinks,
    };
    foreach (var (name, data) in files) writer.AddFile(name, data);
    writer.Finish();
    options.OnProgress?.Invoke(target.Length, target.Length);
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the extents.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => MinixV1ExtentMap.Enumerate(image);

  /// <summary>
  /// Zero-fills every zone the bitmap leaves clear — which is where a removed
  /// file's bytes stay until something else claims them.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = MinixV1ExtentMap.Enumerate(image).ToList();
    if (extents.Count == 0) return 0;

    Func<string, long>? sizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new MinixV1Reader(image);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries)
          if (!entry.IsDirectory) sizes[entry.Name] = entry.Size;
        sizeLookup = name => sizes.TryGetValue(name, out var size) ? size : -1;
      } catch {
        sizeLookup = null;
      }
    }

    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length, wipeClusterTips, sizeLookup);
  }
}
