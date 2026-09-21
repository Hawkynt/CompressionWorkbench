#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.BeeGfs;

/// <summary>
/// Describes BeeGFS as a distributed target set rather than inventing a standalone
/// byte-stream image format.
/// </summary>
/// <remarks>
/// <para>
/// BeeGFS metadata and storage targets are directories on ordinary local Linux filesystems,
/// typically ext4/XFS. A logical BeeGFS namespace can span multiple metadata and storage
/// targets and depends on target/stripe mappings; it is not a self-contained disk image.
/// </para>
/// <para>
/// The legacy one-stream driver entry point therefore remains fail-closed. The multi-stream
/// entry point accepts named backing filesystem images through <see cref="FilesystemStreamSet"/>,
/// opens each one through CompressionWorkbench's normal filesystem parser, resolves its target
/// root, validates BeeGFS target markers/identities, and reconstructs the supported read-only
/// logical namespace from BeeGFS metadata rather than exposing physical target directories.
/// </para>
/// <para>
/// References: BeeGFS manual installation/architecture/deep-inspection documentation and the
/// upstream BeeGFS serialization/storage-toolkit definitions. Upstream source is used only as a
/// behavioral/layout oracle; this implementation is independently written against those format facts.
/// </para>
/// </remarks>
public sealed class BeeGfsFormatDescriptor :
  IFormatDescriptor,
  IFilesystemDriverProvider,
  IFilesystemDriverReadinessProvider,
  IMultiStreamFilesystemDriverProvider,
  IMultiStreamFilesystemDriverReadinessProvider {

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

  private const FilesystemDriverReadinessLayer NativeReadOnlyAvailable =
    ReadOnlyRequired |
    FilesystemDriverReadinessLayer.NativeStableNodeIds;

  private static readonly string[] StreamModelLimitations = [
    "BeeGFS has no standalone filesystem image or canonical single-stream representation.",
    "BeeGFS metadata and storage targets are directories on underlying local filesystems such as ext4 or XFS.",
    "Use FilesystemStreamSet to provide the participating metadata/storage backing images and target roots.",
  ];

  /// <summary>Gets the registry id.</summary>
  public string Id => "BeeGfs";

  /// <summary>Gets the display name.</summary>
  public string DisplayName => "BeeGFS";

  /// <summary>Gets the category used by the filesystem package.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>
  /// Single-stream format capabilities remain empty because BeeGFS is not one standalone image.
  /// Multi-target mounted capabilities are reported by <see cref="ProbeFilesystem(FilesystemStreamSet)"/>.
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
    "on local filesystems such as ext4/XFS. It has no standalone byte-stream image, canonical " +
    ".beegfs extension, or stream magic. CompressionWorkbench accepts a FilesystemStreamSet, " +
    "validates the real multi-target topology, and mounts the proven non-mirrored V3/V6 RAID0 " +
    "subset read-only, including V3 namespace dentries and V6 inline or separate regular-file " +
    "inodes. Unknown, mirrored, sparse, remote-storage and unsupported metadata profiles fail closed.";

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

  /// <summary>
  /// Validates the supplied metadata/storage target set and reports the read-only profile the
  /// reconstructed logical namespace supports. A target set that cannot be validated, or a
  /// namespace outside the supported V3/V6 non-mirrored RAID0 subset, is reported as
  /// non-mountable with the first failure line rather than partially exposed.
  /// </summary>
  public FilesystemDriverProfile ProbeFilesystem(FilesystemStreamSet sources) {
    ArgumentNullException.ThrowIfNull(sources);
    BeeGfsTargetTopology topology;
    try {
      topology = BeeGfsMultiStreamTopology.Inspect(sources);
    } catch (Exception e) when (IsProbeException(e)) {
      return UnsupportedProfile("invalid or unsupported BeeGFS target set", e.Message);
    }

    try {
      using var session = new BeeGfsReadOnlyFilesystemSession(sources, topology, leaveOpen: true);
      return session.Profile;
    } catch (Exception e) when (IsProbeException(e)) {
      return new FilesystemDriverProfile(
        this.Id,
        $"validated BeeGFS topology; unsupported logical profile ({topology.MetadataTargets.Count} metadata, {topology.StorageTargets.Count} storage)",
        FilesystemDriverCapabilities.None,
        FilesystemMutationModel.None,
        CanMount: false,
        CanMountWritable: false,
        [
          TopologySummary(topology),
          FirstLine(e.Message),
        ]);
    }
  }

  /// <inheritdoc />
  public IFilesystemSession OpenFilesystem(Stream image, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    throw new NotSupportedException(
      "BeeGFS cannot be opened from one Stream. Supply the metadata/storage backing images as a " +
      "FilesystemStreamSet so their target roots and identities can be validated.");
  }

  /// <summary>
  /// Opens the validated target set as a read-only session over the reconstructed logical
  /// namespace. Writable opens are refused: a BeeGFS mutation is not a write to one backing
  /// ext/XFS volume but a transaction across every participating target plus management state.
  /// </summary>
  public IFilesystemSession OpenFilesystem(FilesystemStreamSet sources, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(sources);
    ArgumentNullException.ThrowIfNull(options);
    if (!options.ReadOnly)
      throw new NotSupportedException(
        "Writable BeeGFS mounting is disabled until metadata/chunk allocation, mapping, buddy consistency, " +
        "durability/recovery and concurrency are transactional across the complete target set.");
    var topology = BeeGfsMultiStreamTopology.Inspect(sources);
    return new BeeGfsReadOnlyFilesystemSession(sources, topology, options.LeaveOpen);
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

  /// <summary>
  /// Reports which readiness layers the validated target set satisfies. The read-only target is
  /// derivable for a supported topology; the read-write target never is, and names the transaction,
  /// mapping and fault-injection work that stands between here and a writable BeeGFS mount.
  /// </summary>
  public FilesystemDriverReadinessReport DescribeFilesystemDriverReadiness(
      FilesystemStreamSet sources,
      FilesystemDriverTarget target) {
    ArgumentNullException.ThrowIfNull(sources);
    BeeGfsTargetTopology topology;
    try {
      topology = BeeGfsMultiStreamTopology.Inspect(sources);
    } catch (Exception e) when (IsProbeException(e)) {
      return new FilesystemDriverReadinessReport(
        this.Id,
        target,
        FilesystemDriverReadinessLayer.None,
        target == FilesystemDriverTarget.ReadWrite ? ReadWriteRequired : ReadOnlyRequired,
        Derivable: false,
        UsesNativeProvider: true,
        [FirstLine(e.Message)]);
    }

    try {
      using var session = new BeeGfsReadOnlyFilesystemSession(sources, topology, leaveOpen: true);
      var blockers = new List<string>(session.Profile.Limitations);
      if (target == FilesystemDriverTarget.ReadWrite) {
        blockers.Add(
          "Implement coordinated BeeGFS dentry/inode/chunk allocation and deletion across every participating target.");
        blockers.Add(
          "Integrate management target/buddy/storage-pool mappings and define crash-consistent multi-target commit/recovery semantics.");
        blockers.Add(
          "Add mounted mutation locking/concurrency plus interoperability and fault-injection validation before enabling writes.");
      }
      var required = target == FilesystemDriverTarget.ReadWrite ? ReadWriteRequired : ReadOnlyRequired;
      var available = NativeReadOnlyAvailable;
      return new FilesystemDriverReadinessReport(
        this.Id,
        target,
        available,
        required,
        Derivable: (available & required) == required,
        UsesNativeProvider: true,
        blockers.Distinct(StringComparer.Ordinal).ToArray());
    } catch (Exception e) when (IsProbeException(e)) {
      return new FilesystemDriverReadinessReport(
        this.Id,
        target,
        FilesystemDriverReadinessLayer.ImageValidation,
        target == FilesystemDriverTarget.ReadWrite ? ReadWriteRequired : ReadOnlyRequired,
        Derivable: false,
        UsesNativeProvider: true,
        [
          TopologySummary(topology),
          FirstLine(e.Message),
        ]);
    }
  }

  private FilesystemDriverProfile UnsupportedProfile(string name, string reason) => new(
    this.Id,
    name,
    FilesystemDriverCapabilities.None,
    FilesystemMutationModel.None,
    CanMount: false,
    CanMountWritable: false,
    [FirstLine(reason)]);

  private static string TopologySummary(BeeGfsTargetTopology topology)
    => $"Validated {topology.MetadataTargets.Count} metadata target(s) and {topology.StorageTargets.Count} storage target(s), including backing filesystem mounts, BeeGFS format versions, target roots, and numeric target identities.";

  private static bool IsProbeException(Exception e)
    => e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException;

  private static string FirstLine(string message) {
    var end = message.IndexOfAny(['\r', '\n']);
    return end < 0 ? message : message[..end];
  }
}
