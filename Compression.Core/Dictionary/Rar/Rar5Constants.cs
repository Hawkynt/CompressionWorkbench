namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Constants for the RAR5 decompression algorithm.
/// </summary>
internal static class Rar5Constants {
  /// <summary>Maximum Huffman code length.</summary>
  public const int MaxCodeLength = 15;

  /// <summary>Number of main symbols: 256 literals + match-length/distance codes.</summary>
  public const int MainTableSize = 306;

  /// <summary>Number of offset (distance) symbols.</summary>
  public const int OffsetTableSize = 64;

  /// <summary>Number of low-offset symbols (4-bit low distance refinement).</summary>
  public const int LowOffsetTableSize = 16;

  /// <summary>Number of length symbols (additional match-length encoding).</summary>
  public const int LengthTableSize = 16;

  /// <summary>Number of code-length symbols for reading Huffman table definitions.</summary>
  public const int CodeLengthTableSize = 20;

  /// <summary>Maximum match length.</summary>
  public const int MaxMatchLength = 0x101 + 8;

  /// <summary>Minimum match length.</summary>
  public const int MinMatchLength = 2;

  /// <summary>Number of repeated offsets tracked.</summary>
  public const int RepeatOffsets = 4;

  /// <summary>First literal symbol in the main table.</summary>
  public const int LiteralBase = 0;

  /// <summary>Number of literal symbols (byte values 0-255).</summary>
  public const int LiteralCount = 256;

  /// <summary>First match-length symbol in the main table.</summary>
  public const int MatchLengthBase = 256;

  /// <summary>Symbol for repeated offset 0 (last used distance).</summary>
  public const int RepeatOffset0 = 256;

  /// <summary>Symbol for repeated offset 1.</summary>
  public const int RepeatOffset1 = 257;

  /// <summary>Symbol for repeated offset 2.</summary>
  public const int RepeatOffset2 = 258;

  /// <summary>Symbol for repeated offset 3.</summary>
  public const int RepeatOffset3 = 259;

  /// <summary>First symbol encoding match length + distance.</summary>
  public const int MatchBase = 262;

  /// <summary>Symbol indicating a filter block follows.</summary>
  public const int FilterSymbol = 258;

  /// <summary>End of block marker in the main table.</summary>
  public const int EndOfBlock = 256;

  /// <summary>RAR5 filter type: delta filter.</summary>
  public const int FilterDelta = 0;

  /// <summary>RAR5 filter type: E8/E9 (x86 call/jump) filter.</summary>
  public const int FilterE8E9 = 1;

  /// <summary>RAR5 filter type: ARM filter.</summary>
  public const int FilterArm = 2;

  /// <summary>Maximum number of RAR5 filter types.</summary>
  public const int MaxFilterTypes = 3;

  /// <summary>Minimum dictionary size (128 KB).</summary>
  public const int MinDictionarySize = 128 * 1024;

  /// <summary>Maximum dictionary size for RAR5 (4 GB, represented as long).</summary>
  public const long MaxDictionarySize = 4L * 1024 * 1024 * 1024;

  /// <summary>
  /// Length slot base values. Main table symbols 262+ encode match length and distance slot.
  /// Symbols 262-269 encode lengths 2, 3, 4, 5, 6, 7, 8, 9 (zero extra bits).
  /// Symbols 270-277 encode lengths 10+ (with extra bits from length table).
  /// </summary>
  public static ReadOnlySpan<int> MatchLengthSlotBase => [
    2, 3, 4, 5, 6, 7, 8, 9
  ];

  /// <summary>
  /// Number of extra bits for distance slots 0-63.
  /// Slot 0-3: 0 extra bits (distances 0-3).
  /// Slot 4-5: 1 extra bit, slot 6-7: 2 extra bits, etc.
  /// </summary>
  public static int DistanceExtraBits(int slot) =>
    slot < 4 ? 0 : (slot - 2) >> 1;

  /// <summary>
  /// Base value for distance slot.
  /// </summary>
  public static int DistanceBase(int slot) {
    if (slot < 4)
      return slot;

    var extraBits = (slot - 2) >> 1;
    return ((2 + (slot & 1)) << extraBits);
  }
}
