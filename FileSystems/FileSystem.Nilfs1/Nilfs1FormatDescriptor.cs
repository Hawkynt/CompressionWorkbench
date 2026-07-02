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
    // NILFS_SUPER_MAGIC == 0x3434, little-endian at superblock+6 (file offset 1030).
    // Same magic as NILFS2 — Nilfs1Reader gates on s_rev_level == 1 to distinguish.
    // Lowered confidence so NILFS2 wins the tie when rev>=2.
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
    var r = new Nilfs1Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
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
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    var img = w.Build(blockSize, segSize, string.IsNullOrEmpty(label) ? null : label, checksum);
    output.Write(img);
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
    => throw new NotSupportedException("Nilfs1 R/W is log-structured (append-only segments) — defragmentation would re-pack snapshots, which violates the continuous-snapshot invariant.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("Nilfs1 R/W is log-structured (append-only segments) — defragmentation would re-pack snapshots, which violates the continuous-snapshot invariant.");

  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    image.Position = 0;
    var extents = Nilfs1ExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }
}
