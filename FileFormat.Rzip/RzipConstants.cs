namespace FileFormat.Rzip;

/// <summary>
/// Constants for the RZIP format.
/// </summary>
internal static class RzipConstants {

  /// <summary>Magic bytes: "RZIP" (0x52, 0x5A, 0x49, 0x50).</summary>
  public static ReadOnlySpan<byte> Magic => "RZIP"u8;

  /// <summary>Major version number.</summary>
  public const byte VersionMajor = 2;

  /// <summary>Minor version number.</summary>
  public const byte VersionMinor = 1;

  /// <summary>Header size in bytes: magic (4) + version (2) + original size (4).</summary>
  public const int HeaderSize = 10;

  /// <summary>Default chunk size for bzip2 compression (900 KB).</summary>
  public const int DefaultBlockSize = 900 * 1024;

  /// <summary>Tag byte indicating a literal block in the token stream.</summary>
  public const byte TagLiteral = 0;

  /// <summary>Tag byte indicating a match block in the token stream.</summary>
  public const byte TagMatch = 1;
}
