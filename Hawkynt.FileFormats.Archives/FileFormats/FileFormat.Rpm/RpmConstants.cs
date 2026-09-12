namespace FileFormat.Rpm;

/// <summary>
/// Constants for the RPM package format.
/// </summary>
internal static class RpmConstants {
  /// <summary>Magic bytes at the start of the RPM lead: 0xED 0xAB 0xEE 0xDB.</summary>
  public static ReadOnlySpan<byte> LeadMagic => [0xED, 0xAB, 0xEE, 0xDB];

  /// <summary>Total size of the RPM lead section in bytes.</summary>
  public const int LeadSize = 96;

  /// <summary>Magic bytes at the start of every header structure: 0x8E 0xAD 0xE8.</summary>
  public static ReadOnlySpan<byte> HeaderMagic => [0x8E, 0xAD, 0xE8];

  /// <summary>Expected version byte in every header structure.</summary>
  public const byte HeaderVersion = 1;

  /// <summary>Size of a single index entry in a header structure, in bytes.</summary>
  public const int IndexEntrySize = 16;

  /// <summary>Size of the fixed preamble of a header structure (magic + version + reserved + nindex + hsize).</summary>
  public const int HeaderPreambleSize = 16;

  // -------------------------------------------------------------------------
  // Header tag numbers (main header)
  // -------------------------------------------------------------------------

  /// <summary>Tag for the package name string.</summary>
  public const int TagName = 1000;

  /// <summary>Tag for the package version string.</summary>
  public const int TagVersion = 1001;

  /// <summary>Tag for the package release string.</summary>
  public const int TagRelease = 1002;

  /// <summary>Tag for the package architecture string.</summary>
  public const int TagArch = 1022;

  /// <summary>Tag identifying the payload format (e.g. "cpio").</summary>
  public const int TagPayloadFormat = 1124;

  /// <summary>Tag identifying the payload compressor (e.g. "gzip", "xz", "zstd").</summary>
  public const int TagPayloadCompressor = 1125;

  // -------------------------------------------------------------------------
  // Header entry type codes
  // -------------------------------------------------------------------------

  /// <summary>Entry type: null (unused).</summary>
  public const int TypeNull = 0;

  /// <summary>Entry type: single char.</summary>
  public const int TypeChar = 1;

  /// <summary>Entry type: uint8.</summary>
  public const int TypeInt8 = 2;

  /// <summary>Entry type: uint16, big-endian.</summary>
  public const int TypeInt16 = 3;

  /// <summary>Entry type: int32, big-endian.</summary>
  public const int TypeInt32 = 4;

  /// <summary>Entry type: int64, big-endian.</summary>
  public const int TypeInt64 = 5;

  /// <summary>Entry type: NUL-terminated string.</summary>
  public const int TypeString = 6;

  /// <summary>Entry type: raw binary data.</summary>
  public const int TypeBin = 7;

  /// <summary>Entry type: array of NUL-terminated strings.</summary>
  public const int TypeStringArray = 8;

  /// <summary>Entry type: internationalized string table.</summary>
  public const int TypeI18nString = 9;
}
