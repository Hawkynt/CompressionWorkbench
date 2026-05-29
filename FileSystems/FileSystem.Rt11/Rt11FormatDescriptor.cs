#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Rt11;

/// <summary>
/// Read+write descriptor for DEC RT-11 disk images. RT-11 was DEC's flagship
/// PDP-11 single-user operating system from 1973 onwards and remains the most
/// common filesystem found on PDP-11 disk image dumps. Files are 6.3 RAD-50
/// encoded names stored contiguously in 512-byte blocks; the writer emits a
/// canonical RX01 single-density 8" floppy image (~256 KB).
/// </summary>
public sealed class Rt11FormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover {

  /// <summary>
  /// Walks the RT-11 directory segment chain and yields the actual on-disk
  /// byte layout — boot/home blocks + directory segments as
  /// <see cref="DefragBlockKind.MetadataReserved"/>, every permanent file as
  /// a <see cref="DefragBlockKind.Used"/> contiguous 512-byte block run
  /// (RT-11 always stores files contiguously), and E_MPTY directory slots'
  /// ranges as <see cref="DefragBlockKind.Free"/>.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => Rt11ExtentMap.Enumerate(image);

  public string Id => "Rt11";
  public string DisplayName => "DEC RT-11 (RX01)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".rt11";
  public IReadOnlyList<string> Extensions => [".rt11", ".rx01"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Detection by the home-block "DECRT11A    " ASCII marker at file offset
  // 1*512 + 0x1F0 = 0x3F0.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("DECRT11A    "u8.ToArray(), Offset: 0x3F0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "DEC RT-11 disk image (RX01 8\" SSSD reference geometry, 256 256 bytes). " +
    "Flat directory at block 6, 6.3 RAD-50 filenames, files stored contiguously in 512-byte blocks.";

  public long? MaxTotalArchiveSize => Rt11Layout.ImageBytes;
  public long? MinTotalArchiveSize => 0;
  public string AcceptedInputsDescription =>
    $"6.3 RAD-50 filenames (A-Z, 0-9, $, .); up to {Rt11Layout.EntriesPerSegment - 1} files per directory segment; ~250 KB total payload.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = "RT-11 has a single flat directory; no subdirectories."; return false; }
    var fileName = Path.GetFileName(input.ArchiveName);
    var dot = fileName.LastIndexOf('.');
    var stem = dot < 0 ? fileName : fileName[..dot];
    var ext = dot < 0 ? "" : fileName[(dot + 1)..];
    if (stem.Length > 6) { reason = "Filename stem exceeds 6 characters."; return false; }
    if (ext.Length > 3) { reason = "Extension exceeds 3 characters."; return false; }
    if (!Rad50.IsValid(stem) || !Rad50.IsValid(ext)) {
      reason = "Filename contains characters outside RAD-50 alphabet (A-Z, 0-9, $, .).";
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
      WriteFile(outputDir, f.Name, Rt11Reader.Extract(v, f));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var files = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (Path.GetFileName(i.ArchiveName), File.ReadAllBytes(i.FullPath)))
      .ToList();
    var image = Rt11Writer.Build(files);
    output.Write(image);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing RT-11 image. Uses
  /// <see cref="Rt11Modifier"/> for true O(touched bytes) random-access I/O —
  /// only the directory segment(s) and the file's contiguous data run are
  /// read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      Rt11Modifier.RemoveFile(archive, name, wipeData: true);
      Rt11Modifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing RT-11 image. Uses
  /// <see cref="Rt11Modifier"/> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      Rt11Modifier.RemoveFile(archive, name, wipeData: true);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new Rt11BlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new Rt11BlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware RT-11 defragmentor. Tries planner-driven in-place path first,
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
    var mover = new Rt11BlockMover();

    var extents = Rt11ExtentMap.Enumerate(archive).ToList();
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

    var postExtents = Rt11ExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "complete", Fraction: 1, CurrentReadOffset: -1, CurrentWriteOffset: -1,
      ImageSize: imageSize, BlockMap: postExtents, Status: "Defragmentation complete"));
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var v = ReadVolume(stream);
        return v.Files.Select(f => (f.Name, Rt11Reader.Extract(v, f)));
      },
      buildImage: files => Rt11Writer.Build(files.ToList()));
  }

  private static Rt11Reader.Volume ReadVolume(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Rt11Reader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
  }
}
