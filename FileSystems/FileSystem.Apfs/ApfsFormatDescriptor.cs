#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Apfs;

public sealed class ApfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable {
  public string Id => "Apfs";
  public string DisplayName => "APFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;

  public string DefaultExtension => ".apfs";
  public IReadOnlyList<string> Extensions => [".apfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("NXSB"u8.ToArray(), Offset: 32, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// APFS container image — WORM. The writer emits real NXSB/APSB
  /// superblocks, container/volume object maps, and a populated FS-tree
  /// B-tree with inode + drec + file_extent records under Fletcher-64
  /// checksums. True in-flight Add/Remove would require B-tree split/merge,
  /// xid-keyed object map updates, checkpoint advance, spaceman bitmap
  /// allocation, and per-block Fletcher-64 recomputation — multi-week work.
  /// Per project policy, WORM = create only; no in-flight modification.
  /// </summary>
  public string Description => "Apple File System container image (WORM)";

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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new ApfsWriter();
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

  // ── IArchiveModifiable (rebuild-based add / replace / remove) ──────────
  // APFS in-place B-tree mutation needs node split/merge + omap/checkpoint/
  // spaceman advance; instead we read every file and rebuild a fresh image with
  // the (Fletcher-64-valid, B-tree-growing) writer, the same path the
  // defragmentor uses.

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs, ReadEntries, files => BuildImage(files, archive.Length));

  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames, ReadEntries, files => BuildImage(files, archive.Length));

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
