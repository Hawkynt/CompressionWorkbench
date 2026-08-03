#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Coherent;

/// <summary>
/// Descriptor for Mark Williams Coherent OS file system. Coherent carries no
/// numeric magic — it is recognised by the coh_super_block s_fname/s_fpack
/// volume strings ("noname"/"nopack"), which is exactly how the Linux sysv
/// driver's detect_coherent() identifies it.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/torvalds/linux/tree/v6.8/fs/sysv</c> — Linux sysv driver (incl. <c>detect_coherent()</c>); pinned at v6.8, the last release before its removal</description></item>
///   <item><description>Mark Williams Company "COHERENT" manual — original vendor documentation of the filesystem</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Coherent_(operating_system)</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class CoherentFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, IFilesystemExtentMap {

  /// <summary>
  /// Largest volume the in-place pass is offered for. Its guard holds a copy of
  /// the image to compare payloads across the pass.
  /// </summary>
  private const long MaxBufferedImageBytes = 256L * 1024 * 1024;

  // ── IFilesystemExtentMap ────────────────────────────────────────────────

  /// <summary>
  /// Where the volume keeps its bytes: the superblock and the inode table, each
  /// file's blocks under its name, and the indirect blocks that name them.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => CoherentExtentMap.Enumerate(image);

  // ── IArchiveDefragmentable ──────────────────────────────────────────────

  /// <summary>
  /// Lays the volume out again by moving what is out of place. A block is named
  /// once — by a zone slot in the inode, or by an entry in an indirect block —
  /// so a move is the copy plus three bytes.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

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

    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException(
        $"Coherent can only rebuild a volume packed from the start; got {options.Mode}.");

    RebuildVerb.RebuildInPlace(archive, this, this);
  }

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    var reader = new CoherentReader(stream);
    return reader.Entries.Where(e => !e.IsDirectory).Select(reader.Extract).ToList();
  }

  /// <summary>Plans the new layout and moves the blocks into it, repointing as it goes.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new CoherentBlockMover();
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

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }
  public string Id => "Coherent";
  public string DisplayName => "Coherent FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".coh";
  public IReadOnlyList<string> Extensions => [".coh", ".coherent"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // s_fname "noname" at coh_super_block offset 0x1E4 (file offset 484). The
    // coh_super_block has no magic number; the volume-name string is the
    // canonical recogniser (matched by the Linux sysv detect_coherent).
    new([0x6E, 0x6F, 0x6E, 0x61, 0x6D, 0x65], Offset: 484, Confidence: 0.60),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Mark Williams Coherent OS filesystem image — true in-place R/W via V7-style inode + zone mutation. Add scans the inode table for free slots and the data area for unreferenced zones (direct + single-indirect + double-indirect tiers, grows past s_fsize when exhausted). Replace rewrites payload bytes at the same on-disk block offsets when the new size fits the inode's existing zones. Remove zeroes data + indirect pointer blocks + dirent + inode slot. Subdirectory mutation deferred (root-level only).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CoherentReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CoherentReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new CoherentReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// WORM emission: builds a fresh Coherent filesystem image from the
  /// supplied inputs. Directories are flattened (Coherent dirents only
  /// support a single-component 14-byte name) and the resulting image
  /// self-round-trips via <see cref="CoherentReader"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var writer = new CoherentWriter(output, leaveOpen: true);
    foreach (var (name, data) in FlatFiles(inputs))
      writer.AddFile(name, data);
    writer.Finish();
  }

  /// <summary>
  /// Adds (or replaces by leaf name) files inside an existing Coherent image
  /// via true in-place V7-style inode + zone mutation. Routes through
  /// <see cref="CoherentInPlaceModifier"/> — no rebuild fall-back: if the
  /// inode table is exhausted (the WORM writer sizes it tight to the
  /// originally-committed files) the operation surfaces <see cref="IOException"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    CoherentInPlaceModifier.Add(archive, inputs);
  }

  /// <summary>
  /// Removes the named entries from an existing Coherent image. Wipes all
  /// data zones AND indirect pointer blocks, then clears the inode slot and
  /// the dirent — no forensic recovery of the removed content is possible.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      CoherentInPlaceModifier.Remove(archive, name);
  }
}
