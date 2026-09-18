namespace FileFormat.Cpio;

/// <summary>
/// Represents a single entry in a cpio archive.
/// </summary>
public sealed class CpioEntry {
  /// <summary>
  /// Gets or sets the on-disk header variant this entry was parsed from (or is
  /// to be written as). CPIO records the variant per entry rather than per
  /// archive, so this is a property of the entry and not of the reader.
  /// </summary>
  public CpioArchiveFormat Format { get; set; } = CpioArchiveFormat.NewAscii;

  /// <summary>Gets or sets the file name.</summary>
  public string Name { get; set; } = "";

  /// <summary>Gets or sets the inode number.</summary>
  public uint Inode { get; set; }

  /// <summary>Gets or sets the file mode (permissions + type).</summary>
  public uint Mode { get; set; }

  /// <summary>Gets or sets the owner UID.</summary>
  public uint Uid { get; set; }

  /// <summary>Gets or sets the owner GID.</summary>
  public uint Gid { get; set; }

  /// <summary>Gets or sets the number of hard links.</summary>
  public uint NumLinks { get; set; } = 1;

  /// <summary>Gets or sets the modification time (Unix timestamp).</summary>
  public uint ModificationTime { get; set; }

  /// <summary>Gets or sets the file size in bytes.</summary>
  public long FileSize { get; set; }

  /// <summary>Gets or sets the device major number.</summary>
  public uint DevMajor { get; set; }

  /// <summary>Gets or sets the device minor number.</summary>
  public uint DevMinor { get; set; }

  /// <summary>Gets or sets the rdev major number (for device files).</summary>
  public uint RDevMajor { get; set; }

  /// <summary>Gets or sets the rdev minor number (for device files).</summary>
  public uint RDevMinor { get; set; }

  /// <summary>
  /// Gets or sets the header's checksum field, meaningful only for
  /// <see cref="CpioArchiveFormat.NewCrc"/>. Despite the variant's name this is
  /// not a CRC-32 but the unsigned sum of every payload byte, taken modulo
  /// 2^32 — see <c>cpio(5)</c>.
  /// </summary>
  public uint Checksum { get; set; }

  /// <summary>Gets whether this entry is a directory.</summary>
  public bool IsDirectory => (this.Mode & 0xF000) == 0x4000;

  /// <summary>Gets whether this entry is a regular file.</summary>
  public bool IsRegularFile => (this.Mode & 0xF000) == 0x8000;

  /// <summary>Gets whether this entry is a symbolic link.</summary>
  public bool IsSymlink => (this.Mode & 0xF000) == 0xA000;
}
