namespace FileFormat.Cpio;

/// <summary>
/// The on-disk CPIO header variants <see cref="CpioReader"/> reads and
/// <see cref="CpioWriter"/> writes — the same set libarchive's <c>bsdcpio</c>
/// calls <c>bin</c>, <c>odc</c>, <c>newc</c> and <c>crc</c>.
/// </summary>
public enum CpioArchiveFormat {
  /// <summary>SVR4 "new" ASCII (<c>070701</c>): hexadecimal fields, header+name and data aligned to 4 bytes.</summary>
  NewAscii,

  /// <summary>SVR4 CRC (<c>070702</c>): identical to <see cref="NewAscii"/> plus an additive byte-sum over the payload.</summary>
  NewCrc,

  /// <summary>POSIX portable ASCII / <c>odc</c> (<c>070707</c>): octal fields, no alignment padding anywhere.</summary>
  PortableAscii,

  /// <summary>7th Edition binary, 16-bit words in little-endian order — what a PDP-11-descended x86 host writes.</summary>
  BinaryLittleEndian,

  /// <summary>7th Edition binary, 16-bit words in big-endian order — the same header written on a big-endian host.</summary>
  BinaryBigEndian,
}
