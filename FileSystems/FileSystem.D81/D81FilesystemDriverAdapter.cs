#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.D81;

/// <summary>
/// Native mount-grade filesystem sidecar for standard Commodore 1581 D81 images.
/// </summary>
public sealed class D81FilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  public string FormatId => "D81";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var limitations = BaseLimitations();
    if (!image.CanRead || !image.CanSeek) {
      limitations.Add("Mounting requires a readable, seekable image stream.");
      return BuildProfile(false, false, limitations);
    }

    var saved = image.Position;
    try {
      if (image.Length < D81BlockDevice.DataLength) {
        limitations.Add($"Image is {image.Length} bytes; an 80-track D81 needs at least {D81BlockDevice.DataLength} bytes.");
        return BuildProfile(false, false, limitations);
      }
      image.Position = 0;
      var data = new byte[D81BlockDevice.DataLength];
      image.ReadExactly(data);
      var validation = D81MountValidator.Validate(data);
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
    var device = new D81BlockDevice(image, writable: !options.ReadOnly, leaveOpen: options.LeaveOpen);
    return new D81FilesystemSession(device, profile, options.ReadOnly, ownsDevice: true);
  }

  public FilesystemDriverProfile ProbeFilesystem(IRandomAccessBlockDevice device) {
    ArgumentNullException.ThrowIfNull(device);
    var limitations = BaseLimitations();
    if (device.Geometry.LogicalBlockSize != D81BlockDevice.LogicalSectorSize ||
        device.Geometry.BlockCount < D81BlockDevice.SectorCount) {
      limitations.Add(
        $"D81 requires at least {D81BlockDevice.SectorCount} logical blocks of {D81BlockDevice.LogicalSectorSize} bytes; " +
        $"device provides {device.Geometry.BlockCount} × {device.Geometry.LogicalBlockSize}.");
      return BuildProfile(false, false, limitations);
    }

    try {
      var data = new byte[D81BlockDevice.DataLength];
      var blocks = device.ReadBlocks(0, data);
      if (blocks != D81BlockDevice.SectorCount)
        throw new EndOfStreamException($"Block device returned {blocks} sectors; expected {D81BlockDevice.SectorCount}.");
      var validation = D81MountValidator.Validate(data);
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
    return new D81FilesystemSession(device, profile, options.ReadOnly, ownsDevice: !options.LeaveOpen);
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
      blockers.Add("Writable D81 mounting is limited to canonical closed SEQ/PRG/USR files with exact BAM ownership.");
      blockers.Add("REL super-side-sector mutation and CBM partition/subdirectory semantics remain intentionally unsupported.");
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
    "The native D81 driver currently exposes the ordinary flat root namespace; CBM partition/subdirectory entries are not mounted.",
    "Node ids are stable for the lifetime of one mounted session but are not persistent across remounts.",
    "Writable mounting supports ordinary closed SEQ/PRG/USR files; REL super-side-sector semantics fail closed.",
    "Track 40 is reserved for the header, BAM sectors and directory metadata.",
    "Hard links, symlinks and crash-atomic transactions are unavailable in the supported CBM DOS profile.",
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
      "D81",
      "CBM DOS / Commodore 1581 D81",
      capabilities,
      canWrite ? FilesystemMutationModel.Direct : FilesystemMutationModel.None,
      canMount,
      canWrite,
      limitations.Distinct(StringComparer.Ordinal).ToArray());
  }

  private static void EnsureOpenMode(FilesystemDriverProfile profile, bool readOnly) {
    if (!profile.CanMount)
      throw new InvalidDataException("D81 cannot be mounted: " + string.Join(" ", profile.Limitations));
    if (!readOnly && !profile.CanMountWritable)
      throw new NotSupportedException("D81 is not safe for writable mounting: " + string.Join(" ", profile.Limitations));
  }

  private static string FirstLine(string message) {
    var end = message.IndexOfAny(['\r', '\n']);
    return end < 0 ? message : message[..end];
  }
}
