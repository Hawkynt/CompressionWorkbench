namespace FileFormat.Crunch;

/// <summary>
/// Constants for the CP/M Crunch compression format.
/// </summary>
public static class CrunchConstants {
  /// <summary>First magic byte (0x76).</summary>
  public const byte Magic1 = 0x76;

  /// <summary>Second magic byte (0xFE).</summary>
  public const byte Magic2 = 0xFE;

  /// <summary>Minimum LZW code width (9 bits).</summary>
  public const int MinBits = 9;

  /// <summary>Maximum LZW code width (12 bits).</summary>
  public const int MaxBits = 12;
}
