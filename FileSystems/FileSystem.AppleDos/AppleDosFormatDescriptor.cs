#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.AppleDos;

public sealed class AppleDosFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover {

  /// <summary>
  /// Walks the VTOC + catalog (track 17) and per-file T/S list chains,
  /// yielding the actual on-disk byte layout. Track 17 becomes metadata;
  /// every file's T/S list + data sectors collapse into contiguous-run
  /// extents; un-attributed sectors are emitted as Free.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => AppleDosExtentMap.Enumerate(image);

  public long? MaxTotalArchiveSize => AppleDosReader.StandardSize;
  public string AcceptedInputsDescription =>
    "Apple DOS 3.3 disk (35 tracks x 16 sectors x 256 bytes = 143 360 bytes).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>The Apple DOS 3.3 format has exactly one canonical image size.</summary>
  public IReadOnlyList<long> CanonicalSizes => [AppleDosReader.StandardSize];

  public string Id => "AppleDos";
  public string DisplayName => "Apple DOS 3.3";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing AppleDos image.
  /// Uses <c>AppleDosModifier</c> for true O(touched bytes) random-access
  /// I/O — only the VTOC, the catalog chain, and the file's data + T/S
  /// list sectors are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      AppleDosModifier.RemoveFile(archive, name, wipeData: true);
      AppleDosModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing AppleDos image. Uses
  /// <c>AppleDosModifier</c> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      AppleDosModifier.RemoveFile(archive, name, wipeData: true);
  }


  public string DefaultExtension => ".dsk";
  public IReadOnlyList<string> Extensions => [".dsk", ".do"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // DOS 3.3 has no magic bytes — detection is extension + VTOC sanity (handled
  // by attempting a parse). We keep the magic list empty and let FormatDetector
  // fall back to extension matching.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apple II DOS 3.3 floppy disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new AppleDosReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new AppleDosReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var total = 0L;
    foreach (var i in inputs) if (!i.IsDirectory) total += new FileInfo(i.FullPath).Length;
    if (this.MaxTotalArchiveSize is long cap && total > cap)
      throw new InvalidOperationException(
        $"AppleDOS: combined input size {total} bytes exceeds disk capacity ({cap} bytes).");

    var w = new AppleDosWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new AppleDosBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new AppleDosBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware Apple DOS 3.3 defragmentor. Tries the planner-driven in-place path
  /// first, falling back to the rebuild path on error or for <see cref="DefragMode.CarveHole"/>.
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
        using var r = new AppleDosReader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e))).ToList();
      },
      buildImage: files => {
        var w = new AppleDosWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;
    using var snap = new MemoryStream();
    archive.CopyTo(snap);
    var imageData = snap.ToArray();
    var extents = AppleDosExtentMap.Enumerate(new MemoryStream(imageData)).ToList();
    var mover = new AppleDosBlockMover();
    var moves = Compression.Core.Layout.DefragPlanner.Plan(extents, 0, imageSize, 256, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) return;
    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);
  }
}
