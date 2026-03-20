namespace Compression.Core.Entropy.Ppmd;

/// <summary>
/// Constants for PPMd (Prediction by Partial Matching, variant d) compression.
/// </summary>
internal static class PpmdConstants {
  /// <summary>Maximum supported model order (32 for PPMd-H / 7z, ZIP uses up to 16).</summary>
  public const int MaxOrder = 32;

  /// <summary>Maximum model order for PPMd variant I (ZIP).</summary>
  public const int MaxOrderI = 16;

  /// <summary>Default model order.</summary>
  public const int DefaultOrder = 6;

  /// <summary>Minimum allocator memory size (1 MB).</summary>
  public const int MinMemorySize = 1 << 20;

  /// <summary>Maximum allocator memory size (2 GB for 7z, ZIP uses up to 256 MB).</summary>
  public const int MaxMemorySize = int.MaxValue; // ~2 GB

  /// <summary>Default allocator memory size (16 MB).</summary>
  public const int DefaultMemorySize = 16 << 20;

  /// <summary>Size in bytes of a single allocation unit.</summary>
  public const int UnitSize = 12;

  /// <summary>Number of bits used in the frequency period counter.</summary>
  public const int PeriodBits = 7;

  /// <summary>Binary-context scale (2^14).</summary>
  public const int BinScale = 1 << 14;

  /// <summary>Total frequency scale bits for SEE.</summary>
  public const int TotBits = 14;

  /// <summary>Interval bit count for SEE rescaling.</summary>
  public const int IntervalBitCount = 7;

  /// <summary>Maximum frequency value before rescaling.</summary>
  public const int MaxFreq = 124;

  /// <summary>Alphabet size (all byte values).</summary>
  public const int NumSymbols = 256;
}
