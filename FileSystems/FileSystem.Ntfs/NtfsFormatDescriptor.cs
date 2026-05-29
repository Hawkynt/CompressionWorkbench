#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ntfs;

public sealed class NtfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover {

  /// <summary>
  /// Walks the boot sector + $MFT + each MFT record's $DATA attribute and
  /// yields one extent per data run. Records 0-15 (the reserved system
  /// files: $MFT, $MFTMirr, $LogFile, $Volume, $AttrDef, root, $Bitmap,
  /// $Boot, $BadClus, $Secure, $UpCase, $Extend) surface as
  /// MetadataReserved; regular files surface as Used. Adjacent runs are
  /// coalesced.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => NtfsExtentMap.Enumerate(image);

  public string Id => "Ntfs";
  public string DisplayName => "NTFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new NtfsBlockMover();
    mover.Init(image); // reads only the boot sector + MFT record 0
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new NtfsBlockMover();
    mover.Init(image); // reads only the boot sector + MFT record 0
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware NTFS defragmentor. Supports planner-driven in-place path
  /// (using <see cref="DefragPlanner"/> + <see cref="NtfsBlockMover"/>) and the
  /// legacy rebuild path (using <see cref="DefragRebuilder"/>). Falls back to
  /// rebuild when the planner path throws (e.g. data-run re-encoding changes
  /// byte length with no slack space).
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch {
        archive.Position = 0;
      }
    }
    DefragmentWithRebuild(archive, options);
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new NtfsBlockMover();
    mover.Init(archive); // reads only the boot sector + MFT record 0

    // Stream the extent map directly off the archive — no whole-image load.
    var extents = NtfsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent("scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    // Compute data origin: the first byte past all MetadataReserved extents.
    // User data must not be placed in the boot sector, MFT, or system file regions.
    long dataOrigin = mover.FirstDataByte;
    foreach (var e in extents) {
      if (e.Kind == DefragBlockKind.MetadataReserved) {
        var end = e.Offset + e.Length;
        if (end > dataOrigin) dataOrigin = end;
      }
    }
    // Align to cluster boundary.
    var cs = mover.ClusterSize;
    dataOrigin = (dataOrigin + cs - 1) / cs * cs;

    var moves = DefragPlanner.Plan(extents, dataOrigin, archive.Length, mover.ClusterSize, options.Profile, options.Mode);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    // After each move, re-init the mover by re-reading only the boot sector +
    // record 0 from the now-mutated stream — no whole-image load.
    DefragPlannerExecutor.Execute(archive, options, mover, moves, archive.Length, () => {
      archive.Position = 0;
      mover.Init(archive);
    });

    archive.Position = 0;
    var postExtents = NtfsExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    var totalSize = (int)archive.Length;
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new NtfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new NtfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build(totalSize);
      });
  }
  public string DefaultExtension => ".ntfs";
  public IReadOnlyList<string> Extensions => [".ntfs", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'N', (byte)'T', (byte)'F', (byte)'S', (byte)' ', (byte)' ', (byte)' ', (byte)' '], Offset: 3, Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// NTFS filesystem image with LZNT1 compression support. The writer emits
  /// every reserved system MFT record (0-15) with real content: $MFT,
  /// $MFTMirr, $LogFile, $Volume (with a version-3.1 $VOLUME_INFORMATION
  /// and a $VOLUME_NAME), $AttrDef, root ., $Bitmap, $Boot, $BadClus,
  /// $Secure, $UpCase (128 KiB UTF-16 table), and $Extend. Every record
  /// carries $STANDARD_INFORMATION and $FILE_NAME, the Update Sequence
  /// Array (USA) fixup is applied at sector boundaries, and the on-disk
  /// cluster bitmap reflects actual allocations.
  /// </summary>
  public string Description => "NTFS filesystem image with LZNT1 compression and full $MFT system files";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new NtfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new NtfsWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new NtfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Add files to an existing NTFS image. Current implementation re-builds the image
  /// with all existing files + the new ones — the inherent build-from-scratch design
  /// of <see cref="NtfsWriter"/> means "add" equals "re-pack" here. Use
  /// <see cref="Remove"/> first to clean up stale entries.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    archive.Position = 0;
    var reader = new NtfsReader(archive);
    var combined = new NtfsWriter();
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
      combined.AddFile(entry.Name, reader.Extract(entry));
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      combined.AddFile(name, data);
    var totalSize = (int)archive.Length;
    var rebuilt = combined.Build(totalSize);
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  /// <summary>
  /// Removes files from an existing NTFS image with full secure wipe (cluster bytes
  /// for non-resident data, MFT record, and root-dir index entry). No forensic
  /// recovery of the removed content is possible from the resulting bytes.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    foreach (var name in entryNames)
      NtfsRemover.Remove(image, name);
    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }
}
