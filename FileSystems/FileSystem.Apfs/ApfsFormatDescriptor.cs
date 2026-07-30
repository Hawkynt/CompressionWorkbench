#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Apfs;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>https://developer.apple.com/support/downloads/Apple-File-System-Reference.pdf</c> — Apple File System Reference, the official on-disk format specification</description></item>
///   <item><description><c>https://github.com/libyal/libfsapfs</c> — libfsapfs, maintained open-source APFS reader with format documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Apple_File_System</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class ApfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveDefragmentable, IArchiveModifiable,
    IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The only writer-honoured knob is the volume name, written to the APSB
  /// <c>apfs_volname</c> field. The container block size is fixed at 4 KiB and
  /// is not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 255),
  ];

  public string Id => "Apfs";
  public string DisplayName => "APFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  public string DefaultExtension => ".apfs";
  public IReadOnlyList<string> Extensions => [".apfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("NXSB"u8.ToArray(), Offset: 32, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// APFS container image. The writer emits real NXSB/APSB superblocks,
  /// container/volume object maps, and a populated FS-tree B-tree with inode +
  /// drec + file_extent records under Fletcher-64 checksums. In-place mutation
  /// supports full-scope Add / Remove: multi-component nested paths, FS-tree
  /// and OMAP B-tree splits, arbitrary-depth tree height growth, and on-the-fly
  /// directory inode synthesis for missing path components. The mutation path
  /// advances the transaction id, rebuilds every touched B-tree top-down with
  /// valid Fletcher-64 on every node, tail-allocates new physical blocks for
  /// node splits and file data (mirroring the writer's spaceman-less layout),
  /// and zeroes data blocks of removed files. <see cref="ApfsStructuralValidator"/>
  /// runs a paranoid post-mutation cross-check (key ordering, checksum, xid
  /// monotonicity, DIR_REC↔INODE↔FILE_EXTENT linkage). Genuinely-out-of-scope:
  /// snapshots, encryption / FileVault, fusion / tiered storage, sparse clones.
  /// </summary>
  public string Description =>
    "Apple File System container image (full-scope in-place mutation: omap + FS-tree splits, nested paths, tree height growth; structural validator).";

  // WORM write constraints.
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => ApfsConstants.MIN_APFS_IMAGE_SIZE;
  public string AcceptedInputsDescription => "APFS volume image; any files, flat root directory.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ApfsReader(stream, leaveOpen: true);
    var entries = r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified,
      Kind: null, IsSymlink: e.IsSymlink, LinkTarget: e.LinkTarget
    )).ToList();
    return SymlinkResolver.Resolve(entries);
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ApfsReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      // Streamed, not buffered: an APFS file may exceed what a byte[] can hold.
      using var target = CreateEntryFile(outputDir, e.Name);
      r.ExtractTo(e, target);
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
    var r = new ApfsReader(archive, leaveOpen: true);
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
    var w = new ApfsWriter();
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(label)) w.SetVolumeName(label);
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Names are flattened to their leaf, as this path has always done. Only the
      // length is needed to lay the volume out, so a large input is handed over as
      // a stream factory rather than read into a byte[].
      var name = Path.GetFileName(info.ArchiveName);
      if (info.InMemoryContent is { } bytes)
        w.AddFile(name, bytes);
      else
        w.AddStreamingFile(name, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
    }
    // BuildTo keeps free space sparse and streams file data into place, so the
    // volume is not bounded by what a byte[] can hold.
    if (output.CanSeek) { w.BuildTo(output); return; }
    output.Write(w.Build());
  }

  /// <summary>
  /// Streaming creation: each input's length settles the layout, then its bytes
  /// are copied into the block it was allocated. Nothing larger than one copy
  /// buffer is resident, so an entry past what a byte[] can hold is placed like
  /// any other.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs,
                                FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    if (!output.CanSeek)
      throw new ArgumentException("APFS streaming creation requires a seekable output.", nameof(output));

    var w = new ApfsWriter();
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(label)) w.SetVolumeName(label);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(Path.GetFileName(input.Name), input.Size, input.OpenStream);
    }
    w.BuildTo(output);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware APFS defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. All four <see cref="DefragMode"/> values
  /// supported. The writer always emits a fresh contiguous-from-start image
  /// with valid Fletcher-64 checksums and a populated FS-tree B-tree.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // A volume too large to materialise goes through the streaming rebuilder;
    // BuildImage returns a byte[] of the whole image, which Build() refuses to
    // produce once the volume passes the array limit.
    if (archive.CanSeek && archive.Length > MaxBufferedImageBytes
        && options.Mode is DefragMode.ConsolidateAtStart or DefragMode.FillHolesLazy) {
      var minSize = archive.Length;
      ApfsWriter? streamWriter = null;
      Stream? target = null;
      DefragRebuilder.RebuildStreaming(archive, options,
        readEntries: stream => ReadEntries(stream).ToList(),
        beginWrite: s2 => {
          streamWriter = new ApfsWriter();
          streamWriter.SetMinImageSize(minSize);
          target = s2;
        },
        writeEntry: (name, data) => streamWriter!.AddFile(name, data),
        finishWrite: () => streamWriter!.BuildTo(target!));
      return;
    }

    DefragRebuilder.Rebuild(archive, options, ReadEntries, files => BuildImage(files, archive.Length));
  }

  /// <summary>Largest volume a defrag will rebuild through a byte[].</summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  /// <summary>
  /// Adds files to the volume in place via <see cref="ApfsModifier"/>. Supports
  /// nested paths (synthesises missing intermediate directory inodes), arbitrary
  /// FS-tree / OMAP B-tree splits with tree height growth, contiguous tail
  /// allocation for split nodes and file data, per-block Fletcher-64 recompute,
  /// and xid advance. Genuinely-out-of-scope features (snapshots, encryption,
  /// fusion, sparse clones) still throw <see cref="NotSupportedException"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      ApfsModifier.Add(archive, name, data);
  }

  /// <summary>
  /// Removes named entries from the volume in place. Records for each removed entry
  /// (DIR_REC, INODE, FILE_EXTENT) are deleted from the FS-tree, the tree is
  /// rebuilt, the file's data blocks are zeroed (no forensic recovery), per-block
  /// Fletcher-64 is recomputed, and the transaction id advanced. Same full-scope
  /// support as <see cref="Add"/>: arbitrary depth, splits, multi-component paths.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      ApfsModifier.Remove(archive, name);
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new ApfsReader(stream, leaveOpen: true);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files, long currentLength) {
    var w = new ApfsWriter();
    w.SetMinImageSize(Math.Max(currentLength, ApfsConstants.MIN_APFS_IMAGE_SIZE));
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>
  /// Each file occupies one extent starting at its first block; everything
  /// ahead of the lowest of them is container and volume structure. Blocks no
  /// live extent covers are what a removal left behind.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      var reader = new ApfsReader(image);
      var blockSize = (long)reader.BlockSize;
      var first = long.MaxValue;
      foreach (var e in reader.Entries) {
        if (e.IsDirectory || e.IsSymlink || e.Size <= 0 || e.FirstBlock == 0) continue;
        var offset = (long)e.FirstBlock * blockSize;
        if (offset < 0 || offset >= image.Length) continue;
        // An extent is whole blocks; the tail of the last one is slack the
        // wiper trims from the file's real length.
        var length = Math.Min((e.Size + blockSize - 1) / blockSize * blockSize, image.Length - offset);
        result.Add(new DefragBlockInfo(offset, length, DefragBlockKind.Used, e.Name));
        first = Math.Min(first, offset);
      }
      if (first == long.MaxValue) first = Math.Min(image.Length, 1L << 20);
      result.Add(new DefragBlockInfo(0, first, DefragBlockKind.MetadataReserved));
    } catch {
      // An image we cannot walk claims nothing; wiping it would zero live data.
      return [];
    }
    return result;
  }

  /// <inheritdoc />
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new ApfsReader(image);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var e in reader.Entries)
          if (!e.IsDirectory)
            sizes[e.Name] = e.Size;
        fileSizeLookup = n => sizes.TryGetValue(n, out var v) ? v : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length, wipeClusterTips, fileSizeLookup);
  }

}
