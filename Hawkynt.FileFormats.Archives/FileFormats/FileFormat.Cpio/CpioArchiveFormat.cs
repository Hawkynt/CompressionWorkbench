namespace FileFormat.Cpio;

/// <summary>
/// On-disk CPIO header variants supported by <see cref="CpioReader"/> and <see cref="CpioWriter"/>.
/// </summary>
public enum CpioArchiveFormat {
  /// <summary>SVR4 new ASCII format (<c>070701</c>), with hexadecimal fields and 4-byte alignment.</summary>
  NewAscii,

  /// <summary>SVR4 CRC format (<c>070702</c>), identical to new ASCII except for the additive data checksum.</summary>
  NewCrc,

  /// <summary>POSIX portable ASCII / odc format (<c>070707</c>), with octal fields and no alignment padding.</summary>
  PortableAscii,

  /// <summary>7th Edition binary CPIO using little-endian 16-bit words.</summary>
  BinaryLittleEndian,

  /// <summary>7th Edition binary CPIO using big-endian 16-bit words.</summary>
  BinaryBigEndian,

  /// <summary>
  /// PWB/UNIX (6th Edition-derived) binary CPIO. Its byte layout is physically
  /// identical to the little-endian 7th Edition variant; only inode-mode
  /// semantics and representable file types/sizes differ.
  /// </summary>
  PwbBinary,
}
