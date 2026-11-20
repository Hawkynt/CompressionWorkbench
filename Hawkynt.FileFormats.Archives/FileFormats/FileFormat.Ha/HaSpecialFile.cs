namespace FileFormat.Ha;

/// <summary>What an HA special-file record (type 15) holds, from the file-type bits of its Unix mode.</summary>
public enum HaSpecialKind {
  /// <summary>The record carries no Unix mode, or one with a file type HA does not store as special.</summary>
  Unknown,

  /// <summary>A character device; the data is the device number.</summary>
  CharacterDevice,

  /// <summary>A block device; the data is the device number.</summary>
  BlockDevice,

  /// <summary>A symbolic link; the data is the link text and a terminating NUL.</summary>
  SymbolicLink,

  /// <summary>A named pipe; no data.</summary>
  Fifo,

  /// <summary>A Unix-domain socket; no data.</summary>
  Socket,
}

/// <summary>The decoded content of an HA special-file record (docs/HA-ON-DISK.md §1.4).</summary>
/// <param name="Kind">What the record holds.</param>
/// <param name="Data">The record's stored bytes, unchanged.</param>
/// <param name="LinkTarget">The link text of a symbolic link, without its NUL; otherwise null.</param>
/// <param name="DeviceNumber">The device number of a device, read little-endian from all of its 2, 4 or 8 stored bytes; otherwise null.</param>
/// <param name="DeviceMajor">
/// The major number of a device stored as an 8-byte number, split the way Linux (glibc) packs
/// <c>dev_t</c>; null for any other width, whose packing depends on the writing system.
/// </param>
/// <param name="DeviceMinor">The minor number, under the same condition as <paramref name="DeviceMajor"/>.</param>
public sealed record HaSpecialFile(
  HaSpecialKind Kind,
  byte[] Data,
  string? LinkTarget = null,
  ulong? DeviceNumber = null,
  uint? DeviceMajor = null,
  uint? DeviceMinor = null
);
