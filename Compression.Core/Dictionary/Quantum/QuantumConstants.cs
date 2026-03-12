namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Constants for the Quantum compression format (used in Microsoft CAB files).
/// </summary>
internal static class QuantumConstants {
  /// <summary>Maximum window level (1 through 7).</summary>
  public const int MaxWindowLevel = 7;

  /// <summary>Minimum window level.</summary>
  public const int MinWindowLevel = 1;

  /// <summary>
  /// Returns the window size in bytes for a given level (1-based).
  /// Level 1 = 1 KB, level 2 = 2 KB, ..., level 7 = 64 KB.
  /// </summary>
  /// <param name="level">Window level (1–7).</param>
  /// <returns>The window size in bytes.</returns>
  public static int WindowSize(int level) => 1024 << (level - 1);

  /// <summary>Number of literal symbols (0–255).</summary>
  public const int LiteralSymbols = 256;

  /// <summary>
  /// Number of selector symbols.
  /// 0 = literal, 1–4 = match with base length 4–7, 5 = match base 12, 6 = match base 24.
  /// </summary>
  public const int SelectorSymbols = 7;

  /// <summary>Number of length symbols for each match length model.</summary>
  public const int MatchLengthSymbols = 27;

  /// <summary>Maximum match length (base 24 + 26 extra = 50, or higher for other selectors).</summary>
  public const int MaxMatchLength = 50;

  /// <summary>
  /// Returns the minimum (base) match length for a selector value (1–6).
  /// </summary>
  /// <param name="selector">The selector symbol (1–6).</param>
  /// <returns>The base match length.</returns>
  public static int BaseMatchLength(int selector) => selector switch {
    1 => 4,
    2 => 5,
    3 => 6,
    4 => 7,
    5 => 12,
    6 => 24,
    _ => throw new ArgumentOutOfRangeException(nameof(selector))
  };

  /// <summary>Rescale threshold for adaptive frequency models.</summary>
  public const int RescaleThreshold = 3800;
}
