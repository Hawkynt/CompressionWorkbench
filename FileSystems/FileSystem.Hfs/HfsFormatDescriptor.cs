#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Hfs;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description>"Inside Macintosh: Files" (Apple Computer, 1992), chapter "Data Organization on Volumes" — the canonical HFS on-disk specification (MDB, catalog/extents B*-trees)</description></item>
///   <item><description><c>https://www.mars.org/home/rob/proj/hfs/</c> — hfsutils (Robert Leslie), the classic open-source HFS implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Hierarchical_File_System</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class HfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for Classic HFS creation. The Master Directory Block
  /// stores a Pascal-string volume name at <c>drVN</c> (offset 36, max 27
  /// bytes) — the classic Mac Finder surfaces this as the disk's name.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 27),
  ];

  /// <summary>
  /// Walks the HFS catalog B-tree leaf chain and yields the actual on-disk
  /// byte layout — boot blocks + MDB + volume bitmap + catalog file as
  /// <see cref="DefragBlockKind.MetadataReserved"/>, every file record's
  /// data-fork extent (filExtRec[0]) as <see cref="DefragBlockKind.Used"/>.
  /// Coverage matches what <see cref="HfsReader"/> can extract — first leaf
  /// chain only, single data-fork extent per file.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => HfsExtentMap.Enumerate(image);

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in the HFS image: free allocation blocks, gaps
  /// between files and the block-tip slack between a file's logical size and
  /// the end of its last allocated 512-byte block. The catalog extent map
  /// clamps each file's run to its logical byte length, so trailing slack
  /// inside the final block presents as a free gap that the generic
  /// <see cref="UnusedSpaceWiper"/> zero-fills.
  ///
  /// <para>The HFS extent map keys each <see cref="DefragBlockInfo.FileName"/>
  /// by the catalog <em>leaf</em> name, whereas <see cref="HfsReader"/> reports
  /// the full slash-separated path; the size lookup is therefore keyed by the
  /// leaf segment so the explicit cluster-tip pass matches.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new HfsReader(image);
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries)
          if (!entry.IsDirectory) {
            var leaf = LeafName(entry.Name);
            sizeMap[leaf] = entry.Size;
          }
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = HfsExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // The catalog extent map labels file extents with the leaf name only.
  private static string LeafName(string path) {
    var slash = path.LastIndexOf('/');
    return slash < 0 ? path : path[(slash + 1)..];
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new HfsBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new HfsBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public string Id => "Hfs";
  public string DisplayName => "HFS (Classic)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Hfs image via
  /// <see cref="HfsModifier.AddFile"/>. The modifier mutates the catalog leaf,
  /// volume bitmap, MDB, and alternate MDB in place; on leaf overflow it
  /// transparently falls back to a writer-driven rebuild so the call always
  /// succeeds.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FlatFiles(inputs))
      HfsModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing Hfs image via
  /// <see cref="HfsModifier.RemoveFile"/>. File data blocks are wiped and
  /// catalog records are excised from the leaf; missing names are silently
  /// ignored.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    // The in-place modifier only handles the simple single-leaf catalog shape and
    // returns false otherwise — a silent no-op would leave the "removed" file (and
    // its data) intact. Collect anything it couldn't remove and fall back to a
    // verified clean rebuild so the removal (and forensic erasure) always happens.
    var unresolved = new List<string>();
    foreach (var name in entryNames)
      if (!HfsModifier.RemoveFile(archive, name, wipeData: true))
        unresolved.Add(name);
    if (unresolved.Count == 0) return;

    var skip = new HashSet<string>(unresolved, StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        if (skip.Contains(rel) || skip.Contains(Path.GetFileName(rel)))
          File.Delete(file);
      }
    });
  }

  public string DefaultExtension => ".hfs";
  public IReadOnlyList<string> Extensions => [".hfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x42, 0x44], Offset: 1024, Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Classic Macintosh HFS filesystem image (pre-HFS+). Writer emits a
  /// spec-compliant MDB, volume bitmap, and real extents + catalog B-trees
  /// with thread records, file records, and a root-dir record — matching
  /// Inside Macintosh: Files (1992). Scope: flat root directory, ASCII
  /// filenames, ≤ ~30 files per image (single-leaf catalog).
  /// </summary>
  public string Description => "Classic Macintosh HFS filesystem image (pre-HFS+)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new HfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
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
    var r = new HfsReader(archive);
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
    var w = new HfsWriter();
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(label)) w.SetVolumeName(label);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new HfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <inheritdoc/>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Largest volume the in-place pass is offered for. Its guard holds a copy
  /// of the image to compare payloads across the pass, so a volume past this
  /// takes the streaming path instead.
  /// </summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    var reader = new HfsReader(stream);
    return reader.Entries.Where(e => !e.IsDirectory).Select(reader.Extract).ToList();
  }

  /// <summary>Plans the new layout and moves the runs into it, repointing as it goes.</summary>
  private static void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new HfsBlockMover();
    mover.Init(archive);

    archive.Position = 0;
    var extents = HfsExtentMap.Enumerate(archive).ToList();
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
    var postExtents = HfsExtentMap.Enumerate(archive).ToList();

    // Whichever runs moved, the bitmap is right only once they all have: a
    // run's old home is routinely another run's new one.
    mover.SettleAllocationBitmap(archive, postExtents
      .Where(e => e.Kind != DefragBlockKind.Free)
      .Select(e => (e.Offset, e.Length)));

    archive.Position = 0;
    postExtents = HfsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>
  /// Mode-aware HFS defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The writer always emits a contiguous,
  /// start-packed allocation block layout, so all four <see cref="DefragMode"/>
  /// values converge on a clean repack.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Moving what is out of place beats writing the volume out again: a fork is
    // three extent descriptors in its catalog record, so a move is the copy
    // plus the two bytes of the descriptor that named the run.
    //
    // The mover used to rewrite the record's first descriptor whichever run had
    // moved, and to release the old blocks as it went — the first lost a
    // fragmented file's contents, the second handed live space out twice. It
    // repoints the descriptor that moved now, and the bitmap is settled once
    // the pass is over.
    // The guard below snapshots the image to compare payloads across the pass,
    // so it is only offered where a snapshot fits; a volume past the cap takes
    // the streaming path.
    if (archive.CanSeek && archive.Length <= MaxBufferedImageBytes) {
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

    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new HfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new HfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }
}
