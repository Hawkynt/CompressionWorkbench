namespace FileFormat.Cpio;

/// <summary>
/// Constants for the cpio archive format.
/// </summary>
internal static class CpioConstants {
  /// <summary>Magic for "new" (SVR4) ASCII cpio format: "070701".</summary>
  public const string NewAsciiMagic = "070701";

  /// <summary>Magic for "new" CRC ASCII cpio format: "070702".</summary>
  public const string NewCrcMagic = "070702";

  /// <summary>Trailer filename marking end of archive.</summary>
  public const string Trailer = "TRAILER!!!";

  /// <summary>Length of the fixed header in new ASCII format.</summary>
  public const int NewAsciiHeaderSize = 110;
}
