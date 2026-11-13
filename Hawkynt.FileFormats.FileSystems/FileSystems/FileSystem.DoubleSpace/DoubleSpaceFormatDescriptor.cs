#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.DoubleSpace;

/// <summary>
/// Microsoft DoubleSpace compressed volume file (MS-DOS 6.0 / 6.2): the real <c>MSDBL6.0</c>
/// container — MDBPB, MDFAT, BitFAT and an inner FAT12 volume whose clusters are stored or
/// compressed per cluster. Volumes are created with DoubleSpace's own DS-0-x clusters, in the layout the
/// independent dmsdos driver mounts ("drivespace CVF version 2"); DS and JM clusters are read (a
/// DoubleSpace volume later used by DriveSpace may hold both); edits rebuild the volume, keeping its codec.
/// <para>
/// References:
/// <list type="bullet">
///   <item><description><c>docs/CVF-CODECS-ON-DISK.md</c> — the cluster codecs, measured against dmsdos</description></item>
///   <item><description>Microsoft MS-DOS 6 documentation (DoubleSpace chapter) — original vendor description of the compressed volume file</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/DriveSpace</c> — overview of DoubleSpace and its successors</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class DoubleSpaceFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // The optimization adapters are keyed on this descriptor's runtime type, so the
  // registration has to have run before any instance can be looked up. Doing it from
  // the type initializer gives exactly that guarantee without a module initializer.
  static DoubleSpaceFormatDescriptor() => CvfOptimizationRegistration.RegisterDoubleSpace();

  /// <inheritdoc />
  public string Id => "DoubleSpace";
  /// <inheritdoc />
  public string DisplayName => "DoubleSpace CVF";
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
  public IReadOnlyList<string> Extensions => [".cvf"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // OEM name of the MDBPB; DriveSpace 3's 64-sector clusters are claimed by a longer signature.
    new("MSDBL6"u8.ToArray(), Offset: 3, Confidence: 0.85),
  ];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => CvfV2Product.DoubleSpace.Methods;
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "Microsoft DoubleSpace compressed volume file, MS-DOS 6.0/6.2 (MSDBL6.0 container, inner FAT12; stored or DS-0-x clusters written, DS and JM read). "
    + "Created volumes are mounted by the dmsdos driver.";

  /// <inheritdoc />
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = GenuineCvfVolume.Options(CvfV2Product.DoubleSpace);

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) => GenuineCvfVolume.List(stream);

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => GenuineCvfVolume.Extract(stream, outputDir, files);

  /// <inheritdoc />
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => GenuineCvfVolume.Create(output, inputs, options, CvfV2Product.DoubleSpace);

  /// <summary>Adds files by rebuilding the volume (equal names are replaced).</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) => GenuineCvfVolume.RebuildInPlace(archive, CvfV2Product.DoubleSpace, add: inputs);

  /// <summary>Removes files by rebuilding the volume without them.</summary>
  public void Remove(Stream archive, string[] entryNames) => GenuineCvfVolume.RebuildInPlace(archive, CvfV2Product.DoubleSpace, remove: entryNames);

  /// <summary>Repacks the volume's clusters contiguously and drops free sectors; returns bytes reclaimed.</summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true)
    => GenuineCvfVolume.RebuildInPlace(image, CvfV2Product.DoubleSpace);

  /// <summary>Defragments by rebuilding: every file's clusters end up contiguous.</summary>
  public void Defragment(Stream archive) => GenuineCvfVolume.RebuildInPlace(archive, CvfV2Product.DoubleSpace);

  /// <summary>Defragments by rebuilding; the rebuild leaves no gaps, whatever <paramref name="options"/> asks for.</summary>
  public void Defragment(Stream archive, DefragOptions options) => GenuineCvfVolume.RebuildInPlace(archive, CvfV2Product.DoubleSpace);
}
