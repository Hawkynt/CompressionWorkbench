#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.MinixFs;

/// <summary>
/// Native mounted filesystem adapter for canonical MINIX v1/v2/v3 volumes.
/// The mounted path uses inode and zone bitmaps directly and never falls back to
/// the archive-level whole-image rebuild path.
/// </summary>
public sealed class MinixFilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  public string FormatId => "MinixFs";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var original = image.CanSeek ? image.Position : 0;
    try {
      var geometry = MinixMountedGeometry.Parse(image);
      var volume = new MinixMountedVolume(image, geometry);
      volume.ValidateNamespace();

      var writable = geometry.Version == MinixMountedVersion.V3
        || (geometry.State & MinixMountedGeometry.StateError) == 0;
      var capabilities =
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.CaseSensitiveNames |
        FilesystemDriverCapabilities.CasePreservingNames;
      if (writable) {
        capabilities |=
          FilesystemDriverCapabilities.WriteData |
          FilesystemDriverCapabilities.Truncate |
          FilesystemDriverCapabilities.CreateFile |
          FilesystemDriverCapabilities.DeleteFile |
          FilesystemDriverCapabilities.CreateDirectory |
          FilesystemDriverCapabilities.RemoveDirectory |
          FilesystemDriverCapabilities.Rename |
          FilesystemDriverCapabilities.HardLinks |
          FilesystemDriverCapabilities.SymbolicLinks |
          FilesystemDriverCapabilities.SetMetadata |
          FilesystemDriverCapabilities.SparseFiles |
          FilesystemDriverCapabilities.Flush;
      }

      var limitations = new List<string> {
        "Mounted access currently targets canonical one-block-per-zone, 1024-byte-block MINIX layouts; other zone/block geometries fail closed.",
        "MINIX has no on-disk inode generation field, so inode generations are session-local while inode numbers remain native stable identities.",
        geometry.Version == MinixMountedVersion.V1
          ? "MINIX v1 has one shared inode timestamp; mounted atime/mtime updates therefore refer to the same on-disk field."
          : "MINIX v2/v3 preserve separate atime, mtime and ctime fields.",
        geometry.Version == MinixMountedVersion.V3
          ? "MINIX v3 removed s_state; durability is direct bitmap/inode/zone writeback with fsck-based recovery."
          : "Writable MINIX v1/v2 mounts clear MINIX_VALID_FS while active and restore the saved mount state only on clean session disposal.",
        "MINIX exposes no journal transaction API; BeginTransaction remains unsupported.",
      };
      if (!writable)
        limitations.Add("The MINIX superblock carries MINIX_ERROR_FS; writable mounting is disabled until fsck.minix repairs the volume.");

      return new FilesystemDriverProfile(
        FormatId,
        geometry.VersionName + " native mounted driver",
        capabilities,
        writable ? FilesystemMutationModel.Direct : FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: writable,
        limitations);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return new FilesystemDriverProfile(
        FormatId,
        "unsupported or damaged MINIX profile",
        FilesystemDriverCapabilities.None,
        FilesystemMutationModel.None,
        CanMount: false,
        CanMountWritable: false,
        [FirstLine(e.Message)]);
    } finally {
      if (image.CanSeek) image.Position = original;
    }
  }

  public IFilesystemSession OpenFilesystem(Stream image, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("MINIX image is not mountable: " + string.Join("; ", profile.Limitations));
    if (!options.ReadOnly && !profile.CanMountWritable)
      throw new NotSupportedException("MINIX image is not safe for writable mounting: " + string.Join("; ", profile.Limitations));
    if (options.ReadOnly)
      return new MinixFilesystemSession(image, profile, readOnly: true, options.LeaveOpen);

    var geometry = MinixMountedGeometry.Parse(image);
    return geometry.Version == MinixMountedVersion.V3
      ? new MinixFilesystemSession(image, profile, readOnly: false, options.LeaveOpen)
      : MinixWritableMountStateSession.Open(image, profile, geometry, options.LeaveOpen);
  }

  public FilesystemDriverProfile ProbeFilesystem(IRandomAccessBlockDevice device) {
    using var stream = new BlockDeviceStream(device, leaveOpen: true);
    return ProbeFilesystem(stream);
  }

  public IFilesystemSession OpenFilesystem(IRandomAccessBlockDevice device, FilesystemOpenOptions options) {
    var stream = new BlockDeviceStream(device, leaveOpen: false);
    try {
      return OpenFilesystem(stream, options with { LeaveOpen = false });
    } catch {
      stream.Dispose();
      throw;
    }
  }

  public FilesystemDriverReadinessReport DescribeFilesystemDriverReadiness(
      Stream image,
      FilesystemDriverTarget target) {
    var profile = ProbeFilesystem(image);
    var readRequired =
      FilesystemDriverReadinessLayer.ImageValidation |
      FilesystemDriverReadinessLayer.Namespace |
      FilesystemDriverReadinessLayer.SessionStableNodeIds |
      FilesystemDriverReadinessLayer.ReadData |
      FilesystemDriverReadinessLayer.RandomAccessRead;
    var writeRequired = readRequired |
      FilesystemDriverReadinessLayer.AllocationMap |
      FilesystemDriverReadinessLayer.WriteData |
      FilesystemDriverReadinessLayer.Truncate |
      FilesystemDriverReadinessLayer.NamespaceMutation |
      FilesystemDriverReadinessLayer.MetadataMutation |
      FilesystemDriverReadinessLayer.Links |
      FilesystemDriverReadinessLayer.Flush |
      FilesystemDriverReadinessLayer.DurabilityModel |
      FilesystemDriverReadinessLayer.Recovery |
      FilesystemDriverReadinessLayer.Concurrency;

    var available = profile.CanMount
      ? readRequired |
        FilesystemDriverReadinessLayer.NativeStableNodeIds |
        FilesystemDriverReadinessLayer.AllocationMap
      : FilesystemDriverReadinessLayer.None;
    if (profile.CanMountWritable) {
      available |=
        FilesystemDriverReadinessLayer.WriteData |
        FilesystemDriverReadinessLayer.Truncate |
        FilesystemDriverReadinessLayer.NamespaceMutation |
        FilesystemDriverReadinessLayer.MetadataMutation |
        FilesystemDriverReadinessLayer.Links |
        FilesystemDriverReadinessLayer.Flush |
        FilesystemDriverReadinessLayer.DurabilityModel |
        FilesystemDriverReadinessLayer.Recovery |
        FilesystemDriverReadinessLayer.Concurrency;
    }

    var required = target == FilesystemDriverTarget.ReadOnly ? readRequired : writeRequired;
    var blockers = target == FilesystemDriverTarget.ReadWrite && !profile.CanMountWritable
      ? profile.Limitations
      : Array.Empty<string>();
    return new FilesystemDriverReadinessReport(
      FormatId,
      target,
      available,
      required,
      profile.CanMount && (available & required) == required,
      UsesNativeProvider: true,
      blockers.Distinct(StringComparer.Ordinal).ToArray());
  }

  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }
}