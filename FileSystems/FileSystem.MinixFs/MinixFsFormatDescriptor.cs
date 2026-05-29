#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.MinixFs;

public sealed class MinixFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover {
  public string Id => "MinixFs";
  public string DisplayName => "Minix FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".minix";
  public IReadOnlyList<string> Extensions => [".minix", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x5A, 0x4D], Offset: 1048, Confidence: 0.80f),  // v3: magic 0x4D5A at sb+24
    new([0x7F, 0x13], Offset: 1040, Confidence: 0.80f),  // v1 14-char names
    new([0x8F, 0x13], Offset: 1040, Confidence: 0.80f),  // v1 30-char names
    new([0x68, 0x24], Offset: 1040, Confidence: 0.80f),  // v2 14-char names
    new([0x78, 0x24], Offset: 1040, Confidence: 0.80f),  // v2 30-char names
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("minixfs", "Minix FS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Minix file system image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MinixFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MinixFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new MinixFsWriter(output, leaveOpen: true);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing MinixFs image. Uses
  /// <see cref="MinixFsModifier"/> for true O(touched bytes) random-access I/O.
  /// Falls back to rebuild if the image has no free inodes or zones.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    try {
      foreach (var (name, data) in FilesOnly(inputs)) {
        MinixFsModifier.RemoveFile(archive, name, wipeData: true);
        MinixFsModifier.AddFile(archive, name, data);
      }
    } catch (IOException) {
      archive.Position = 0;
      ModifyRebuilder.Add(archive, inputs,
        readEntries: stream => {
          var r = new MinixFsReader(stream);
          return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
        },
        buildImage: files => {
          using var ms = new MemoryStream();
          using var w = new MinixFsWriter(ms, leaveOpen: true);
          foreach (var (n, d) in files) w.AddFile(n, d);
          w.Finish();
          return ms.ToArray();
        });
    }
  }

  /// <summary>
  /// Removes the named entries from an existing MinixFs image using
  /// <see cref="MinixFsModifier"/> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      MinixFsModifier.RemoveFile(archive, name, wipeData: true);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new MinixFsBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new MinixFsBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware MinixFs defragmentor. Tries the planner-driven in-place path
  /// first, falling back to the rebuild path on error.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    try {
      DefragmentWithPlanner(archive, options);
      return;
    } catch {
      archive.Position = 0;
    }
    DefragmentWithRebuild(archive, options);
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;
    using var snap = new MemoryStream();
    archive.CopyTo(snap);
    var imageData = snap.ToArray();
    var extents = EnumerateExtents(new MemoryStream(imageData)).ToList();
    var mover = new MinixFsBlockMover();
    var moves = Compression.Core.Layout.DefragPlanner.Plan(extents, 0, imageSize, 1024, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) return;
    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new MinixFsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using var w = new MinixFsWriter(ms, leaveOpen: true);
        foreach (var (n, d) in files) w.AddFile(n, d);
        w.Finish();
        return ms.ToArray();
      });
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    var r = new MinixFsReader(image);
    long offset = 0;
    // Metadata region: boot block + superblock + bitmaps + inode table
    // For a v3 image, the first data zone offset gives us the metadata size
    // We approximate from the image: everything before the first file data is metadata
    yield return new DefragBlockInfo(0, 2 * 1024, DefragBlockKind.MetadataReserved, "boot+superblock");
    // Emit directories alongside files (trailing "/" marks them); without this
    // dir blocks would be invisible to the planner and counted as Free space.
    foreach (var e in r.Entries) {
      if (e.Size <= 0) continue;
      var emitName = e.IsDirectory ? e.Name + "/" : e.Name;
      yield return new DefragBlockInfo(offset + 2048, e.Size, DefragBlockKind.Used, emitName);
      offset += e.Size;
    }
  }
}
