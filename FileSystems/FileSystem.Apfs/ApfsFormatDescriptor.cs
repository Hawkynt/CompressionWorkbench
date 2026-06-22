#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Apfs;

public sealed class ApfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveDefragmentable, IArchiveModifiable,
    IFormatOptionsSchema, ILayoutOptimizable {

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
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ApfsReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
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
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware APFS defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. All four <see cref="DefragMode"/> values
  /// supported. The writer always emits a fresh contiguous-from-start image
  /// with valid Fletcher-64 checksums and a populated FS-tree B-tree.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options, ReadEntries, files => BuildImage(files, archive.Length));

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
}
