namespace FileFormat.Cpio;

/// <summary>
/// Constants for the cpio archive format.
/// </summary>
internal static class CpioConstants {
  /// <summary>Magic for "new" (SVR4) ASCII cpio format: "070701".</summary>
  public const string NewAsciiMagic = "070701";

  /// <summary>Magic for "new" CRC ASCII cpio format: "070702".</summary>
  public const string NewCrcMagic = "070702";

  /// <summary>Magic for the POSIX portable ASCII / odc cpio format: "070707".</summary>
  public const string PortableAsciiMagic = "070707";

  /// <summary>Numeric magic used by the binary CPIO formats (octal 070707).</summary>
  public const ushort BinaryMagic = 0x71C7;

  /// <summary>Trailer filename marking end of archive.</summary>
  public const string Trailer = "TRAILER!!!";

  /// <summary>Length of the fixed header in new ASCII format.</summary>
  public const int NewAsciiHeaderSize = 110;

  /// <summary>Length of the fixed header in POSIX portable ASCII / odc format.</summary>
  public const int PortableAsciiHeaderSize = 76;

  /// <summary>Length of the fixed header in the historical binary formats.</summary>
  public const int BinaryHeaderSize = 26;
}
