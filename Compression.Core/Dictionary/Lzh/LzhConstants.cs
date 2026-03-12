namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Constants for LZH (LHA) compression methods.
/// </summary>
public static class LzhConstants {
  /// <summary>Character code range: 0-255 literals + 256 match-length symbols (256..MaxMatch+253).</summary>
  public const int NChar = 256;

  /// <summary>Maximum match length for -lh5-/-lh6-/-lh7-.</summary>
  public const int MaxMatch = 256;

  /// <summary>Threshold: matches shorter than this are not worth encoding.</summary>
  public const int Threshold = 3;

  /// <summary>Number of literal/length symbols: 256 literals + (MaxMatch - Threshold + 1) length codes.</summary>
  public const int NumCodes = NChar + MaxMatch - Threshold + 1;

  /// <summary>Maximum number of position (offset) bits for -lh5- (8KB window).</summary>
  public const int Lh5PositionBits = 13;

  /// <summary>Maximum number of position (offset) bits for -lh6- (32KB window).</summary>
  public const int Lh6PositionBits = 15;

  /// <summary>Maximum number of position (offset) bits for -lh7- (64KB window).</summary>
  public const int Lh7PositionBits = 16;

  /// <summary>Number of position slots for -lh5-.</summary>
  public const int Lh5PositionSlots = 14;

  /// <summary>Number of position slots for -lh6-.</summary>
  public const int Lh6PositionSlots = 16;

  /// <summary>Number of position slots for -lh7-.</summary>
  public const int Lh7PositionSlots = 17;

  /// <summary>Block size for Huffman tree reset (number of codes per block).</summary>
  public const int BlockSize = 16384;

  /// <summary>Maximum bits for literal/length Huffman codes.</summary>
  public const int MaxCodeBits = 16;

  /// <summary>Maximum bits for position Huffman codes.</summary>
  public const int MaxPositionBits = 17;

  /// <summary>Number of code-length Huffman symbols (for encoding the main tree).</summary>
  public const int NumCodeLengthSymbols = 19;

  /// <summary>Special code-length value meaning "next 9 zero-length codes" when encoding code lengths.</summary>
  public const int CodeLengthRepeatZero = 0;

  /// <summary>Window size for -lh5- (8192 bytes).</summary>
  public const int Lh5WindowSize = 1 << Lh5PositionBits;

  /// <summary>Window size for -lh6- (32768 bytes).</summary>
  public const int Lh6WindowSize = 1 << Lh6PositionBits;

  /// <summary>Window size for -lh7- (65536 bytes).</summary>
  public const int Lh7WindowSize = 1 << Lh7PositionBits;
}
