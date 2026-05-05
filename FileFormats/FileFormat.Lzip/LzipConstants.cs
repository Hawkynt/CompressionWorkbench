namespace FileFormat.Lzip;

/// <summary>
/// Constants for the Lzip (.lz) file format.
/// </summary>
public static class LzipConstants {
  /// <summary>Magic byte 0: 'L'.</summary>
  public const byte Magic0 = 0x4C;

  /// <summary>Magic byte 1: 'Z'.</summary>
  public const byte Magic1 = 0x5A;

  /// <summary>Magic byte 2: 'I'.</summary>
  public const byte Magic2 = 0x49;

  /// <summary>Magic byte 3: 'P'.</summary>
  public const byte Magic3 = 0x50;

  /// <summary>The only supported format version.</summary>
  public const byte Version = 1;

  /// <summary>Size of the Lzip member header in bytes.</summary>
  public const int HeaderSize = 6;

  /// <summary>Size of the Lzip member trailer in bytes.</summary>
  public const int TrailerSize = 20;

  /// <summary>Minimum allowed dictionary size (4 KiB).</summary>
  public const int MinDictionarySize = 4096;

  /// <summary>Maximum allowed dictionary size (512 MiB).</summary>
  public const int MaxDictionarySize = 512 * 1024 * 1024;

  /// <summary>
  /// LZMA property byte used by Lzip: lc=3, lp=0, pb=2.
  /// Computed as: lc + 9 * (lp + 5 * pb) = 3 + 9 * (0 + 10) = 93 = 0x5D.
  /// </summary>
  public const byte LzmaPropertiesByte = 0x5D;
}
