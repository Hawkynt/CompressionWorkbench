#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using Compression.Core.FileSystems.Cvf;
using FileSystem.DoubleSpace;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.DriveSpace3;

/// <summary>
/// Microsoft DriveSpace 3 compressed volume file (Windows 95 Plus! Pack / OSR2): the real
/// <c>MSDBL6.0</c> container with 32 KiB clusters, version 3 and a 5-byte MDFAT, whose clusters are
/// stored or compressed per cluster (JM-0-x <i>Normal/High</i>, SQ-0-0 <i>Ultra</i>; DS-0-x is read but
/// not written). Volumes
/// are written only in the layout the independent dmsdos driver mounts ("drivespace 3 CVF"); edits
/// rebuild the volume.
/// <para>
/// Shares the <c>.cvf</c> extension and the <c>MSDBL6.0</c> name with DoubleSpace/DriveSpace; its
/// signature additionally requires 64 sectors per cluster, which wins detection for these volumes.
/// </para>
/// References:
/// <list type="bullet">
///   <item><description><c>docs/CVF-CODECS-ON-DISK.md</c> — the cluster codecs, measured against dmsdos</description></item>
///   <item><description>Microsoft Plus! for Windows 95 documentation (DriveSpace 3) — original vendor description</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/DriveSpace</c> — overview of the CVF family</description></item>
/// </list>
/// </summary>
public sealed class DriveSpace3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // The optimization adapters are keyed on this descriptor's runtime type, so the
  // registration has to have run before any instance can be looked up. Doing it from
  // the type initializer gives exactly that guarantee without a module initializer.
  static DriveSpace3FormatDescriptor() => DriveSpace3OptimizationRegistration.Register();

  private static readonly IReadOnlyDictionary<string, CvfLzMethod> Codecs = new Dictionary<string, CvfLzMethod> {
    ["jm"] = CvfLzMethod.Jm,
    ["sq"] = CvfLzMethod.Sq,
    ["auto"] = CvfLzMethod.Auto,
  };

  /// <inheritdoc />
  public string Id => "DriveSpace3";
  /// <inheritdoc />
  public string DisplayName => "DriveSpace 3 CVF";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <inheritdoc />
  public string DefaultExtension => ".cvf";
  /// <inheritdoc />
  // Extension-shared with DoubleSpace; detection routes by the signature below.
  public IReadOnlyList<string> Extensions => [];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "MSDBL6.0", any bytes per sector, then 64 sectors per cluster.
    new([.. "MSDBL6.0"u8, 0x00, 0x00, 64], Offset: 3, Confidence: 0.95,
      Mask: [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0xFF]),
  ];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods { get; } = CvfCodecChoice.Methods(
    ("jm", "DriveSpace 3 JM-0-x LZ77 (Normal/High)"),
    ("sq", "DriveSpace 3 SQ-0-0 (Ultra; DEFLATE)"),
    ("auto", "Smaller of JM and SQ per cluster"));
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "Microsoft DriveSpace 3 compressed volume file, Windows 95 (MSDBL6.0 container, 32 KiB clusters; stored, JM or SQ clusters written, DS also read). "
    + "Created volumes are mounted by the dmsdos driver.";

  /// <inheritdoc />
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new(Key: "VolumeLabel", DisplayName: "Volume label", Kind: FormatOptionKind.String, Default: "",
      Description: "Optional 11-char inner-volume label written to the root directory."),
    new(Key: "Timestamp", DisplayName: "File timestamp", Kind: FormatOptionKind.String, Default: "",
      Description: "Optional ISO-8601 date/time (e.g. 1996-08-24) stamped on every file's FAT directory entry. "
        + "Blank leaves the date/time unset."),
    new(Key: "Method", DisplayName: "Compression", Kind: FormatOptionKind.Enum, Default: "Auto",
      AllowedValues: ["Stored", "JM", "SQ", "Auto"],
      Description: "Per-cluster compression — the codecs the real DriveSpace 3 driver writes. Stored = none. "
        + "JM = 'JM-0-0' LZ (Normal). SQ = 'SQ-0-0' (Ultra; DEFLATE). Auto = per cluster the smaller of JM "
        + "and SQ, else stored. dmsdos reads all of them."),
    new(Key: "Level", DisplayName: "Compression level", Kind: FormatOptionKind.Integer, Default: "2",
      Description: "Codec search effort (1 = fast, up to 4; higher = better ratio, slower)."),
    new(Key: "ForceCompress", DisplayName: "Force compression", Kind: FormatOptionKind.Boolean, Default: "false",
      Description: "Keep the compressed form even when it does not shrink a cluster."),
  ];

  // The DriveSpace 3 container: MSDBL6.0, 64 sectors per cluster (boot byte 13) and version 3
  // (boot byte 51) — distinct from DoubleSpace/DriveSpace version 2 (16 sectors per cluster).
  internal static bool IsDriveSpace3(byte[] data) =>
    data.Length > 0x34
    && System.Text.Encoding.ASCII.GetString(data, 3, 8) == "MSDBL6.0"
    && data[0x0D] == 64
    && data[0x33] == 3;

  private static GenuineDvr3Reader Open(byte[] data) =>
    IsDriveSpace3(data)
      ? new GenuineDvr3Reader(new MemoryStream(data))
      : throw new InvalidDataException("DriveSpace 3: not an MSDBL6.0 volume with 64-sector clusters and version 3.");

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = Open(GenuineCvfVolume.ReadAll(stream));
    return reader.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, -1, "cvf", e.IsDirectory, false, null)).ToList();
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = Open(GenuineCvfVolume.ReadAll(stream));
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      if (files is { Length: > 0 } && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, reader.Extract(e));
    }
  }

  /// <inheritdoc />
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var (method, level) = CvfCodecChoice.Resolve(options, "auto", Codecs);
    var writer = new GenuineDvr3Writer {
      VolumeLabel = options.GetOption("VolumeLabel", ""),
      Timestamp = FatDirStamp.Parse(options.GetOption("Timestamp", "")),
      CompressionMethod = method,
      CompressionLevel = level,
      ForceCompress = options.GetOptionBool("ForceCompress", false),
    };
    foreach (var (name, data) in FlatFiles(inputs))
      writer.AddFile(name, data);
    output.Write(writer.Build());
  }

  /// <summary>Adds files by rebuilding the volume (equal names are replaced).</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) => RebuildInPlace(archive, inputs, null);

  /// <summary>Removes files by rebuilding the volume without them.</summary>
  public void Remove(Stream archive, string[] entryNames) => RebuildInPlace(archive, null, entryNames);

  /// <summary>Repacks the volume's clusters contiguously and drops free sectors; returns bytes reclaimed.</summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true)
    => RebuildInPlace(image, null, null);

  /// <summary>Defragments by rebuilding: every file's clusters end up contiguous.</summary>
  public void Defragment(Stream archive) => RebuildInPlace(archive, null, null);

  /// <summary>Defragments by rebuilding; the rebuild leaves no gaps, whatever <paramref name="options"/> asks for.</summary>
  public void Defragment(Stream archive, DefragOptions options) => RebuildInPlace(archive, null, null);

  // Reading every file and re-emitting packs clusters contiguously, recompresses every cluster
  // with the smallest codec and drops stale sectors; the inner volume label is kept.
  internal static long RebuildInPlace(Stream image, IReadOnlyList<ArchiveInputInfo>? add, string[]? remove) {
    var data = GenuineCvfVolume.ReadAll(image);
    using var reader = Open(data);
    var removeSet = remove is null ? null : new HashSet<string>(remove.Select(GenuineCvfVolume.LeafLower));
    var keep = reader.Entries
      .Where(e => !e.IsDirectory && (removeSet is null || !removeSet.Contains(GenuineCvfVolume.LeafLower(e.Name))))
      .Select(e => (e.Name, Data: reader.Extract(e)))
      .ToList();
    if (add is not null)
      foreach (var (n, d) in FlatFiles(add)) {
        keep.RemoveAll(k => GenuineCvfVolume.LeafLower(k.Name) == GenuineCvfVolume.LeafLower(n));
        keep.Add((n, d));
      }
    var writer = new GenuineDvr3Writer { VolumeLabel = reader.VolumeLabel, CompressionMethod = CvfLzMethod.Auto, CompressionLevel = 2 };
    foreach (var (n, d) in keep) writer.AddFile(n, d);
    var rebuilt = writer.Build();
    GenuineCvfVolume.WriteBack(image, rebuilt);
    return Math.Max(0, data.Length - rebuilt.Length);
  }
}
