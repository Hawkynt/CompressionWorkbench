#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.CpcDsk;

public sealed class CpcDskFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for CPC DSK creation. AMSDOS has no volume label; the
  /// only per-image knobs are the physical disk geometry the FDC presents.
  /// Default Tracks=40, Sides=1 (1 × 40 × 9 × 512 = 180 KB; the canonical
  /// CPC 3" floppy size used by AMSDOS).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Tracks",
      DisplayName: "Tracks",
      Kind: FormatOptionKind.Enum,
      Default: "40",
      AllowedValues: ["40", "80"],
      Description: "Number of cylinders per side. 40 = standard CPC 3\" / PCW 720 KB " +
        "side; 80 = double-stepped 3.5\" floppy."),
    new FormatOptionDescriptor(
      Key: "Sides",
      DisplayName: "Sides",
      Kind: FormatOptionKind.Enum,
      Default: "1",
      AllowedValues: ["1", "2"],
      Description: "Number of magnetic surfaces. 1 = single-sided (CPC default); " +
        "2 = double-sided (PCW / DSDD)."),
  ];

  /// <summary>
  /// Walks a Standard or Extended CPC DSK image and yields the actual on-disk
  /// byte layout — the disk-info header + per-track Track Info Blocks +
  /// AMSDOS directory area (track 0 side 0) as
  /// <see cref="DefragBlockKind.MetadataReserved"/>, every AMSDOS file's
  /// allocated sector list (coalesced into contiguous runs by physical block
  /// number) as <see cref="DefragBlockKind.Used"/>, unallocated data sectors
  /// as <see cref="DefragBlockKind.Free"/>.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => CpcDskExtentMap.Enumerate(image);

  public string Id => "CpcDsk";
  public string DisplayName => "CPC DSK";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dsk";
  public IReadOnlyList<string> Extensions => [".dsk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MV - CPC"u8.ToArray(), Confidence: 0.95),
    new("EXTENDED"u8.ToArray(), Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("cpcdsk", "CPC DSK")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amstrad CPC disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CpcDskReader(stream);
    return r.Entries.Select((e, i) =>
      new ArchiveEntryInfo(i, e.Name, e.Size, e.Size, "Stored", false, false, null)
    ).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CpcDskReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var tracks = options?.GetOptionInt("Tracks", 40) ?? 40;
    var sides  = options?.GetOptionInt("Sides", 1) ?? 1;
    if (tracks is not (40 or 80)) tracks = 40;
    if (sides is not (1 or 2)) sides = 1;
    using var w = new CpcDskWriter(output, leaveOpen: true, tracks: tracks, sides: sides);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing CPC DSK image.
  /// Uses <see cref="CpcDskModifier"/> for true O(touched bytes) random-access
  /// I/O — only the disk header, the directory area on track 0, and the
  /// freshly allocated data sectors are read or written. The full image is
  /// not paged in.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      var entryName = Path.GetFileName(name);
      // Replacement semantics: drop any prior entry with the same name first.
      CpcDskModifier.RemoveFile(archive, entryName, wipeData: true);
      CpcDskModifier.AddFile(archive, entryName, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing CPC DSK image. Uses
  /// <see cref="CpcDskModifier"/> for O(touched bytes) random-access I/O —
  /// walks the directory on track 0, secure-wipes the file's data sectors,
  /// and marks the directory entry's user-number byte as 0xE5 (CP/M unused).
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames) {
      var entryName = Path.GetFileName(name);
      CpcDskModifier.RemoveFile(archive, entryName, wipeData: true);
    }
  }

  /// <summary>
  /// Zeros all unused space in a CPC DSK image: unallocated data sectors and
  /// the cluster-tip slack at the tail of each AMSDOS file's last sector.
  /// CP/M allocates whole sectors but tracks length only to 128-byte record
  /// granularity, so the bytes between a file's real length and its last
  /// allocated sector boundary are slack and get zero-filled when
  /// <paramref name="wipeClusterTips"/> is set. Live file data and the AMSDOS
  /// directory / Track-Info metadata are preserved.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    // Build a file-size lookup from the logical AMSDOS files. The extent map
    // names Used runs by the AMSDOS "base.ext" filename, which is exactly the
    // key EnumerateLogicalFiles returns — so cluster-tip detection lines up.
    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var sizeMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, data) in CpcDskModifier.EnumerateLogicalFiles(image))
          sizeMap[name] = data.LongLength;
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = CpcDskExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new CpcDskBlockMover();
    mover.Init(image);
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new CpcDskBlockMover();
    mover.Init(image);
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware CPC DSK defragmentor. Tries planner-driven in-place path first,
  /// falls back to rebuild path on error.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      archive.Position = 0;
      using var snapshot = new MemoryStream();
      archive.CopyTo(snapshot);
      try {
        archive.Position = 0;
        DefragmentWithPlanner(archive, options);
        return;
      } catch {
        archive.Position = 0;
        snapshot.Position = 0;
        snapshot.CopyTo(archive);
        archive.SetLength(snapshot.Length);
        archive.Position = 0;
      }
    }

    DefragmentWithRebuild(archive, options);
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;

    var mover = new CpcDskBlockMover();
    mover.Init(archive);

    var extents = CpcDskExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "scanning", Fraction: 0, CurrentReadOffset: 0, CurrentWriteOffset: -1,
      ImageSize: imageSize, BlockMap: extents, Status: "Analysing layout"));

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.BlockToOffset(mover.FirstDataBlock), imageSize, mover.SectorSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);

    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        Phase: "complete", Fraction: 1, CurrentReadOffset: -1, CurrentWriteOffset: -1,
        ImageSize: imageSize, BlockMap: extents, Status: "Already defragmented"));
      return;
    }

    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize, () => mover.Init(archive));

    var postExtents = CpcDskExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "complete", Fraction: 1, CurrentReadOffset: -1, CurrentWriteOffset: -1,
      ImageSize: imageSize, BlockMap: postExtents, Status: "Defragmentation complete"));
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => CpcDskModifier.EnumerateLogicalFiles(stream),
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new CpcDskWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files)
            w.AddFile(n, d);
          w.Finish();
        }
        return ms.ToArray();
      });
  }
}
