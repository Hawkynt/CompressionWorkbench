#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Adf;

public sealed class AdfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty {

  /// <summary>
  /// Walks the boot blocks + root block + bitmap blocks + per-file
  /// header/extension/data block chains, yielding the actual on-disk
  /// layout. Boot/root/bitmap and directory headers become
  /// <see cref="DefragBlockKind.MetadataReserved"/>; file header
  /// + extension blocks + data blocks attribute to their owning file
  /// (coalesced into contiguous runs).
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => AdfExtentMap.Enumerate(image);

  public long? MaxTotalArchiveSize => 901120;  // standard DD (880 KB) — 11 sectors × 2 sides × 80 tracks × 512
  public string AcceptedInputsDescription =>
    "Amiga DD ADF disk; any file up to 901 120 bytes total.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  public IReadOnlyList<long> CanonicalSizes => [901120];
  public void Shrink(Stream input, Stream output) =>
    Compression.Registry.ArchiveShrinker.ShrinkViaRebuild(input, output, this, this, this.CanonicalSizes);

  public string Id => "Adf";
  public string DisplayName => "ADF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Adf image (FFS).
  /// Uses <c>AdfModifier</c> for true O(touched bytes) random-access I/O —
  /// only the root block, the bitmap, the optional hash-chain neighbour,
  /// and the new file's header + data blocks are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      AdfModifier.RemoveFile(archive, name, wipeData: true);
      AdfModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing Adf image (FFS). Uses
  /// <c>AdfModifier</c> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      AdfModifier.RemoveFile(archive, name, wipeData: true);
  }

  public string DefaultExtension => ".adf";
  public IReadOnlyList<string> Extensions => [".adf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("DOS\0"u8.ToArray(), Confidence: 0.60)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("adf", "ADF")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga Disk File";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new AdfReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new AdfReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new AdfWriter();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new AdfBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new AdfBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware ADF defragmentor. Tries the planner-driven in-place path first,
  /// falling back to the rebuild path on error or for <see cref="DefragMode.CarveHole"/>.
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
        var r = new AdfReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory)
          .Select(e => (e.FullPath, r.Extract(e)));
      },
      buildImage: files => {
        var w = new AdfWriter();
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
    var extents = AdfExtentMap.Enumerate(new MemoryStream(imageData)).ToList();
    var mover = new AdfBlockMover();
    var moves = Compression.Core.Layout.DefragPlanner.Plan(extents, 0, imageSize, 512, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) return;
    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);
  }

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in an Amiga ADF image: every 512-byte sector not
  /// claimed by a boot/root/bitmap block, a directory or file header, a file
  /// extension block, or a file data block. Driven by the generic
  /// <see cref="UnusedSpaceWiper"/> over the ADF extent map.
  ///
  /// <para>Per-file cluster-tip wiping is <em>not</em> applied: an ADF file's
  /// extent is a coalesced run that interleaves the file header block, optional
  /// extension blocks and the data blocks (and, under OFS, every data block
  /// carries a 24-byte block header), so the file's logical bytes are not laid
  /// out as a flat <c>offset..offset+size</c> region. Treating the trailing
  /// bytes of that run as slack would clobber live metadata, so tip wiping is
  /// N/A here; only genuinely free sectors are zeroed.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = AdfExtentMap.Enumerate(image);
    // Tips are N/A for ADF (header/extension/data blocks interleave within a
    // file's run); wipe free sectors only, never per-extent tails.
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }
}
