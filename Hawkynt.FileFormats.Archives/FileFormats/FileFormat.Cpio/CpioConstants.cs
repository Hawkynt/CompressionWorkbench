namespace FileFormat.Cpio;

/// <summary>
/// Constants for the cpio archive format.
/// </summary>
internal static class CpioConstants {
  /// <summary>Magic for "new" (SVR4) ASCII cpio format: "070701".</summary>
  public const string NewAsciiMagic = "070701";

  /// <summary>Magic for "new" CRC ASCII cpio format: "070702".</summary>
  public const string NewCrcMagic = "070702";

  /// <summary>Magic for the POSIX portable ASCII ("odc") cpio format: "070707".</summary>
  public const string PortableAsciiMagic = "070707";

  /// <summary>
  /// Magic word of the 7th Edition binary format: octal 070707 as a 16-bit
  /// integer. Written in the producing host's byte order, which is what makes
  /// the variant readable in two endiannesses.
  /// </summary>
  public const ushort BinaryMagic = 0x71C7;

  /// <summary>Trailer filename marking end of archive.</summary>
  public const string Trailer = "TRAILER!!!";

  /// <summary>Length of the fixed header in new ASCII format.</summary>
  public const int NewAsciiHeaderSize = 110;

  /// <summary>Length of the fixed header in the POSIX portable ASCII ("odc") format.</summary>
  public const int PortableAsciiHeaderSize = 76;

  /// <summary>Length of the fixed header in the 7th Edition binary format.</summary>
  public const int BinaryHeaderSize = 26;

  /// <summary>Largest value a 6-digit octal odc field can carry (8^6 - 1).</summary>
  public const uint PortableAsciiMaxShortField = 0x3FFFF;

  /// <summary>Largest value an 11-digit octal odc field can carry (8^11 - 1).</summary>
  public const long PortableAsciiMaxLongField = 0x1FFFFFFFFL;
}
