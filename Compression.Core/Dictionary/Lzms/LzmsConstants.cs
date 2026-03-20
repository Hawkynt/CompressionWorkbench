namespace Compression.Core.Dictionary.Lzms;

/// <summary>
/// Constants for the LZMS compression algorithm.
/// </summary>
internal static class LzmsConstants {
  /// <summary>Number of LZ match offset slots.</summary>
  public const int NumLzOffsetSlots = 799;

  /// <summary>Maximum number of recent LZ offsets to track.</summary>
  public const int NumRecentLzOffsets = 3;

  /// <summary>Maximum number of recent delta offsets to track.</summary>
  public const int NumRecentDeltaOffsets = 3;

  /// <summary>Number of bits for the range coder probability.</summary>
  public const int NumProbBits = 6;

  /// <summary>Initial probability (50/50).</summary>
  public const int InitialProb = 1 << (NumProbBits - 1); // 32

  /// <summary>Total probability range.</summary>
  public const int ProbDenominator = 1 << NumProbBits; // 64

  /// <summary>Huffman rebuild interval for literal codes.</summary>
  public const int LiteralRebuildInterval = 1024;

  /// <summary>Huffman rebuild interval for LZ offset codes.</summary>
  public const int LzOffsetRebuildInterval = 1024;

  /// <summary>Huffman rebuild interval for length codes.</summary>
  public const int LengthRebuildInterval = 512;

  /// <summary>Huffman rebuild interval for delta power codes.</summary>
  public const int DeltaPowerRebuildInterval = 1024;

  /// <summary>Huffman rebuild interval for delta offset codes.</summary>
  public const int DeltaOffsetRebuildInterval = 1024;

  /// <summary>Number of literal symbols (256 bytes).</summary>
  public const int NumLiteralSymbols = 256;

  /// <summary>Number of length symbols.</summary>
  public const int NumLengthSymbols = 27;

  /// <summary>Number of delta power symbols.</summary>
  public const int NumDeltaPowerSymbols = 8;

  /// <summary>Number of delta offset slots.</summary>
  public const int NumDeltaOffsetSlots = 799;

  /// <summary>Minimum match length.</summary>
  public const int MinMatchLength = 2;

  // Length base values (for Huffman length decoding)
  public static readonly int[] LengthBase = [
    2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 16, 20, 24, 28, 32,
    40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224
  ];

  public static readonly int[] LengthExtraBits = [
    0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 2,
    3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 0
  ];
}
