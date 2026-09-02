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
///
/// References:
/// <list type="bullet">
///   <item><description>DEC "RT-11 Volume and File Formats Manual" (AA-PD6PA-TC) — canonical directory/volume spec (archived at Bitsavers)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/RT-11</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class Rt11FormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunable knobs for DEC RT-11 creation. The home block carries a 12-char
  /// ASCII volume identifier (offset 0x1D8) and the directory area's size is
  /// configurable from 1..31 segments — each segment holds 71 entries plus a
  /// terminator, so dirSegments controls the maximum file count.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 12),
    new FormatOptionDescriptor(
      Key: "DirectorySegments",
      DisplayName: "Directory segments",
      Kind: FormatOptionKind.Integer,
      Default: "1",
      Description: "Number of 1024-byte directory segments. Each segment holds 71 entries + " +
        "terminator. Range 1..31; raise to fit more files."),
  ];

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

  /// <summary>
  /// Zeros all unused space in an RT-11 image: the blocks behind E_MPTY
  /// directory slots and any trailing region not claimed by a permanent file
  /// or the boot/home/directory metadata. Driven by the generic
  /// <see cref="UnusedSpaceWiper"/> over the RT-11 extent map.
  ///
  /// <para>Cluster tips are not applicable: RT-11 stores files contiguously and
  /// records only a 512-byte block count — there is no sub-block logical length,
  /// so a file occupies exactly its allocated block run with no slack tail.
  /// <paramref name="wipeClusterTips"/> is therefore forced off.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = Rt11ExtentMap.Enumerate(image);
    // No logical-vs-physical size distinction in RT-11 — tips never apply.
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Rt11";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "DEC RT-11 (RX01)";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".rt11";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".rt11", ".rx01"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // Detection by the home-block "DECRT11A    " ASCII marker at file offset
  // 1*512 + 0x1F0 = 0x3F0.
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("DECRT11A    "u8.ToArray(), Offset: 0x3F0, Confidence: 0.95),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "DEC RT-11 disk image (RX01 8\" SSSD reference geometry, 256 256 bytes). " +
    "Flat directory at block 6, 6.3 RAD-50 filenames, files stored contiguously in 512-byte blocks.";

    /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => Rt11Layout.ImageBytes;
    /// <summary>
  /// Gets the min total archive size.
  /// </summary>
public long? MinTotalArchiveSize => 0;
    /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    $"6.3 RAD-50 filenames (A-Z, 0-9, $, .); up to {Rt11Layout.EntriesPerSegment - 1} files per directory segment; ~250 KB total payload.";

    /// <summary>
  /// Performs the can accept operation.
  /// </summary>
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

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var v = ReadVolume(stream);
    return v.Files.Select((f, i) => new ArchiveEntryInfo(
      i, f.Name, f.ByteLength, f.ByteLength, "stored",
      false, false, f.Created)).ToList();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var v = ReadVolume(stream);
    foreach (var f in v.Files) {
      if (files != null && files.Length > 0 && !MatchesFilter(f.Name, files)) continue;
      WriteFile(outputDir, f.Name, Rt11Reader.Extract(v, f));
    }
  }

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var files = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (Path.GetFileName(i.ArchiveName), i.ReadContent()))
      .ToList();
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(label)) label = "RT11A   ";
    var dirSegs = Math.Clamp(options?.GetOptionInt("DirectorySegments", 1) ?? 1, 1, 31);
    var image = Rt11Writer.Build(files, label, dirSegs);
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
    /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new Rt11BlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new Rt11BlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
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
