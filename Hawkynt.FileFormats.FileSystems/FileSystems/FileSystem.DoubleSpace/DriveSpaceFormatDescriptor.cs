#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.DoubleSpace;

/// <summary>
/// Microsoft DriveSpace compressed volume file (MS-DOS 6.22): the same real <c>MSDBL6.0</c>
/// version-2 container as <see cref="DoubleSpaceFormatDescriptor"/>, created with DriveSpace's own
/// JM-0-0 codec. On disk the two cannot be told apart, so detection hands such a volume to the
/// DoubleSpace descriptor; this one exists to create (and edit) DriveSpace volumes by name.
/// <para>
/// References:
/// <list type="bullet">
///   <item><description><c>docs/CVF-CODECS-ON-DISK.md</c> — the cluster codecs, measured against dmsdos</description></item>
///   <item><description>Microsoft MS-DOS 6.22 documentation (DriveSpace chapter) — original vendor description</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/DriveSpace</c> — overview</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class DriveSpaceFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  // The optimization adapters are keyed on this descriptor's runtime type, so the
  // registration has to have run before any instance can be looked up. Doing it from
  // the type initializer gives exactly that guarantee without a module initializer.
  static DriveSpaceFormatDescriptor() => CvfOptimizationRegistration.RegisterDriveSpace();

  /// <inheritdoc />
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = GenuineCvfVolume.Options(CvfV2Product.DriveSpace);
  /// <inheritdoc />
  public string Id => "DriveSpace";
  /// <inheritdoc />
  public string DisplayName => "DriveSpace CVF";
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
  // The MSDBL6 signature belongs to DoubleSpace, which reads the very same container.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => CvfV2Product.DriveSpace.Methods;
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <inheritdoc />
  public string Description =>
    "Microsoft DriveSpace compressed volume file, MS-DOS 6.22 (MSDBL6.0 container, inner FAT12; stored or JM-0-0 clusters written, DS and JM read). "
    + "Created volumes are mounted by the dmsdos driver.";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) => GenuineCvfVolume.List(stream);

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => GenuineCvfVolume.Extract(stream, outputDir, files);

  /// <inheritdoc />
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => GenuineCvfVolume.Create(output, inputs, options, CvfV2Product.DriveSpace);

  /// <summary>Adds files by rebuilding the volume (equal names are replaced).</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) => GenuineCvfVolume.RebuildInPlace(archive, CvfV2Product.DriveSpace, add: inputs);

  /// <summary>Removes files by rebuilding the volume without them.</summary>
  public void Remove(Stream archive, string[] entryNames) => GenuineCvfVolume.RebuildInPlace(archive, CvfV2Product.DriveSpace, remove: entryNames);

  /// <summary>Repacks the volume's clusters contiguously and drops free sectors; returns bytes reclaimed.</summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true)
    => GenuineCvfVolume.RebuildInPlace(image, CvfV2Product.DriveSpace);

  /// <summary>Defragments by rebuilding: every file's clusters end up contiguous.</summary>
  public void Defragment(Stream archive) => GenuineCvfVolume.RebuildInPlace(archive, CvfV2Product.DriveSpace);

  /// <summary>Defragments by rebuilding; the rebuild leaves no gaps, whatever <paramref name="options"/> asks for.</summary>
  public void Defragment(Stream archive, DefragOptions options) => GenuineCvfVolume.RebuildInPlace(archive, CvfV2Product.DriveSpace);
}
