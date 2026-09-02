#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.SysV;

/// <summary>
/// R/W descriptor for AT&amp;T UNIX System V (s5fs) filesystem images.
/// Magic <c>0xFD187E20</c> at file offset 1024+504 = 0x5F8.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/torvalds/linux/tree/v6.6/fs/sysv</c> — Linux sysv driver (v6.6 LTS tree; the driver was removed from later kernels)</description></item>
///   <item><description>Maurice J. Bach, "The Design of the UNIX Operating System" (Prentice Hall, 1986) — s5fs internals</description></item>
///   <item><description>AT&amp;T "System V Interface Definition"</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Reads any s5fs image with the documented superblock layout (1024-byte
/// blocks, 64-byte inodes, 24-bit zone pointers, 16-byte directory entries).
/// Writes a fresh image targeting the same classic AT&amp;T variant only —
/// other in-the-wild SysV-family flavours (Coherent, Xenix, SCO, AFS) use
/// distinct magics and inode shapes and are out of scope for the writer.
/// </para>
/// <para>
/// Mutation surface (<see cref="IArchiveModifiable"/>): true in-place R/W
/// via <see cref="SysVInPlaceModifier"/> — every Add/Remove/Replace mutates
/// the existing image at fixed byte offsets without rebuilding, including
/// the classic V7/SYSV chained free-block group cache (refill from chain
/// when <c>s_nfree</c> drops to 1; spill to a new chain block when it
/// would exceed 50) and the in-line <c>s_inode[100]</c> cache with re-scan
/// refill. Nested-path adds/removes fall back to the rebuild-from-scratch
/// path so the in-place engine never has to re-walk the directory tree.
/// Per-file size is bounded at 10 direct zones (10 KB); indirect blocks
/// are out of scope (same as the WORM writer).
/// </para>
/// <para>
/// Acceptance gates: round-trip via our own reader (necessary), spec
/// field-offset audit against linux/fs/sysv/super.c and the AT&amp;T System V
/// Interface Definition (sufficient — the writer comments cite the exact
/// offsets), and an opt-in WSL <c>mount -t sysv -o loop,ro</c> gate that
/// skips cleanly when the kernel's sysv driver isn't loadable (the default
/// WSL2 kernel ships without it).
/// </para>
/// </remarks>
public sealed class SysVFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {
  /// <summary>
  /// s5fs geometry (1024-byte blocks, 64-byte inodes, single-group layout) is
  /// fixed at the classic AT&amp;T variant the writer emits, so the only honoured
  /// knob is the 6-byte volume name in the superblock <c>s_fname[6]</c> field.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "VolumeLabel", DisplayName: "Volume Label", Kind: FormatOptionKind.String, Default: "",
      Description: "s5fs volume name stored in s_fname (max 6 ASCII chars)."),
  ];

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "SysV";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "UNIX System V FS";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".s5";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".s5", ".sysv"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0xFD187E20 little-endian at file offset 512+504 = 1016 (0x3F8) — the
    // superblock sits at block 0 + BLOCK_SIZE/2, where the Linux sysv driver reads it.
    new([0x20, 0x7E, 0x18, 0xFD], Offset: 1016, Confidence: 0.90),
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
    "AT&T UNIX System V s5fs filesystem image — true in-place R/W " +
    "(spec-audited writer + SysVInPlaceModifier mutating inode table and " +
    "data blocks at fixed byte offsets via the chained free-block group " +
    "cache + s_inode[100] cache with re-scan refill; Linux sysv kernel " +
    "driver mountable when host ships sysv.ko).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SysVReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SysVReader(stream);
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
    var r = new SysVReader(archive);
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
  /// Emits a fresh s5fs image (1024-byte blocks, classic AT&amp;T System V
  /// variant). Subdirectories are encoded from path separators in the input
  /// entry names; per-file size cap is 10 KB (10 direct zones).
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var w = new SysVWriter(output, leaveOpen: true);
    if (options.HasOption("VolumeLabel")) w.SetVolumeLabel(options.GetOption("VolumeLabel", ""));
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  /// <summary>
  /// Adds (or replaces) files inside an existing s5fs image. Flat-root files
  /// are mutated truly in place by <see cref="SysVInPlaceModifier"/> (real
  /// free-block chain refill + inode re-scan, no rebuild); anything with a
  /// path separator or a capacity overflow falls back to
  /// <see cref="ModifyRebuilder"/> so the descriptor stays consistent for
  /// out-of-scope inputs.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    try {
      foreach (var (name, data) in FilesOnly(inputs)) {
        // Nested-path entries can't go through the in-place modifier — fall
        // through to the rebuild path for the entire input list so the
        // resulting image stays self-consistent.
        if (name.Contains('/') || name.Contains('\\'))
          throw new NotSupportedException("nested path");
        SysVInPlaceModifier.Add(archive, name, data);
      }
    } catch (NotSupportedException) {
      RebuildAdd(archive, inputs);
    } catch (InvalidOperationException) {
      RebuildAdd(archive, inputs);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing s5fs image via the in-place
  /// modifier (zeroes file data blocks before returning them to the free
  /// list, matching the <see cref="IArchiveModifiable.Remove"/> wipe
  /// contract). Falls back to the rebuild path for any nested-path entry
  /// the in-place engine won't touch.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var rebuildList = new List<string>();
    foreach (var name in entryNames) {
      if (name.Contains('/') || name.Contains('\\')) {
        rebuildList.Add(name);
        continue;
      }
      if (!SysVInPlaceModifier.Remove(archive, name))
        rebuildList.Add(name);   // not found at root — let the rebuild path filter from nested layers
    }
    if (rebuildList.Count > 0)
      RebuildRemove(archive, rebuildList.ToArray());
  }

  private void RebuildAdd(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    archive.Position = 0;
    ModifyRebuilder.Add(archive, inputs,
      readEntries: stream => {
        var r = new SysVReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: BuildImage, largeVolumeCreator: this);
  }

  private void RebuildRemove(Stream archive, string[] entryNames) {
    archive.Position = 0;
    ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => {
        var r = new SysVReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: BuildImage, largeVolumeCreator: this);
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    using var ms = new MemoryStream();
    using var w = new SysVWriter(ms, leaveOpen: true);
    foreach (var (n, d) in files) w.AddFile(n, d);
    w.Finish();
    return ms.ToArray();
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
    var mover = new SysVBlockMover();
    mover.Init(archive);

    var extents = SysVExtentMap.Enumerate(archive).ToList();
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
    var postExtents = SysVExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>Every file's name and bytes, for the rebuild and the guard.</summary>
  private static List<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var reader = new SysVReader(stream);
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
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);

    source.Position = 0;
    var files = new List<(string Name, byte[] Data)>();
    {
      var reader = new SysVReader(source);
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory) continue;
        files.Add((entry.Name, reader.Extract(entry)));
      }
    }

    using var writer = new SysVWriter(target, leaveOpen: true) {
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
    => SysVExtentMap.Enumerate(image);

  /// <summary>
  /// Zero-fills every block no inode claims — which is where a removed file's
  /// bytes stay until something else takes them.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = SysVExtentMap.Enumerate(image).ToList();
    if (extents.Count == 0) return 0;

    Func<string, long>? sizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new SysVReader(image);
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
