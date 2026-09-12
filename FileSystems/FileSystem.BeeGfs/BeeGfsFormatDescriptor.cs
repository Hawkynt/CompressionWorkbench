#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.BeeGfs;

/// <summary>
/// Describes BeeGFS without inventing a standalone byte-stream image format.
/// </summary>
/// <remarks>
/// <para>
/// BeeGFS metadata and storage targets are directories on ordinary local Linux filesystems,
/// typically ext4/XFS. A logical BeeGFS namespace can span multiple metadata and storage
/// targets and depends on target/stripe mappings; it is not a self-contained disk image.
/// </para>
/// <para>
/// CompressionWorkbench's filesystem-driver boundary currently opens one <see cref="Stream"/>.
/// That cannot represent a BeeGFS deployment or an offline cluster snapshot, so this descriptor
/// deliberately fails closed instead of advertising a synthetic extension, magic signature,
/// archive projection, or mounted namespace.
/// </para>
/// <para>
/// References: BeeGFS manual installation documentation (storage directories on local
/// filesystems) and the upstream BeeGFS storage-toolkit format/target marker definitions.
/// </para>
/// </remarks>
public sealed class BeeGfsFormatDescriptor :
  IFormatDescriptor,
  IFilesystemDriverProvider,
  IFilesystemDriverReadinessProvider {

  private const FilesystemDriverReadinessLayer ReadOnlyRequired =
    FilesystemDriverReadinessLayer.ImageValidation |
    FilesystemDriverReadinessLayer.Namespace |
    FilesystemDriverReadinessLayer.SessionStableNodeIds |
    FilesystemDriverReadinessLayer.ReadData |
    FilesystemDriverReadinessLayer.RandomAccessRead;

  private const FilesystemDriverReadinessLayer ReadWriteRequired =
    ReadOnlyRequired |
    FilesystemDriverReadinessLayer.AllocationMap |
    FilesystemDriverReadinessLayer.WriteData |
    FilesystemDriverReadinessLayer.Truncate |
    FilesystemDriverReadinessLayer.NamespaceMutation |
    FilesystemDriverReadinessLayer.Flush |
    FilesystemDriverReadinessLayer.DurabilityModel |
    FilesystemDriverReadinessLayer.Concurrency;

  private static readonly string[] StreamModelLimitations = [
    "BeeGFS has no standalone filesystem image or canonical single-stream representation.",
    "BeeGFS metadata and storage targets are directories on underlying local filesystems such as ext4 or XFS.",
    "A logical BeeGFS namespace can require multiple metadata/storage targets plus target and stripe mappings; one Stream is insufficient.",
  ];

  /// <summary>Gets the registry id.</summary>
  public string Id => "BeeGfs";

  /// <summary>Gets the display name.</summary>
  public string DisplayName => "BeeGFS";

  /// <summary>Gets the category used by the filesystem package.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>
  /// Gets the single-stream capabilities. BeeGFS has none because it is not a standalone
  /// stream/image format.
  /// </summary>
  public FormatCapabilities Capabilities => FormatCapabilities.None;

  /// <summary>BeeGFS has no canonical file extension.</summary>
  public string DefaultExtension => string.Empty;

  /// <summary>BeeGFS has no canonical file extensions.</summary>
  public IReadOnlyList<string> Extensions => [];

  /// <summary>BeeGFS has no compound file extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  /// BeeGFS has no standalone stream header. Target directories are identified structurally
  /// by their service metadata, not by magic bytes at offset zero of one file.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <summary>There is no archive/storage method for a synthetic BeeGFS image.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [];

  /// <summary>BeeGFS is not a tar compound format.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Gets the registry family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Gets the format description.</summary>
  public string Description =>
    "BeeGFS is a distributed filesystem whose metadata and storage targets are directories " +
    "on local filesystems such as ext4/XFS. A logical namespace can span multiple targets " +
    "and requires target/stripe mappings, so BeeGFS has no standalone byte-stream image, " +
    "canonical .beegfs extension, or stream magic. CompressionWorkbench currently accepts " +
    "one Stream per filesystem driver; BeeGFS therefore remains registered but deliberately " +
    "non-mountable until a directory-/multi-target snapshot abstraction exists.";

  /// <inheritdoc />
  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    return new FilesystemDriverProfile(
      this.Id,
      "not stream-addressable",
      FilesystemDriverCapabilities.None,
      FilesystemMutationModel.None,
      CanMount: false,
      CanMountWritable: false,
      StreamModelLimitations);
  }

  /// <inheritdoc />
  public IFilesystemSession OpenFilesystem(Stream image, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    throw new NotSupportedException(
      "BeeGFS cannot be opened from one Stream. A real BeeGFS namespace requires metadata/storage " +
      "target directories and their target/stripe mappings; CompressionWorkbench has no multi-target " +
      "snapshot input contract yet.");
  }

  /// <inheritdoc />
  public FilesystemDriverReadinessReport DescribeFilesystemDriverReadiness(
      Stream image,
      FilesystemDriverTarget target) {
    ArgumentNullException.ThrowIfNull(image);

    IReadOnlyList<string> blockers = target switch {
      FilesystemDriverTarget.ReadWrite => [
        .. StreamModelLimitations,
        "Writable BeeGFS support additionally requires coordinated metadata, namespace, chunk, mapping, durability, and concurrency semantics across all participating targets.",
      ],
      _ => StreamModelLimitations,
    };

    return new FilesystemDriverReadinessReport(
      this.Id,
      target,
      FilesystemDriverReadinessLayer.None,
      target == FilesystemDriverTarget.ReadWrite ? ReadWriteRequired : ReadOnlyRequired,
      Derivable: false,
      UsesNativeProvider: true,
      blockers);
  }
}
