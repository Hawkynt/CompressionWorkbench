#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.HfsPlus;

public sealed class HfsPlusFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IFormatOptionsSchema {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// HFS+ creation knobs: volume label and the allocation block size. The block
  /// size dropdown offers Auto (slack + table-overhead minimisation) plus the
  /// power-of-two sizes 4 KB … 64 KB that the writer supports.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(),
    FilesystemSchemaPresets.ClusterSize(
      key: "BlockSize",
      displayName: "Allocation block size",
      min: 4096, max: 65536,
      description: "HFS+ allocation block size (power of two, 4 KB … 64 KB). " +
        "Auto picks the size that minimises slack + allocation-bitmap and B-tree overhead."),
  ];

  /// <summary>
  /// Walks the HFS+ catalog B-tree leaf chain and yields the actual on-disk
  /// byte layout — reserved boot region + volume header + allocation file +
  /// catalog file as <see cref="DefragBlockKind.MetadataReserved"/>, every
  /// file record's first data-fork extent
  /// (<c>HFSPlusForkData.extents[0]</c>) as
  /// <see cref="DefragBlockKind.Used"/>.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => HfsPlusExtentMap.Enumerate(image);

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new HfsPlusBlockMover();
    image.Position = 0;
    mover.Init(image); // reads only the 512-byte volume header
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new HfsPlusBlockMover();
    image.Position = 0;
    mover.Init(image); // reads only the 512-byte volume header
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  public string Id => "HfsPlus";
  public string DisplayName => "HFS+";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing HFS+ image via
  /// <see cref="HfsPlusModifier.AddFile"/>. The modifier mutates the catalog
  /// leaf, allocation bitmap, and volume header in place; on leaf overflow it
  /// transparently falls back to a writer-driven rebuild so the call always
  /// succeeds.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FlatFiles(inputs))
      HfsPlusModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing HFS+ image via
  /// <see cref="HfsPlusModifier.RemoveFile"/>. File data blocks are wiped and
  /// the catalog records are excised from the leaf node; missing names are
  /// silently ignored.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      HfsPlusModifier.RemoveFile(archive, name, wipeData: true);
  }
  public string DefaultExtension => ".dmg";
  public IReadOnlyList<string> Extensions => [".dmg", ".hfsx", ".hfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x48, 0x2B], Offset: 1024, Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("hfsplus", "HFS+")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Apple HFS+ filesystem image. Writer emits full 248-byte TN1150
  /// HFSPlusCatalogFile records with HFSPlusForkData at offsets 88/168.
  /// </summary>
  public string Description => "Apple HFS+ filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new HfsPlusReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "Stored", e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new HfsPlusWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);

    // "BlockSize" → bytes (0 = Auto). The writer's optimizer confirms or bumps.
    var blockSize = FilesystemSchemaPresets.ParseSize(
      options.FormatSpecific?.GetValueOrDefault("BlockSize"));
    output.Write(w.BuildAutoSized(blockSize));
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new HfsPlusReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  /// <inheritdoc/>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware HFS+ defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The writer always emits a contiguous,
  /// start-packed allocation block layout, so all four <see cref="DefragMode"/>
  /// values converge on a clean repack.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new HfsPlusReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FullPath, r.Extract(e)));
      },
      buildImage: files => {
        var w = new HfsPlusWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }
}
