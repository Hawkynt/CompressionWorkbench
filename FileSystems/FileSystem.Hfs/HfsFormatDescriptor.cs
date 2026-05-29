#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Hfs;

public sealed class HfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover {

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
    foreach (var name in entryNames)
      HfsModifier.RemoveFile(archive, name, wipeData: true);
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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new HfsWriter();
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
  /// Mode-aware HFS defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The writer always emits a contiguous,
  /// start-packed allocation block layout, so all four <see cref="DefragMode"/>
  /// values converge on a clean repack.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
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
