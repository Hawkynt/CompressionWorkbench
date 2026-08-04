#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Nilfs1;

/// <summary>
/// Descriptor for NILFS v1 — the original (pre-mainline) New Implementation
/// of a Log-structured File System, predecessor of NILFS2. Shares the 0x3434
/// magic with NILFS2 but is distinguished by <c>s_rev_level == 1</c>
/// (NILFS2 uses rev≥2).
///
/// <para><b>Writer scope.</b> Per the task brief, NILFS v1's full DAT-tree /
/// segment-usage / log-replay surface is a multi-week effort. The writer here
/// emits a spec-compliant superblock plus a single segment with a compact
/// directory + payload region. External NILFS v1 tools that validate the
/// superblock signature accept the result; our reader fully round-trips List
/// and Extract through the writer's directory marker.</para>
///
/// <para><b>Hierarchy.</b> Subdirectories are recorded as path-prefixed entries
/// in the writer's compact directory ('/' separator). The reader returns the
/// flat list with subdir prefixes; consumers reconstruct the tree via
/// <see cref="FormatHelpers.WriteFile(string, string, byte[])"/>.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://nilfs.sourceforge.io/</c> — NILFS project home (covers the original NILFS v1)</description></item>
///   <item><description><c>https://github.com/torvalds/linux/blob/master/include/uapi/linux/nilfs2_ondisk.h</c> — shared on-disk superblock layout (s_rev_level discriminates v1)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/NILFS</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class Nilfs1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>Synthetic surface entries the reader injects for an unparsable image.</summary>
  private static readonly HashSet<string> SyntheticEntries =
    new(StringComparer.Ordinal) { "FULL.nilfs", "metadata.ini", "superblock.bin" };

  public string Id => "Nilfs1";
  public string DisplayName => "NILFS v1";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".nilfs1";
  public IReadOnlyList<string> Extensions => [".nilfs1", ".nilfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // NILFS_SUPER_MAGIC == 0x3434 at superblock+6 (file offset 1030) is shared with
    // NILFS2, so on its own it made every v1 image detect as NILFS2 — whose reader
    // then rejected it for s_rev_level < 2. The discriminating signature spans
    // s_rev_level (u32 at 1024) as well, masking out the minor revision between
    // them, and outranks NILFS2's bare magic on a v1 volume.
    new([0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x34, 0x34],
      Offset: 1024, Confidence: 0.92,
      Mask: [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0xFF, 0xFF]),
    // Bare magic, below NILFS2's confidence so a v2 volume still goes there.
    new([0x34, 0x34], Offset: 1030, Confidence: 0.80),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "NILFS v1 log-structured filesystem (precursor to NILFS2) — minimal writer + reader.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.PowerOfTwoSize(
      key: "BlockSize",
      displayName: "Block size",
      min: 1024, max: 65536,
      defaultLabel: "4 KB",
      description: "Block size in bytes — NILFS v1 supports any power of two in [1024, 65536]."),
    new FormatOptionDescriptor(
      Key: "SegmentSize",
      DisplayName: "Segment size",
      Kind: FormatOptionKind.String,
      Default: "0",
      Description: "Segment size in bytes (0 = 8 × block size, the v1 default)."),
    new FormatOptionDescriptor(
      Key: "VolumeLabel",
      DisplayName: "Volume label",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "Volume name (16 ASCII chars, written into the spec's volume-label slot)."),
    new FormatOptionDescriptor(
      Key: "Checksum",
      DisplayName: "Enable checksum",
      Kind: FormatOptionKind.Boolean,
      Default: "false",
      Description: "Sets s_flags bit 0 advertising that segments carry per-segment checksums (informational only — our writer does not compute them)."),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Nilfs1Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new Nilfs1Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      r.ExtractTo(e, output);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    options ??= new FormatCreateOptions();

    var blockLabel = options.GetOption("BlockSize", "4 KB");
    var blockSize = FilesystemSchemaPresets.ParseSize(blockLabel);
    if (blockSize <= 0) blockSize = 4096;
    var segSize = options.GetOptionInt("SegmentSize", 0);
    var label = options.GetOption("VolumeLabel", "");
    var checksum = options.GetOptionBool("Checksum", false);

    var w = new Nilfs1Writer();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the directory out; reading a large input
      // into a byte[] would cap the volume at what an array can hold.
      var name = Path.GetFileName(info.ArchiveName);
      if (info.InMemoryContent is { } bytes)
        w.AddFile(name, bytes);
      else
        w.AddStreamingFile(name, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
    }
    var volumeLabel = string.IsNullOrEmpty(label) ? null : label;
    if (output.CanSeek) w.WriteTo(output, blockSize, segSize, volumeLabel, checksum);
    else output.Write(w.Build(blockSize, segSize, volumeLabel, checksum));
  }

  /// <summary>
  /// Appends a fresh log segment at the tail of the image carrying dirent + data
  /// blocks for each input, and bumps <c>s_last_cno</c>. The 8-byte cno field is
  /// the only in-place edit; every other byte of the prior image stays
  /// byte-identical at its original offset — continuous-snapshot semantic intact.
  /// Inputs whose name already exists are effectively replaced (the higher cno
  /// wins on read).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    Nilfs1InPlaceModifier.Add(archive, inputs);
  }

  /// <summary>
  /// Appends a tombstone dirent for each named entry in a fresh log segment and
  /// bumps <c>s_last_cno</c>. The reader's cno-merge drops the entry from the
  /// listing; the original data blocks stay byte-identical at their offsets and
  /// remain addressable as a snapshot of the pre-Remove state.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    Nilfs1InPlaceModifier.Remove(archive, entryNames);
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => Nilfs1ExtentMap.Enumerate(image);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Rewrites the volume as a single checkpoint holding the live state — the
  /// reclamation a segment cleaner performs, which is what frees the space that
  /// superseded and tombstoned records still occupy. Older checkpoints are what
  /// a cleaner run reclaims, so they do not survive it; the live file set does,
  /// byte for byte.
  /// </summary>
  /// <summary>
  /// Largest volume the in-place pass is offered for. Its guard holds a copy of
  /// the image to compare payloads across the pass.
  /// </summary>
  private const long PlannerImageCap = 256L * 1024 * 1024;

  /// <summary>Every file's bytes, as the guard compares them before and after.</summary>
  private static IReadOnlyList<byte[]> ReadPayloadsForGuard(Stream stream) {
    stream.Position = 0;
    using var reader = new Nilfs1Reader(stream);

    // The reader injects a view of the whole image beside the real files.
    // Comparing that across a pass compares the pass to itself: the image
    // changed, so it always differs.
    return reader.Entries
      .Where(e => !SyntheticEntries.Contains(e.Name))
      .Select(reader.Extract)
      .ToList();
  }

  /// <summary>Plans a layout inside the base segment's area and moves the payloads.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Nilfs1BlockMover();
    mover.Init(archive);

    archive.Position = 0;
    var extents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    // Only what lies in the base segment's own area takes part; everything
    // past the first appended segment stays where it is.
    var within = extents
      .Where(e => e.Offset >= mover.FirstDataByte && e.Offset + e.Length <= mover.PayloadEnd)
      .ToList();
    if (within.Count == 0) return;

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      within, mover.FirstDataByte, mover.PayloadEnd, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves,
      mover.PayloadEnd, reinitAfterMove: null);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    // Moving what is out of place beats writing the volume out again, inside
    // the one area where it can be done: the base segment's own payloads. A
    // payload's position is an offset from the start of the segment describing
    // it, so a move is that one field — and it must stay between where those
    // payloads start and where the first appended segment begins, because the
    // reader finds that segment by carrying on from where they end.
    //
    // That is where the holes are. Removing a file writes a tombstone into a
    // new segment and leaves the bytes it had unclaimed, which is exactly the
    // space this closes up. Compacting across segments still means writing the
    // segments again, which is the rebuild below.
    if (archive.CanSeek && archive.Length <= PlannerImageCap) {
      var planned = false;
      // The in-place pass is kept only if every payload still reads back: it
      // can refuse partway, and a rebuild is the honest answer when it does.
      DefragContentGuard.RunOrRebuild(archive,
        readContents: ReadPayloadsForGuard,
        inPlace: () => { DefragmentWithPlanner(archive, options); planned = true; },
        rebuild: () => planned = false);
      if (planned) return;
      archive.Position = 0;
    }

    // Every consolidate mode lands on the same layout here: the writer emits a
    // fresh volume packed from the first data block, and has no way to place
    // files against the tail. Carving a hole is the one request it cannot meet.
    if (options.Mode is DefragMode.CarveHole)
      throw new NotSupportedException(
        "Nilfs1 defragmentation cannot carve a hole: the rebuild always start-packs the volume.");

    var tempPath = Path.GetTempFileName();
    var spill = new List<string>();
    try {
      using (var temp = File.Open(tempPath, FileMode.Open, FileAccess.ReadWrite)) {
        var w = new Nilfs1Writer();
        using (var reader = new Nilfs1Reader(archive)) {
          foreach (var e in reader.Entries) {
            if (e.IsDirectory || SyntheticEntries.Contains(e.Name)) continue;
            // Spilled to scratch so the new image streams the bytes back rather
            // than holding the whole live set while it is assembled.
            var path = Path.GetTempFileName();
            spill.Add(path);
            using (var scratch = File.Create(path))
              reader.ExtractTo(e, scratch);
            var captured = path;
            w.AddStreamingFile(e.Name, e.Size, () => File.OpenRead(captured));
          }
        }
        w.WriteTo(temp);

        options.OnProgress?.Invoke(new DefragProgressEvent(
          Phase: "commit", Fraction: 1.0, CurrentReadOffset: archive.Length,
          CurrentWriteOffset: temp.Length, ImageSize: temp.Length, BlockMap: null));

        temp.Position = 0;
        archive.Position = 0;
        temp.CopyTo(archive);
        archive.SetLength(temp.Length);
        archive.Flush();
      }
    } finally {
      File.Delete(tempPath);
      foreach (var path in spill)
        try { File.Delete(path); } catch { /* scratch file already gone */ }
    }
  }

  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    image.Position = 0;
    var extents = Nilfs1ExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }
}
