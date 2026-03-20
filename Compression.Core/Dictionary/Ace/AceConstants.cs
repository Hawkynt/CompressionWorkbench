namespace Compression.Core.Dictionary.Ace;

/// <summary>
/// Constants for the ACE compression algorithm.
/// </summary>
public static class AceConstants {
  /// <summary>Compression type: stored (no compression).</summary>
  public const int CompStore = 0;

  /// <summary>Compression type: ACE 1.0 (LZ77 + Huffman).</summary>
  public const int CompAce10 = 1;

  /// <summary>Compression type: ACE 2.0 (blocked, multiple sub-modes).</summary>
  public const int CompAce20 = 2;

  /// <summary>Main Huffman tree: 284 symbols (256 literals + 28 length codes).</summary>
  public const int MainSymbols = 284;

  /// <summary>Length tree: 255 symbols for extended match lengths.</summary>
  public const int LenSymbols = 255;

  /// <summary>Symbol 256: end of block marker.</summary>
  public const int SymbolEndOfBlock = 256;

  /// <summary>Symbol 283: ACE 2.0 mode switch marker.</summary>
  public const int SymbolModeSwitch = 283;

  /// <summary>Symbol range 257-282: match length base codes (length 2..28+).</summary>
  public const int SymbolMatchBase = 257;

  /// <summary>Number of repeated offsets maintained.</summary>
  public const int NumRepOffsets = 4;

  /// <summary>Minimum dictionary bits.</summary>
  public const int MinDictBits = 10;

  /// <summary>Maximum dictionary bits.</summary>
  public const int MaxDictBits = 22;

  /// <summary>Default dictionary bits.</summary>
  public const int DefaultDictBits = 15;

  /// <summary>Match length bases for symbols 257-283.</summary>
  public static readonly int[] LengthBase = [
    2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
    12, 14, 16, 20, 24, 32, 40, 56, 72, 104,
    136, 200, 264, 392, 520, 776, 1032
  ];

  /// <summary>Extra bits for match length symbols 257-283.</summary>
  public static readonly int[] LengthExtra = [
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    1, 1, 2, 2, 3, 3, 4, 4, 5, 5,
    6, 6, 7, 7, 8, 8, 8
  ];
}
