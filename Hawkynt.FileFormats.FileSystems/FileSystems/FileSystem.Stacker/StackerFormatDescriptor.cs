#pragma warning disable CS1591
using Compression.Core.FileSystems.Cvf;
using Compression.Core.Layout;
using Compression.Registry;
using FileSystem.DoubleSpace;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Stacker;

/// <summary>
/// Stacker STACVOL compressed volume (Stac Electronics, MS-DOS) — the predecessor of Microsoft's
/// DoubleSpace. A STACVOL wraps an inner FAT12 volume behind the "STACKER" banner, an obfuscated
/// superblock, an emulated boot block and an interleaved AMAP; clusters are stored or compressed per
/// cluster (SD-3, SD-4 or DS). Volumes are written only in the layout the independent dmsdos driver
/// mounts; edits rebuild the volume, keeping its label.
/// <para>
/// References:
/// <list type="bullet">
///   <item><description><c>docs/CVF-CODECS-ON-DISK.md</c> — the cluster codecs and fragmented clusters, measured against dmsdos</description></item>
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc1974</c> — Stac LZS, the token grammar of SD-3</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Stac_Electronics</c> — overview</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class StackerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // The optimization adapters are keyed on this descriptor's runtime type, so the
  // registration has to have run before any instance can be looked up. Doing it from
  // the type initializer gives exactly that guarantee without a module initializer.
  static StackerFormatDescriptor() => StackerOptimizationRegistration.Register();

  private static readonly IReadOnlyDictionary<string, CvfLzMethod> Codecs = new Dictionary<string, CvfLzMethod> {
    ["sd3"] = CvfLzMethod.Sd3,
    ["sd4"] = CvfLzMethod.Sd4,
    ["ds"] = CvfLzMethod.Ds,
    ["auto"] = CvfLzMethod.Auto,
  };

  /// <inheritdoc />
  public string Id => "Stacker";
  /// <inheritdoc />
  public string DisplayName => "Stacker CVF";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract
    | FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify;
  /// <inheritdoc />
  public string DefaultExtension => ".sta";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".sta", ".stk"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASCII "STACKER" at offset 0 — the start of the STACVOL banner sector.
    new([0x53, 0x54, 0x41, 0x43, 0x4B, 0x45, 0x52], Offset: 0, Confidence: 0.90),
  ];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods { get; } = CvfCodecChoice.Methods(
    ("sd3", "Stacker 3 SD-3 (LZS)"),
    ("sd4", "Stacker 4 SD-4 (Huffman-coded LZ77)"),
    ("ds", "DoubleSpace DS LZ77"),
    ("auto", "Smallest of SD-3, SD-4 and DS per cluster"));
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "Stacker STACVOL (Stac Electronics, MS-DOS): obfuscated superblock, interleaved AMAP, inner FAT12; "
    + "stored, SD-3, SD-4 or DS clusters (fragmented clusters read). Created volumes are mounted by the dmsdos driver.";

  /// <inheritdoc />
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new(Key: "Version", DisplayName: "Stacker version", Kind: FormatOptionKind.Enum, Default: "3",
      AllowedValues: ["3", "4"],
      Description: "Stacker format version stamped into the volume superblock: 3 = Stacker 3.x (MS-DOS 6 era), "
        + "4 = Stacker 4.x. dmsdos detects each as that version and reads both. Edits keep the version."),
    new(Key: "VolumeLabel", DisplayName: "Volume label", Kind: FormatOptionKind.String, Default: "",
      Description: "Optional 11-char inner-volume label written to the root directory."),
    new(Key: "Timestamp", DisplayName: "File timestamp", Kind: FormatOptionKind.String, Default: "",
      Description: "Optional ISO-8601 date/time (e.g. 1994-02-01) stamped on every file's FAT directory entry. "
        + "Blank leaves the date/time unset."),
    new(Key: "Method", DisplayName: "Compression", Kind: FormatOptionKind.Enum, Default: "Auto",
      AllowedValues: ["Stored", "DS", "SD3", "SD4", "Auto"],
      Description: "Per-cluster compression. Stored = none. SD3 = Stacker 3 LZS codec (no header, check byte). "
        + "SD4 = Stacker 4 Huffman codec (header 0x0081). DS = the 'DS' LZ stream. "
        + "Auto = per cluster the smallest of DS/SD3/SD4, else stored."),
    new(Key: "Level", DisplayName: "Compression level", Kind: FormatOptionKind.Integer, Default: "2",
      Description: "Codec search effort (1 = fast, up to 4; higher = better ratio, slower)."),
    new(Key: "ForceCompress", DisplayName: "Force compression", Kind: FormatOptionKind.Boolean, Default: "false",
      Description: "Keep the compressed form even when it does not shrink a cluster. A compressed run "
        + "filling a whole cluster cannot be told from a stored one on Stacker, so such a cluster is still stored."),
  ];

  private static GenuineStackerReader Open(byte[] data) => new(new MemoryStream(data));

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
    var version = options.GetOptionInt("Version", 3);
    if (version is not (3 or 4))
      throw new ArgumentException($"Stacker version {version} is not supported; choose 3 or 4.", nameof(options));
    var writer = new GenuineStackerWriter {
      Version = version,
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
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    GenuineCvfVolume.WriteBack(archive, Rebuild(GenuineCvfVolume.ReadAll(archive), inputs, null));
  }

  /// <summary>Removes files by rebuilding the volume without them.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    GenuineCvfVolume.WriteBack(archive, Rebuild(GenuineCvfVolume.ReadAll(archive), null, entryNames));
  }

  /// <summary>Defragments by rebuilding: every file's clusters end up contiguous.</summary>
  public void Defragment(Stream archive)
    => GenuineCvfVolume.WriteBack(archive, Rebuild(GenuineCvfVolume.ReadAll(archive), null, null));

  /// <summary>Defragments by rebuilding; the rebuild leaves no gaps, whatever <paramref name="options"/> asks for.</summary>
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  /// <summary>
  /// Shrinks the volume by rebuilding it (label kept, every cluster recompressed with the smallest
  /// codec). The rebuilt image is emitted only when it lists the same file set AND is smaller;
  /// otherwise the original bytes are copied through unchanged, so Shrink never corrupts or grows
  /// the source.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var data = GenuineCvfVolume.ReadAll(input);
    byte[]? rebuilt;
    try {
      rebuilt = Rebuild(data, null, null);
      var before = this.List(new MemoryStream(data), null)
        .Where(e => !e.IsDirectory).Select(e => GenuineCvfVolume.LeafLower(e.Name)).Order().ToList();
      var after = this.List(new MemoryStream(rebuilt), null)
        .Where(e => !e.IsDirectory).Select(e => GenuineCvfVolume.LeafLower(e.Name)).Order().ToList();
      if (!before.SequenceEqual(after))
        rebuilt = null;
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException) {
      rebuilt = null;
    }
    output.Position = 0;
    output.SetLength(0);
    output.Write(rebuilt is { Length: > 0 } && rebuilt.Length < data.Length ? rebuilt : data);
  }

  /// <summary>Repacks the volume's clusters contiguously and drops free sectors; returns bytes reclaimed.</summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    var data = GenuineCvfVolume.ReadAll(image);
    var rebuilt = Rebuild(data, null, null);
    GenuineCvfVolume.WriteBack(image, rebuilt);
    return Math.Max(0, data.Length - rebuilt.Length);
  }

  // Read every file and re-emit: contiguous AMAP packing (defrag), smallest-codec recompression
  // (optimize/shrink) and a fresh image (purge). The volume label is kept.
  private static byte[] Rebuild(byte[] data, IReadOnlyList<ArchiveInputInfo>? add, string[]? remove) {
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
    var writer = new GenuineStackerWriter { Version = reader.Version >= 410 ? 4 : 3, VolumeLabel = reader.VolumeLabel, CompressionMethod = CvfLzMethod.Auto, CompressionLevel = 2 };
    foreach (var (n, d) in keep) writer.AddFile(n, d);
    return writer.Build();
  }
}
