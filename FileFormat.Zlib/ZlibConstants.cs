namespace FileFormat.Zlib;

/// <summary>
/// Constants for the zlib compressed data format (RFC 1950).
/// </summary>
public static class ZlibConstants {
  /// <summary>Deflate compression method identifier.</summary>
  public const int CompressionMethodDeflate = 8;

  /// <summary>Default window size exponent (15 → 32 KB window).</summary>
  public const int DefaultWindowBits = 15;

  /// <summary>Size of the zlib header in bytes (CMF + FLG).</summary>
  public const int HeaderSize = 2;

  /// <summary>Size of the Adler-32 trailer in bytes.</summary>
  public const int TrailerSize = 4;

  // FLG.FLEVEL values
  /// <summary>Compressor used fastest algorithm.</summary>
  public const int LevelFastest = 0;

  /// <summary>Compressor used fast algorithm.</summary>
  public const int LevelFast = 1;

  /// <summary>Compressor used default algorithm.</summary>
  public const int LevelDefault = 2;

  /// <summary>Compressor used maximum compression.</summary>
  public const int LevelMaximum = 3;
}
