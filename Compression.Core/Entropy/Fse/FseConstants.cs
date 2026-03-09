namespace Compression.Core.Entropy.Fse;

/// <summary>
/// Constants for Finite State Entropy (FSE) coding.
/// </summary>
internal static class FseConstants {
  /// <summary>Maximum supported table log (log2 of table size).</summary>
  public const int MaxTableLog = 12;

  /// <summary>Default table log used when none is specified.</summary>
  public const int DefaultTableLog = 11;

  /// <summary>Minimum supported table log.</summary>
  public const int MinTableLog = 5;

  /// <summary>Maximum symbol value (byte alphabet).</summary>
  public const int MaxSymbolValue = 255;
}
