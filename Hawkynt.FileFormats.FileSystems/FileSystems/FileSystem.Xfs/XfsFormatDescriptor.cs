#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Xfs;

/// <summary>
/// R/W descriptor for SGI XFS filesystem images ("XFSB" superblock magic)
/// at <c>mkfs.xfs</c>-faithful defaults.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://mirrors.edge.kernel.org/pub/linux/utils/fs/xfs/docs/xfs_filesystem_structure.pdf</c> — "XFS Algorithms &amp; Data Structures" — the on-disk specification</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/xfs</c> — Linux reference implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/XFS</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class XfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  /// <inheritdoc />
  public DefragFeature SupportedDefragFeatures => DefragFeature.Packing | DefragFeature.CarveHole | DefragFeature.AscendingOrder;

  /// <summary>
  /// XFS geometry (block size, inode size, AG layout) is fixed at the
  /// <c>mkfs.xfs</c>-faithful defaults the writer emits, so the only honoured
  /// tunable is the volume label stored in the superblock <c>sb_fname[12]</c>
  /// field (ASCII, truncated to 12 bytes).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "VolumeLabel", DisplayName: "Volume Label", Kind: FormatOptionKind.String, Default: "",
      Description: "XFS volume label stored in sb_fname (max 12 ASCII chars)."),
  ];

  /// <summary>
  /// Walks the per-AG superblock + AGF/AGI/AGFL + bnobt/cntbt/inobt headers
  /// (yielded as MetadataReserved tiles) and the root inode's directory
  /// listing, then yields each child file's BMBT_REC packed-128-bit extents
  /// as Used runs (with adjacent runs coalesced). Inline (<c>local</c>
  /// fork-format) inodes surface as MetadataReserved — the file content
  /// lives inside the inode itself.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => XfsExtentMap.Enumerate(image);

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new XfsBlockMover();
    image.Position = 0;
    mover.Init(image); // reads only the 512-byte superblock
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new XfsBlockMover();
    image.Position = 0;
    mover.Init(image); // reads only the 512-byte superblock
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Largest volume the in-place pass is offered for. Its guard holds a copy of
  /// the image to compare payloads across the pass.
  /// </summary>
  private const long PlannerImageCap = 256L * 1024 * 1024;

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    var reader = new XfsReader(stream);
    return reader.Entries.Where(e => !e.IsDirectory).Select(reader.Extract).ToList();
  }

  /// <summary>Plans the new layout, moves the extents, and settles the free space.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new XfsBlockMover();
    mover.Init(archive);

    archive.Position = 0;
    var extents = this.EnumerateExtents(archive).ToList();
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

    // Which blocks are free is known only once every run has landed.
    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    mover.SettleFreeSpace(archive, postExtents
      .Where(e => e.Kind != DefragBlockKind.Free)
      .Select(e => (e.Offset, e.Length)));

    archive.Position = 0;
    postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>
  /// Defragments in place: the planner moves file extents and the allocation groups'
  /// free-space btrees are rewritten from the result; the pass is kept only if every
  /// file reads back unchanged. Anything else is refused and the volume left as it
  /// was — the rebuild that used to stand behind every refusal wrote a new volume
  /// with a fixed UUID, no label, zero timestamps and no owners, links or xattrs.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    DefragSupport.Require(options, this.SupportedDefragFeatures, "XFS");
    if (!archive.CanSeek || archive.Length > PlannerImageCap)
      throw new NotSupportedException(
        $"XFS: in-place defragmentation checks the result against a snapshot held in memory; volumes over {PlannerImageCap:N0} bytes are refused.");
    DefragContentGuard.RunOrRebuild(archive,
      readContents: ReadPayloadsForGuard,
      inPlace: () => DefragmentWithPlanner(archive, options),
      rebuild: () => throw new NotSupportedException("XFS: the volume cannot be laid out in place; it was left unchanged."));
  }


  // WORM write constraints — XFS has no inherent ceiling; real mkfs.xfs minimum ≈ 16 MB.
  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the min total archive size.
  /// </summary>
  public long? MinTotalArchiveSize => 16 * 1024 * 1024;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription => "XFS v5 (CRC) filesystem image; nested directory trees with short-form, single-block and leaf-form dir2 directories.";
  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in an XFS image: free blocks and the cluster-tip
  /// slack at the tail of each file's last data block.
  /// <para>The XFS extent map emits each file's data as a <c>Used</c> run
  /// clipped to the file's logical size. The remainder of the file's last
  /// block (from the logical size to the block boundary) is therefore not
  /// covered by any live extent and surfaces as a free gap, which the generic
  /// wiper scrubs — that is the cluster tip. A directory-entry size lookup
  /// (keyed by the same file name the extent map uses) is supplied so any
  /// extent reported block-aligned is still trimmed precisely.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new XfsReader(image);
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
    var extents = XfsExtentMap.Enumerate(image).ToList();

    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Xfs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "XFS";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  // Not R/W: the in-place adder assumes this package's own layout and corrupts
  // volumes made by mkfs.xfs, and removal existed only as a rebuild that drops the
  // label, UUID, owners, modes, times, links and xattrs. See FormatCapabilities.cs.
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".xfs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".xfs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("XFSB"u8.ToArray(), Offset: 0, Confidence: 0.95)];
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
  public string Description => "XFS filesystem image";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new XfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new XfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      r.ExtractTo(e, output);
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
    var r = new XfsReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      // The file may span several extents, so it is spooled to scratch rather
      // than windowed; the spill is deleted when the stream closes.
      var scratch = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
        FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
      var size = r.ExtractTo(e, scratch);
      scratch.Position = 0;
      return new Compression.Registry.Streaming.BoundedEntryStream(scratch, size, leaveOpen: false);
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

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new XfsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new XfsWriter();
    w.SetVolumeLabel(options.GetOption("VolumeLabel", ""));
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      if (i.InMemoryContent is { } bytes) {
        w.AddFile(i.ArchiveName, bytes);
        continue;
      }
      // Sized from disk and opened only while its extent is being filled, so the
      // volume is bounded by the target rather than by what a byte[] can hold.
      var path = i.FullPath;
      w.AddStreamingFile(i.ArchiveName, new FileInfo(path).Length, () => File.OpenRead(path));
    }
    w.WriteTo(output);
  }

  /// <summary>
  /// Two-pass streaming creation. Pass 1 plans the AG / inode / data-extent
  /// geometry from each input's pre-known size; pass 2 emits all metadata (with
  /// CRC-32C), then streams each file's bytes into its data extent via 64 KB
  /// chunks — file bytes never travel through a writer-held <c>byte[]</c>. XFS
  /// stores every regular file as a data extent (no inline file form) and file
  /// data carries no CRC (only metadata/dir blocks are checksummed), so the
  /// streamed output is byte-identical to <see cref="Create"/> for the same
  /// inputs. Non-seekable targets fall back to the buffering base implementation.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new XfsWriter();
    w.SetVolumeLabel(options.GetOption("VolumeLabel", ""));
    if (!output.CanSeek) {
      // Non-seekable target: cannot do the seek-back second pass, so buffer
      // each entry into the writer's byte[] path and emit in one shot.
      foreach (var input in inputs) {
        if (input.IsDirectory) continue;
        using var src = input.OpenStream();
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        w.AddFile(input.Name, ms.ToArray());
      }
      w.WriteTo(output);
      return;
    }
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    w.BuildToStreaming(output);
  }


}
