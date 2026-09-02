#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Xenix;

/// <summary>
/// Descriptor for Microsoft/SCO Xenix System V filesystem images.
/// Carries the genuine Xenix superblock magic 0x2B5544 at s_magic (struct
/// offset 0x3F8 → file offset 2040), the value the Linux sysv driver matches.
/// Reads existing Xenix images and emits fresh WORM images via
/// <see cref="XenixWriter"/>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/torvalds/linux/tree/v6.6/fs/sysv</c> — Linux sysv driver matching the Xenix magic (v6.6 LTS tree; removed from later kernels)</description></item>
///   <item><description>SCO "XENIX System V" development and operations documentation (vendor manuals)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Xenix</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class XenixFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Xenix";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Xenix FS";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".xnx";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".xnx", ".xenix"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Genuine Xenix s_magic 0x2B5544 (LE) at file offset 2040 (block 1 + 0x3F8).
    new([0x44, 0x55, 0x2B, 0x00], Offset: 2040, Confidence: 0.70),
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
  public string Description => "Microsoft/SCO Xenix filesystem image — read + WORM emit + in-place Add/Remove via s_free/s_inode cache (Xenix V variant).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new XenixReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new XenixReader(stream);
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
    var r = new XenixReader(archive);
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

  /// <summary>
  /// WORM-emits a fresh Xenix V image to <paramref name="output"/> containing
  /// the supplied <paramref name="inputs"/>. Directory components in input
  /// archive names become real intermediate directory inodes. Names are
  /// truncated to 14 ASCII bytes per the on-disk dir-entry budget; each file is
  /// stored through the inode's 10 direct zone slots (max 10 KB with the 1 KB
  /// block size we emit). Failing those constraints throws
  /// <see cref="InvalidOperationException"/> with the offending path.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var w = new XenixWriter(output, leaveOpen: true);
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  /// <summary>
  /// Adds (or replaces by leaf name) files inside an existing Xenix V image
  /// via <see cref="XenixModifier"/> — O(touched bytes) random-access I/O using
  /// the s5fs s_free / s_inode caches (refilled from a full scan on first
  /// mutation, since the WORM writer leaves the caches zeroed). Files are
  /// flattened to their leaf names (single-level root scope); name length is
  /// truncated to 14 ASCII bytes per the on-disk dirent budget.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    try {
      foreach (var (name, data) in FilesOnly(inputs)) {
        // Idempotent replace: drop any existing copy first so the inode + zones
        // get freed back into the caches before we re-allocate them.
        XenixModifier.RemoveFile(archive, LeafName(name), wipeData: true);
        XenixModifier.AddFile(archive, name, data);
      }
    } catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException) {
      // The in-place modifier addresses direct zones only, so a file past ten
      // blocks has nowhere to go through it — while the writer builds one
      // happily. Without this the volume could hold a file it could never be
      // given, which is a difference nobody could explain to a caller.
      archive.Position = 0;
      ModifyRebuilder.Add(archive, inputs, ReadEntries, BuildImage,
        largeVolumeCreator: this);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing Xenix V image. Names are
  /// matched against their on-disk (leaf, 14-char-truncated) form so callers
  /// can pass either the leaf or the original nested path supplied to
  /// <see cref="Add"/>.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      XenixModifier.RemoveFile(archive, LeafName(name), wipeData: true);
  }

  private static string LeafName(string name) {
    var leaf = name;
    var slash = Math.Max(leaf.LastIndexOf('/'), leaf.LastIndexOf('\\'));
    if (slash >= 0) leaf = leaf[(slash + 1)..];
    return leaf;
  }
  // ── IArchiveDefragmentable ─────────────────────────────────────────────

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Lays the volume out again. A file's bytes are addressed one block at a
  /// time by pointers in its inode and the indirect blocks below it, so a move
  /// is the copy plus those pointers — cheaper than reading every file out and
  /// writing a fresh volume, which is what the inherited default did for the
  /// one mode it offered.
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
          var built = BuildImage(files);
          if (built.Length >= archive.Length) return built;
          var padded = new byte[archive.Length];
          Array.Copy(built, padded, built.Length);
          return padded;
        }));
  }

  /// <summary>Plans the moves the layout needs and commits them in place.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new XenixBlockMover();
    mover.Init(archive);

    var extents = XenixExtentMap.Enumerate(archive).ToList();
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
    var postExtents = XenixExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>Writes a fresh volume holding exactly the files given.</summary>
  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    using var ms = new MemoryStream();
    using (var writer = new XenixWriter(ms, leaveOpen: true)) {
      foreach (var (name, data) in files) writer.AddFile(name, data);
      writer.Finish();
    }
    return ms.ToArray();
  }

  /// <summary>Every file's name and bytes, for the rebuild and the guard.</summary>
  private static List<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var reader = new XenixReader(stream);
    return reader.Entries.Where(e => !e.IsDirectory)
                         .Select(e => (e.Name, reader.Extract(e))).ToList();
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// A block pointer of zero names no block, so a run of zeros need not be
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
    {
      var reader = new XenixReader(source);
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory) continue;
        files.Add((entry.Name, reader.Extract(entry)));
      }
    }

    using var writer = new XenixWriter(target, leaveOpen: true) {
      MakeSparse = options.MakeSparse,
      DeduplicateWithLinks = options.DeduplicateWithLinks,
    };
    foreach (var (name, data) in files) writer.AddFile(name, data);
    writer.Finish();
    options.OnProgress?.Invoke(target.Length, target.Length);
  }

  /// <summary>
  /// Enumerates the extents.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => XenixExtentMap.Enumerate(image);

  /// <summary>
  /// Zero-fills every block no inode claims — which is where a removed file's
  /// bytes stay until something else takes them.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = XenixExtentMap.Enumerate(image).ToList();
    if (extents.Count == 0) return 0;

    Func<string, long>? sizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new XenixReader(image);
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
