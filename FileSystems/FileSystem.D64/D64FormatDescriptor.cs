#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.D64;

public sealed class D64FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover {

  /// <summary>
  /// Walks the directory chain on track 18 and yields the actual on-disk
  /// byte layout — track 18 (BAM + directory) as <see cref="DefragBlockKind.MetadataReserved"/>,
  /// every per-file sector chain as one or more contiguous-run extents, and
  /// the un-attributed sectors as <see cref="DefragBlockKind.Free"/>. Used by
  /// the defragment window's block-map preview.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => D64ExtentMap.Enumerate(image);

  public long? MaxTotalArchiveSize => 174848;  // standard 1541 single-sided D64 image size
  public string AcceptedInputsDescription =>
    "Commodore 1541 D64 disk; any file up to 174 848 bytes total (664 data sectors × 254 bytes).";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    // C64 allows any filename internally; the PETSCII-to-ASCII mapping happens at write time.
    reason = null;
    return true;
  }

  // D64 has only one canonical size. Shrink therefore rebuilds to the fixed 174848 bytes.
  public IReadOnlyList<long> CanonicalSizes => [174848];
  public void Shrink(Stream input, Stream output) =>
    Compression.Registry.ArchiveShrinker.ShrinkViaRebuild(input, output, this, this, this.CanonicalSizes);

  public string Id => "D64";
  public string DisplayName => "D64";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing D64 image.
  /// Uses <see cref="D64Modifier"/> for true O(touched bytes) random-access
  /// I/O — only the BAM (1 sector) + directory chain (≤19 sectors) + the
  /// new file's data sectors (⌈len/254⌉ sectors) are read or written. The
  /// 174 848-byte image isn't touched outside that.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      var truncatedName = name.Length > 16 ? name[..16] : name;
      // Replacement semantics: if the file exists, remove it first.
      D64Modifier.RemoveFile(archive, truncatedName, wipeData: true);
      D64Modifier.AddFile(archive, truncatedName, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing D64 image. Uses
  /// <see cref="D64Modifier"/> for O(touched bytes) random-access I/O —
  /// walks the file chain, marks each sector free in the BAM, secure-wipes
  /// data sectors, and clears the directory entry's file-type byte.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames) {
      var truncatedName = name.Length > 16 ? name[..16] : name;
      D64Modifier.RemoveFile(archive, truncatedName, wipeData: true);
    }
  }

  public string DefaultExtension => ".d64";
  public IReadOnlyList<string> Extensions => [".d64"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Commodore 64 1541 disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new D64Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new D64Reader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new D64Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name.Length > 16 ? name[..16] : name, data);
    output.Write(w.Build());
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new D64BlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new D64BlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware D64 defragmentor. Tries the planner-driven in-place path first
  /// (using the planner + <see cref="D64BlockMover"/>), falling back to the
  /// rebuild path on error or for <see cref="DefragMode.CarveHole"/>.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch {
        archive.Position = 0;
      }
    }
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new D64Reader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new D64Writer();
        foreach (var (n, d) in files)
          w.AddFile(n.Length > 16 ? n[..16] : n, d);
        return w.Build();
      });
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;
    using var snap = new MemoryStream();
    archive.CopyTo(snap);
    var imageData = snap.ToArray();
    var extents = D64ExtentMap.Enumerate(new MemoryStream(imageData)).ToList();
    var mover = new D64BlockMover();
    var moves = Compression.Core.Layout.DefragPlanner.Plan(extents, 0, imageSize, 256, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) return;
    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);
  }
}
