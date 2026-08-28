#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.D64;

namespace FileSystem.CbmNibble;

/// <summary>
/// Shared filesystem-driver bridge for canonical 1541 GCR media. It performs
/// two independent probes: GCR sector integrity first, then CBM DOS allocation
/// integrity. Only profiles that pass both are exposed as writable mounts.
/// </summary>
internal static class CbmNibbleFilesystemDriver {
  public enum ContainerKind { G64, Nib }

  public static IRandomAccessBlockDevice OpenBlockDevice(
      Stream image,
      ContainerKind kind,
      bool writable,
      bool leaveOpen) {
    IRawTrackDevice raw = kind switch {
      ContainerKind.G64 => new G64DirectRawTrackDevice(image, writable, leaveOpen),
      ContainerKind.Nib => CbmNibbleRawTrackDevices.OpenNib(image, writable, leaveOpen),
      _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
    try {
      return new CbmGcrSectorDevice(
        raw,
        writable,
        kind == ContainerKind.Nib ? CbmNibbleReader.NibTrackSize : null,
        ownsTracks: true);
    } catch {
      raw.Dispose();
      throw;
    }
  }

  public static FilesystemDriverProfile Probe(Stream image, ContainerKind kind) {
    ArgumentNullException.ThrowIfNull(image);
    var id = kind == ContainerKind.G64 ? "G64" : "Nib";
    var profileName = kind == ContainerKind.G64
      ? "CBM DOS 2.6 over canonical G64"
      : "CBM DOS 2.6 over canonical NIB";
    var limitations = new List<string> {
      "Filesystem projection requires all 35 standard 1541 tracks and all 683 sectors with valid GCR/header/data checksums.",
      "CBM DOS 2.6 is a flat root namespace; subdirectories, hard links, symlinks and transactions are unavailable.",
      "Node ids are stable for one mounted session but are not persistent across remounts.",
      "REL side-sector semantics and unclosed/splat files are read-only until their mutation rules are modeled.",
    };
    if (kind == ContainerKind.G64)
      limitations.Add("Writable G64 mounts require constant speed zones and blank/nonexistent odd half-tracks; copy-protection half-tracks stay raw/read-only.");

    if (!image.CanRead || !image.CanSeek) {
      limitations.Add("Mounting requires a readable, seekable image stream.");
      return BuildProfile(id, profileName, false, false, limitations);
    }

    var saved = image.Position;
    try {
      byte[] d64;
      try {
        image.Position = 0;
        using var block = OpenBlockDevice(image, kind, writable: false, leaveOpen: true);
        d64 = new byte[D64BlockDevice.DataLength];
        if (block.ReadBlocks(0, d64) != D64BlockDevice.SectorCount)
          throw new EndOfStreamException("GCR sector device did not return all 683 1541 sectors.");
      } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException) {
        limitations.Add(ex.Message);
        return BuildProfile(id, profileName, false, false, limitations);
      }

      var fs = D64MountValidator.Validate(d64);
      limitations.AddRange(fs.Limitations);
      if (!fs.CanRead) return BuildProfile(id, profileName, false, false, limitations);

      var writable = false;
      if (fs.CanWrite && image.CanWrite) {
        try {
          image.Position = 0;
          using var block = OpenBlockDevice(image, kind, writable: true, leaveOpen: true);
          writable = block.CanWrite;
        } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException) {
          limitations.Add(ex.Message);
        }
      } else if (!image.CanWrite) {
        limitations.Add("Backing stream is not writable.");
      }

      return BuildProfile(id, profileName, true, writable, limitations);
    } finally {
      image.Position = saved;
    }
  }

  public static IFilesystemSession OpenFilesystem(
      Stream image,
      ContainerKind kind,
      FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var profile = Probe(image, kind);
    if (!profile.CanMount)
      throw new InvalidDataException($"{profile.FormatId} cannot be mounted as CBM DOS: {string.Join(" ", profile.Limitations)}");
    if (!options.ReadOnly && !profile.CanMountWritable)
      throw new NotSupportedException($"{profile.FormatId} is not safe for a writable CBM DOS mount: {string.Join(" ", profile.Limitations)}");

    image.Position = 0;
    var block = OpenBlockDevice(image, kind, writable: !options.ReadOnly, leaveOpen: options.LeaveOpen);
    return new D64FilesystemSession(block, profile, options.ReadOnly, ownsDevice: true);
  }

  private static FilesystemDriverProfile BuildProfile(
      string id,
      string profileName,
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
      id,
      profileName,
      capabilities,
      canWrite ? FilesystemMutationModel.Direct : FilesystemMutationModel.None,
      canMount,
      canWrite,
      limitations.Distinct().ToArray());
  }
}
