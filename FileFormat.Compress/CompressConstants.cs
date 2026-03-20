namespace FileFormat.Compress;

/// <summary>
/// Constants for the Unix compress (.Z) file format.
/// </summary>
internal static class CompressConstants {
  /// <summary>Magic bytes: 0x1F 0x9D.</summary>
  public const byte Magic1 = 0x1F;
  public const byte Magic2 = 0x9D;

  /// <summary>Bit mask for the maximum code size in the flags byte.</summary>
  public const byte MaxBitsMask = 0x1F;

  /// <summary>Flag indicating block-mode compression (dictionary resets via clear code).</summary>
  public const byte BlockModeFlag = 0x80;

  /// <summary>Minimum LZW code width.</summary>
  public const int MinBits = 9;

  /// <summary>Default maximum LZW code width.</summary>
  public const int DefaultMaxBits = 16;
}
