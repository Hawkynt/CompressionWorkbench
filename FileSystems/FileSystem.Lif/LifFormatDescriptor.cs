#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Lif;

/// <summary>
/// Read+write descriptor for HP LIF (Logical Interchange Format) volumes — a
/// flat-directory disk format used by the HP Series 80, HP-71/75/85 personal
/// computers and compatible HP-IL/HP-IB peripherals from the early 1980s.
/// </summary>
public sealed class LifFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for HP LIF creation: 6-char volume label, directory size
  /// (one 256-byte sector holds 7 user files plus a terminator; raising
  /// this lifts the 14-file ceiling), and the default LIF file type code
  /// applied to every entry.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 6),
    new FormatOptionDescriptor(
      Key: "DirectorySectors",
      DisplayName: "Directory sectors",
      Kind: FormatOptionKind.Integer,
      Default: "1",
      Description: "Number of 256-byte sectors reserved for the directory. Each sector " +
        "holds 8 entries (one is the terminator). Default 1 → max 7 files; raise " +
        "to fit more."),
    new FormatOptionDescriptor(
      Key: "DefaultFileType",
      DisplayName: "Default file type",
      Kind: FormatOptionKind.Enum,
      Default: "BIN (0xE020)",
      AllowedValues: ["BIN (0xE020)", "BPGM (0xE204)", "DATA (0x0001)", "TEXT (0xE0F0)", "BAS (0xE0D0)"],
      Description: "HP LIF 16-bit file-type code stored at directory entry offset 10. " +
        "Determines how HP Series 80/HP-71 routines treat the file."),
  ];

  /// <summary>
  /// Walks the LIF directory and yields the actual on-disk byte layout — the
  /// volume label + directory sectors as <see cref="DefragBlockKind.MetadataReserved"/>,
  /// every per-file contiguous 256-byte sector run as a
  /// <see cref="DefragBlockKind.Used"/> extent, and unused sectors as
  /// <see cref="DefragBlockKind.Free"/>.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => LifExtentMap.Enumerate(image);

  public string Id => "Lif";
  public string DisplayName => "HP LIF (Logical Interchange Format)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".lif";
  public IReadOnlyList<string> Extensions => [".lif"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x80, 0x00], Confidence: 0.40),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "HP LIF volume — flat directory at sector 2, files stored contiguously in 256-byte sectors. " +
    "Common in HP Series 80 / HP-71 / HP-75 / HP-85 disk and tape images.";

  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "Up to 14 files at 10-character names; flat root only; contents stored verbatim in 256-byte sectors.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = "Flat root only; no subdirectories."; return false; }
    if (input.ArchiveName.Length > 10) {
      reason = "LIF filenames limited to 10 characters.";
      return false;
    }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var v = ReadVolume(stream);
    return v.Files.Select((f, i) => new ArchiveEntryInfo(
      i, f.Name, f.ByteLength, f.ByteLength, "stored",
      false, false, f.Created)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var v = ReadVolume(stream);
    foreach (var f in v.Files) {
      if (files != null && files.Length > 0 && !MatchesFilter(f.Name, files)) continue;
      WriteFile(outputDir, f.Name, LifReader.Extract(v, f));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var files = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (Path.GetFileName(i.ArchiveName), File.ReadAllBytes(i.FullPath)))
      .ToList();

    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(label)) label = "CWB";
    var dirSectors = Math.Max(1, options?.GetOptionInt("DirectorySectors", 1) ?? 1);
    var fileType = (options?.GetOption("DefaultFileType", "BIN (0xE020)") ?? "BIN (0xE020)") switch {
      "BPGM (0xE204)" => (ushort)0xE204,
      "DATA (0x0001)" => (ushort)0x0001,
      "TEXT (0xE0F0)" => (ushort)0xE0F0,
      "BAS (0xE0D0)"  => (ushort)0xE0D0,
      _               => (ushort)0xE020,
    };
    var image = LifWriter.Build(files, label, fileType, dirSectors);
    output.Write(image);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing LIF image. Uses
  /// <see cref="LifModifier"/> for true O(touched bytes) random-access I/O —
  /// only the directory sectors and the file's contiguous data run are
  /// read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      LifModifier.RemoveFile(archive, name, wipeData: true);
      LifModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing LIF image. Uses
  /// <see cref="LifModifier"/> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      LifModifier.RemoveFile(archive, name, wipeData: true);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new LifBlockMover();
    mover.Init(image);
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new LifBlockMover();
    mover.Init(image);
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware LIF defragmentor. Tries planner-driven in-place path first,
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

    var mover = new LifBlockMover();
    mover.Init(archive);

    var extents = LifExtentMap.Enumerate(archive).ToList();
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

    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize, () => mover.Init(archive));

    var postExtents = LifExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "complete", Fraction: 1, CurrentReadOffset: -1, CurrentWriteOffset: -1,
      ImageSize: imageSize, BlockMap: postExtents, Status: "Defragmentation complete"));
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var v = ReadVolume(stream);
        return v.Files.Select(f => (f.Name, LifReader.Extract(v, f)));
      },
      buildImage: files => LifWriter.Build(files.ToList()));
  }

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros the unused (free) sectors of a LIF volume. LIF stores each file as a
  /// contiguous run of 256-byte sectors, but the directory entry records the
  /// file length only in whole sectors — there is no byte-precise logical size
  /// on disk, so a file exactly fills its allocated sectors and there is no
  /// recoverable cluster tip. Cluster-tip wiping is therefore N/A: no file-size
  /// lookup is supplied and <paramref name="wipeClusterTips"/> is forced off so
  /// a sector-rounded run is never trimmed below its real on-disk extent.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = LifExtentMap.Enumerate(image);
    // No byte-precise file size on disk → no cluster tips. Zero only free sectors.
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  private static LifReader.Volume ReadVolume(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return LifReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
  }
}
