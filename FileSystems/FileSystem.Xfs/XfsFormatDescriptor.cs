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
public sealed class XfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

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
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new XfsBlockMover();
    image.Position = 0;
    mover.Init(image); // reads only the 512-byte superblock
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
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
  /// Mode-aware XFS defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. All four <see cref="DefragMode"/> values supported.
  /// </summary>
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
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) {
    // Moving what is out of place beats writing the volume out again. A file's
    // extent record names the block it starts at, and the free space each
    // allocation group records — twice, once by position and once by length,
    // with the totals in its header — is written again from the layout the
    // pass finished with.
    //
    // A file in more than one piece, or one whose extents have grown into a
    // b-tree, is more than the mover takes; the guard falls back to the
    // rebuild for those rather than leaving a volume half-repointed.
    if (archive.CanSeek && archive.Length <= PlannerImageCap) {
      var planned = false;
      // The in-place pass is kept only if every payload still reads back: it
      // can refuse partway, and a rebuild is the honest answer when it does.
      DefragContentGuard.RunOrRebuild(archive,
        readContents: ReadPayloadsForGuard,
        inPlace: () => { DefragmentWithPlanner(archive, options); planned = true; },
        rebuild: () => planned = false);
      if (planned) return;
      archive.Position = 0;
    }
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Buffering the rebuilt image would cap the volume at what a byte[] can
    // hold, so the packing modes stream: each entry is spilled to scratch and
    // the writer pulls it back while laying out the extents.
    // Every mode streams: end-pack and carve-hole order their entries from
    // scratch inside the rebuilder, so none of them has to fall back to the
    // buffered path that a volume past two gigabytes cannot use.
    {
      XfsWriter? writer = null;
      Stream? target = null;
      var spill = new List<string>();
      try {
        DefragRebuilder.RebuildStreaming(archive, options,
          readEntries: ReadEntries,
          beginWrite: s => { writer = new XfsWriter(); target = s; },
          writeEntry: (name, data) => {
            var path = Path.GetTempFileName();
            spill.Add(path);
            File.WriteAllBytes(path, data);
            writer!.AddStreamingFile(name, data.LongLength, () => File.OpenRead(path));
          },
          finishWrite: () => writer!.WriteTo(target!));
      } finally {
        foreach (var path in spill)
          try { File.Delete(path); } catch { /* scratch file already gone */ }
      }
    }
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
  // R/W: a mutable filesystem. Add/Remove produce a valid modified image; the
  // implementation re-packs the volume, so existing data may move — acceptable for
  // a conceptually read-write container. See FormatCapabilities.cs (WORM vs R/W).
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
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

  /// <summary>
  /// Adds (or replaces by name) files into an existing XFS image via a genuine
  /// in-place edit (<see cref="XfsInPlaceAdder"/>): a free inode slot is claimed
  /// from the inobt — growing a fresh 64-inode chunk when every chunk is full —
  /// a data extent is carved from the AGF bnobt/cntbt free space (best-fit across
  /// a multi-record, fragmented free map), the file bytes and inode core + BMBT
  /// extent are written, the directory entry is inserted (short-form, or after
  /// promoting the directory to single-block / leaf form, or into an existing
  /// block/leaf directory), the free counters are decremented and CRC-32C is
  /// recomputed on every touched v5 metadata block. Nested sub-directory targets
  /// are resolved (intermediate directories are created in place when absent) and
  /// replace-by-name frees the prior inode + extent first. Existing files, their
  /// inodes and data blocks stay byte-identical at their original offsets (no
  /// re-pack). The few cases the in-place path still cannot satisfy — directories
  /// large enough to need node-form (da-btree) indexing or a larger directory
  /// block size, a multi-level free-space/inode btree, or content that no longer
  /// fits AG 0 — fall back to the verified <see cref="XfsModifier"/> rebuild.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier walks the volume in memory, which a volume past two
    // gigabytes does not fit in. Above that the edit unpacks and relays it out.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this);
      return;
    }

    var toAdd = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (Name: i.ArchiveName, Data: i.ReadContent()))
      .ToList();

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var original = ms.ToArray();

    // Genuine in-place on a working copy; commit only if every input succeeds so
    // a structural limit leaves the source untouched for the rebuild fallback.
    var work = (byte[])original.Clone();
    var inPlace = true;
    try {
      foreach (var (name, data) in toAdd)
        XfsInPlaceAdder.AddFile(work, name, data);
    } catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidDataException) {
      inPlace = false;
    }
    if (inPlace) {
      archive.Position = 0;
      archive.Write(work, 0, work.Length);
      archive.SetLength(work.Length);
      return;
    }

    // Fallback: verified rebuild over the old bytes.
    archive.Position = 0;
    XfsModifier.AddOrReplace(archive, toAdd);
  }

  /// <summary>
  /// Rebuild-style remove (see <see cref="XfsModifier"/>). The removed file's
  /// data does not survive into the rebuilt image because the new writer emits
  /// a fresh superblock, AGF/AGI, and inode table.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this);
      return;
    }

    XfsModifier.Remove(archive, entryNames);
  }
}
