namespace FileFormat.Gzip;

/// <summary>
/// Constants defined by RFC 1952 (GZIP file format).
/// </summary>
public static class GzipConstants {
  /// <summary>GZIP magic number byte 1.</summary>
  public const byte Magic1 = 0x1F;

  /// <summary>GZIP magic number byte 2.</summary>
  public const byte Magic2 = 0x8B;

  /// <summary>Compression method: Deflate.</summary>
  public const byte MethodDeflate = 8;

  /// <summary>Flag: file is probably ASCII text.</summary>
  public const byte FlagText = 0x01;

  /// <summary>Flag: header CRC16 is present.</summary>
  public const byte FlagHcrc = 0x02;

  /// <summary>Flag: extra field is present.</summary>
  public const byte FlagExtra = 0x04;

  /// <summary>Flag: original file name is present.</summary>
  public const byte FlagName = 0x08;

  /// <summary>Flag: comment is present.</summary>
  public const byte FlagComment = 0x10;

  /// <summary>OS code: FAT filesystem (MS-DOS, OS/2, NT/Win32).</summary>
  public const byte OsFat = 0;

  /// <summary>OS code: Unix.</summary>
  public const byte OsUnix = 3;

  /// <summary>OS code: unknown.</summary>
  public const byte OsUnknown = 255;
}
