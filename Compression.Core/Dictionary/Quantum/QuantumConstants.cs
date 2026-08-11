namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Constants shared by the Quantum compressor and decompressor.
/// </summary>
/// <remarks>
/// Every value here is part of the wire format: encoder and decoder must agree on
/// all of them, and so must any other implementation that wants to read the output.
/// </remarks>
internal static class QuantumConstants {
  /// <summary>Minimum window level.</summary>
  public const int MinWindowLevel = 1;

  /// <summary>Maximum window level (1 through 7).</summary>
  public const int MaxWindowLevel = 7;

  /// <summary>
  /// Returns the window size in bytes for a given level (1-based).
  /// Level 1 = 1 KB, level 2 = 2 KB, ..., level 7 = 64 KB.
  /// </summary>
  /// <param name="level">Window level (1–7).</param>
  /// <returns>The window size in bytes.</returns>
  public static int WindowSize(int level) => 1024 << (level - 1);

  /// <summary>Number of literal symbols (0–255).</summary>
  public const int LiteralSymbols = 256;

  /// <summary>Number of coding states in the literal/match context selector.</summary>
  public const int StateCount = 7;

  /// <summary>State transition after coding a literal, indexed by the current state.</summary>
  public static readonly int[] LiteralNextState = [0, 0, 0, 1, 2, 3, 4];

  /// <summary>State transition after coding a match, indexed by the current state.</summary>
  public static readonly int[] MatchNextState = [4, 5, 6, 6, 6, 6, 6];

  /// <summary>Shortest run of bytes the match finder will encode as a match.</summary>
  public const int MinMatch = 3;

  /// <summary>Maximum number of hash-chain candidates examined per position.</summary>
  public const int MaxMatchChain = 64;

  /// <summary>
  /// Number of symbols in the magnitude-slot alphabet shared by the match length and
  /// match distance models. A slot is the bit length of the value being coded, so 40
  /// slots cover magnitudes far beyond any representable input.
  /// </summary>
  public const int SlotSymbols = 40;

  /// <summary>Amount added to a symbol's frequency each time the model observes it.</summary>
  public const int ModelIncrement = 24;

  /// <summary>
  /// Total frequency at which an adaptive model halves all counts. Chosen so that the
  /// 32-bit arithmetic coder's range always stays far larger than the total frequency,
  /// which is what keeps every symbol's sub-range non-empty.
  /// </summary>
  public const int ModelMaxTotal = 1 << 14;
}
