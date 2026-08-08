#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Os9Rbf;

/// <summary>
/// Read+write descriptor for Microware OS-9 RBF (Random-Block-File) disk
/// images. OS-9 was a multi-tasking real-time OS released in 1979 by Microware
/// Systems; it shipped on the Tandy CoCo, Sharp MZ-2500, embedded systems and
/// later as OS-9/68000 and OS-9000. The writer emits a 35-track DSDD CoCo
/// reference geometry (~315 KB); the reader parses any RBF image whose root
/// directory descriptor is reachable via the identification sector.
///
/// References:
/// <list type="bullet">
///   <item><description>Microware "OS-9 Technical Reference" (RBF chapter) — the canonical RBF on-disk description</description></item>
///   <item><description><c>https://sourceforge.net/projects/nitros9/</c> — NitrOS-9 — maintained open-source OS-9/6809 with an RBF implementation + ToolShed tooling</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/OS-9</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class Os9RbfFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for Microware OS-9 RBF creation. The writer emits the
  /// canonical 35-track DSDD CoCo reference geometry; only the volume label
  /// stored at LSN 0 (DD.NAM, 32-byte high-bit-terminated string) is
  /// per-volume tunable.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 31),
  ];

  /// <summary>
  /// Walks the OS-9 RBF root directory and yields the actual on-disk byte
  /// layout — identification sector + bitmap + per-file FD sectors as
  /// <see cref="DefragBlockKind.MetadataReserved"/>, every (start, count)
  /// segment in each file's segment list as a contiguous
  /// <see cref="DefragBlockKind.Used"/> extent, and unallocated sectors as
  /// <see cref="DefragBlockKind.Free"/>.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => Os9RbfExtentMap.Enumerate(image);

  public string Id => "Os9Rbf";
  public string DisplayName => "Microware OS-9 RBF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".os9";
  public IReadOnlyList<string> Extensions => [".os9", ".rbf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // RBF identification sectors have no fixed magic — detection is by extension
  // plus structural validation (DD.TOT, DD.DIR, DD.BIT plausibility) in the reader.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Microware OS-9 RBF disk image (35-track DSDD CoCo reference, ~315 KB, 256-byte sectors). " +
    "Files described by file-descriptor sectors with segment lists; root directory only.";

  public long? MaxTotalArchiveSize => Os9Layout.TotalBytes;
  public long? MinTotalArchiveSize => 0;
  public string AcceptedInputsDescription =>
    "ASCII filenames up to 28 characters; flat root directory; ~315 KB total payload.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = "Flat root directory only; no subdirectories."; return false; }
    var name = Path.GetFileName(input.ArchiveName);
    if (name.Length > Os9Layout.DirEntryNameMaxBytes - 1) {
      reason = $"OS-9 RBF filenames are limited to {Os9Layout.DirEntryNameMaxBytes - 1} characters.";
      return false;
    }
    foreach (var c in name) {
      if (c is < (char)0x20 or > (char)0x7E) {
        reason = "Filename contains non-printable ASCII characters.";
        return false;
      }
    }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var v = ReadVolume(stream);
    return v.Files.Select((f, i) => new ArchiveEntryInfo(
      i, f.Name, f.ByteLength, f.ByteLength, "stored",
      f.IsDirectory, false, f.Created)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var v = ReadVolume(stream);
    foreach (var f in v.Files) {
      if (f.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(f.Name, files)) continue;
      WriteFile(outputDir, f.Name, Os9RbfReader.Extract(v, f));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var files = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (Path.GetFileName(i.ArchiveName), i.ReadContent()))
      .ToList();
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(label)) label = "OS9";
    var image = Os9RbfWriter.Build(files, label);
    output.Write(image);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing OS-9 RBF image. Uses
  /// <see cref="Os9RbfModifier"/> for true O(touched bytes) random-access I/O —
  /// only the identification sector, the bitmap, the root dir's FD + extents,
  /// and the new file's FD + data sectors are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      var bare = Path.GetFileName(name);
      Os9RbfModifier.RemoveFile(archive, bare, wipeData: true);
      Os9RbfModifier.AddFile(archive, bare, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing OS-9 RBF image. Uses
  /// <see cref="Os9RbfModifier"/> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      Os9RbfModifier.RemoveFile(archive, Path.GetFileName(name), wipeData: true);
  }

  /// <summary>
  /// Zeros all unused space in the OS-9 RBF image: unallocated sectors and the
  /// sector-tip slack between a file's logical size (FD.SIZ) and the end of its
  /// last allocated 256-byte sector. Cluster-tip wiping is applied only to files
  /// whose data is a single contiguous segment; a file spread across several
  /// segments keeps its tip in its final segment only, which the per-segment
  /// extent map cannot pinpoint by total size alone, so such files are omitted
  /// from the tip pass to avoid clobbering live sectors.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    image.Position = 0;
    var extents = Os9RbfExtentMap.Enumerate(image).ToList();

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        // A file with exactly one Used extent occupies a single contiguous
        // segment, so its tip is the trailing slack of that one run.
        var usedExtentCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ex in extents)
          if (ex.Kind == DefragBlockKind.Used && ex.FileName != null
              && ex.Classification != DefragBlockClass.Directory)
            usedExtentCount[ex.FileName] = usedExtentCount.GetValueOrDefault(ex.FileName) + 1;

        image.Position = 0;
        var volume = ReadVolume(image);
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var f in volume.Files) {
          if (f.IsDirectory) continue;
          if (usedExtentCount.GetValueOrDefault(f.Name) == 1)
            sizeMap[f.Name] = f.ByteLength;
        }
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new Os9RbfBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new Os9RbfBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware OS-9 RBF defragmentor. Tries planner-driven in-place path first,
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
    var mover = new Os9RbfBlockMover();

    var extents = Os9RbfExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "scanning", Fraction: 0, CurrentReadOffset: 0, CurrentWriteOffset: -1,
      ImageSize: imageSize, BlockMap: extents, Status: "Analysing layout"));

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.DataOrigin, imageSize, mover.UnitSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);

    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        Phase: "complete", Fraction: 1, CurrentReadOffset: -1, CurrentWriteOffset: -1,
        ImageSize: imageSize, BlockMap: extents, Status: "Already defragmented"));
      return;
    }

    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);

    var postExtents = Os9RbfExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "complete", Fraction: 1, CurrentReadOffset: -1, CurrentWriteOffset: -1,
      ImageSize: imageSize, BlockMap: postExtents, Status: "Defragmentation complete"));
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var v = ReadVolume(stream);
        return v.Files.Where(f => !f.IsDirectory)
                      .Select(f => (f.Name, Os9RbfReader.Extract(v, f)));
      },
      buildImage: files => Os9RbfWriter.Build(files.ToList()));
  }

  private static Os9RbfReader.Volume ReadVolume(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Os9RbfReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
  }
}
