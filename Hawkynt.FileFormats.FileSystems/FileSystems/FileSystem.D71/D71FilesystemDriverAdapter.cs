#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.D71;

/// <summary>
/// Native mount-grade filesystem sidecar for standard Commodore 1571 D71 images.
/// </summary>
public sealed class D71FilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  public string FormatId => "D71";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var limitations = BaseLimitations();
    if (!image.CanRead || !image.CanSeek) {
      limitations.Add("Mounting requires a readable, seekable image stream.");
      return BuildProfile(false, false, limitations);
    }

    var saved = image.Position;
    try {
      if (image.Length < D71BlockDevice.DataLength) {
        limitations.Add($"Image is {image.Length} bytes; a 70-track D71 needs at least {D71BlockDevice.DataLength} bytes.");
        return BuildProfile(false, false, limitations);
      }
      image.Position = 0;
      var data = new byte[D71BlockDevice.DataLength];
      image.ReadExactly(data);
      var validation = D71MountValidator.Validate(data);
      limitations.AddRange(validation.Limitations);
      return BuildProfile(validation.CanRead, validation.CanWrite && image.CanWrite, limitations);
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or ArgumentException) {
      limitations.Add(FirstLine(ex.Message));
      return BuildProfile(false, false, limitations);
    } finally {
      image.Position = saved;
    }
  }

  public IFilesystemSession OpenFilesystem(Stream image, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    var profile = ProbeFilesystem(image);
    EnsureOpenMode(profile, options.ReadOnly);
    var device = new D71BlockDevice(image, writable: !options.ReadOnly, leaveOpen: options.LeaveOpen);
    return new D71FilesystemSession(device, profile, options.ReadOnly, ownsDevice: true);
  }

  public FilesystemDriverProfile ProbeFilesystem(IRandomAccessBlockDevice device) {
    ArgumentNullException.ThrowIfNull(device);
    var limitations = BaseLimitations();
    if (device.Geometry.LogicalBlockSize != D71BlockDevice.LogicalSectorSize ||
        device.Geometry.BlockCount < D71BlockDevice.SectorCount) {
      limitations.Add(
        $"D71 requires at least {D71BlockDevice.SectorCount} logical blocks of {D71BlockDevice.LogicalSectorSize} bytes; " +
        $"device provides {device.Geometry.BlockCount} × {device.Geometry.LogicalBlockSize}.");
      return BuildProfile(false, false, limitations);
    }

    try {
      var data = new byte[D71BlockDevice.DataLength];
      var blocks = device.ReadBlocks(0, data);
      if (blocks != D71BlockDevice.SectorCount)
        throw new EndOfStreamException($"Block device returned {blocks} sectors; expected {D71BlockDevice.SectorCount}.");
      var validation = D71MountValidator.Validate(data);
      limitations.AddRange(validation.Limitations);
      return BuildProfile(validation.CanRead, validation.CanWrite && device.CanWrite, limitations);
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or ArgumentException) {
      limitations.Add(FirstLine(ex.Message));
      return BuildProfile(false, false, limitations);
    }
  }

  public IFilesystemSession OpenFilesystem(IRandomAccessBlockDevice device, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(device);
    ArgumentNullException.ThrowIfNull(options);
    var profile = ProbeFilesystem(device);
    EnsureOpenMode(profile, options.ReadOnly);
    return new D71FilesystemSession(device, profile, options.ReadOnly, ownsDevice: !options.LeaveOpen);
  }

  public FilesystemDriverReadinessReport DescribeFilesystemDriverReadiness(
      Stream image,
      FilesystemDriverTarget target) {
    var profile = ProbeFilesystem(image);
    const FilesystemDriverReadinessLayer readRequired =
      FilesystemDriverReadinessLayer.ImageValidation |
      FilesystemDriverReadinessLayer.Namespace |
      FilesystemDriverReadinessLayer.SessionStableNodeIds |
      FilesystemDriverReadinessLayer.ReadData |
      FilesystemDriverReadinessLayer.RandomAccessRead;
    const FilesystemDriverReadinessLayer writeRequired = readRequired |
      FilesystemDriverReadinessLayer.AllocationMap |
      FilesystemDriverReadinessLayer.WriteData |
      FilesystemDriverReadinessLayer.Truncate |
      FilesystemDriverReadinessLayer.NamespaceMutation |
      FilesystemDriverReadinessLayer.Flush |
      FilesystemDriverReadinessLayer.DurabilityModel |
      FilesystemDriverReadinessLayer.Concurrency;

    var available = profile.CanMount
      ? readRequired | FilesystemDriverReadinessLayer.AllocationMap | FilesystemDriverReadinessLayer.ValidationCorpus
      : FilesystemDriverReadinessLayer.None;
    if (profile.CanMountWritable)
      available |= writeRequired;

    var required = target == FilesystemDriverTarget.ReadOnly ? readRequired : writeRequired;
    var blockers = new List<string>(profile.Limitations);
    if (target == FilesystemDriverTarget.ReadWrite && !profile.CanMountWritable) {
      blockers.Add("Writable D71 mounting is limited to canonical closed SEQ/PRG/USR files with exact BAM ownership.");
      blockers.Add("REL side-sector mutation and recovery of splat/unclosed files remain intentionally unsupported.");
    }

    return new FilesystemDriverReadinessReport(
      FormatId,
      target,
      available,
      required,
      profile.CanMount && (available & required) == required,
      UsesNativeProvider: true,
      blockers.Distinct(StringComparer.Ordinal).ToArray());
  }

  private static List<string> BaseLimitations() => [
    "1571 CBM DOS is exposed as a flat root namespace; subdirectories, hard links, symlinks and transactions are unavailable.",
    "Node ids are stable for the lifetime of one mounted session but are not persistent across remounts.",
    "Writable mounting supports ordinary closed SEQ/PRG/USR files; REL side-sector semantics fail closed.",
    "Track 18 is reserved for BAM/directory metadata and track 53 is reserved for the side-2 BAM.",
  ];

  private static FilesystemDriverProfile BuildProfile(
      bool canMount,
      bool canWrite,
      IReadOnlyList<string> limitations) {
    var capabilities = FilesystemDriverCapabilities.None;
    if (canMount)
      capabilities = FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.Flush;
    if (canWrite)
      capabilities |= FilesystemDriverCapabilities.WriteData |
        FilesystemDriverCapabilities.Truncate |
        FilesystemDriverCapabilities.CreateFile |
        FilesystemDriverCapabilities.DeleteFile |
        FilesystemDriverCapabilities.Rename;
    return new FilesystemDriverProfile(
      "D71",
      "CBM DOS / Commodore 1571 D71",
      capabilities,
      canWrite ? FilesystemMutationModel.Direct : FilesystemMutationModel.None,
      canMount,
      canWrite,
      limitations.Distinct(StringComparer.Ordinal).ToArray());
  }

  private static void EnsureOpenMode(FilesystemDriverProfile profile, bool readOnly) {
    if (!profile.CanMount)
      throw new InvalidDataException("D71 cannot be mounted: " + string.Join(" ", profile.Limitations));
    if (!readOnly && !profile.CanMountWritable)
      throw new NotSupportedException("D71 is not safe for writable mounting: " + string.Join(" ", profile.Limitations));
  }

  private static string FirstLine(string message) {
    var end = message.IndexOfAny(['\r', '\n']);
    return end < 0 ? message : message[..end];
  }
}
