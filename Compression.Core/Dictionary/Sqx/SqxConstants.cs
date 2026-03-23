namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// Constants for the SQX compression algorithm (V11 and V20).
/// </summary>
public static class SqxConstants {
  // ── Symbol alphabet ───────────────────────────────────────────────

  /// <summary>Number of literal symbols (0-255).</summary>
  public const int LitCodes = 256;

  /// <summary>Duplicate-last-match symbol (reuse last length + distance).</summary>
  public const int DupLastSymbol = 256;

  /// <summary>First repeated-offset symbol (257).</summary>
  public const int RepStart = 257;

  /// <summary>Number of repeated-offset symbols (257-260).</summary>
  public const int RepCodes = 4;

  /// <summary>First length-2 match symbol (261).</summary>
  public const int Len2Start = 261;

  /// <summary>Number of length-2 match symbols (261-268).</summary>
  public const int Len2Codes = 8;

  /// <summary>First length-3 match symbol (269).</summary>
  public const int Len3Start = 269;

  /// <summary>Number of length-3 match symbols (269-283).</summary>
  public const int Len3Codes = 15;

  /// <summary>First length-4+ match symbol (284).</summary>
  public const int LenStart = 284;

  /// <summary>Number of length-4+ match symbols (284-308).</summary>
  public const int LenCodes = 25;

  /// <summary>Total main Huffman tree symbols (310).</summary>
  public const int NC = 310;

  /// <summary>Pre-tree symbol count (codes 0-15 + RLE 16,17,18).</summary>
  public const int PreTreeSymbols = 19;

  /// <summary>Max code length in pre-tree.</summary>
  public const int PreTreeMaxBits = 7;

  /// <summary>Max code length in main/distance trees.</summary>
  public const int MainTreeMaxBits = 15;

  /// <summary>Number of distance tree symbols (depends on dict size, max 56).</summary>
  public const int MaxDistCodes = 56;

  /// <summary>Distance codes for 1MB dictionary.</summary>
  public const int DistCodes1M = 48;

  /// <summary>Distance codes for 2MB dictionary.</summary>
  public const int DistCodes2M = 50;

  /// <summary>Distance codes for 4MB dictionary.</summary>
  public const int DistCodes4M = 52;

  // ── Match parameters ──────────────────────────────────────────────

  /// <summary>Minimum match length.</summary>
  public const int MinMatch = 2;

  /// <summary>Default dictionary size (32KB).</summary>
  public const int DefaultDictSize = 32768;

  /// <summary>Maximum dictionary size (4MB).</summary>
  public const int MaxDictSize = 4 * 1024 * 1024;

  /// <summary>Block size in symbols.</summary>
  public const int BlockSize = 16384;

  // ── Length-2 distance tables (for symbols 261-268) ────────────────

  /// <summary>Distance base values for length-2 matches.</summary>
  public static readonly int[] Len2Offsets = [0, 4, 8, 16, 32, 64, 128, 192];

  /// <summary>Extra bits for length-2 match distances.</summary>
  public static readonly int[] Len2ExtraBits = [2, 2, 3, 4, 5, 6, 6, 6];

  // ── Length-3 distance tables (for symbols 269-283) ────────────────

  /// <summary>Distance base values for length-3 matches.</summary>
  public static readonly int[] Len3Offsets = [0, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192];

  /// <summary>Extra bits for length-3 match distances.</summary>
  public static readonly int[] Len3ExtraBits = [0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];

  // ── Length 4+ tables (for symbols 284-308) ────────────────────────

  /// <summary>Length base values for length-4+ matches.</summary>
  public static readonly int[] LenOffsets = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 160, 192, 224, 0];

  /// <summary>Extra bits for length-4+ matches.</summary>
  public static readonly int[] LenExtraBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 5, 5, 5, 0];

  // ── Max distances for short matches ───────────────────────────────

  /// <summary>Maximum distance for a length-2 match (255).</summary>
  public const int MaxDistLen2 = 0xFF;

  /// <summary>Maximum distance for a length-3 match (16383).</summary>
  public const int MaxDistLen3 = 0x3FFF;

  /// <summary>Maximum distance for a length-4+ match (262143).</summary>
  public const int MaxDistLen4 = 0x3FFFF;

  // ── Codec block header flags ──────────────────────────────────────

  /// <summary>Block type bit: 0 = LZ block, 1 = multimedia block.</summary>
  public const int LzBlockType = 0;

  /// <summary>Huffman table encoding: use pre-tree for code lengths.</summary>
  public const int CodeTreeTabs = 0;

  /// <summary>Huffman table encoding: use flat 4-bit lengths.</summary>
  public const int CodeFlatTabs = 1;

  /// <summary>Three Huffman trees mode.</summary>
  public const int CodeThreeTrees = 0;

  /// <summary>Four Huffman trees mode (V20 extended).</summary>
  public const int CodeFourTrees = 1;

  // ── Helpers ───────────────────────────────────────────────────────

  /// <summary>
  /// Gets the number of distance tree symbols for a given dictionary size.
  /// </summary>
  public static int GetDistSlots(int dictSize) {
    var kb = dictSize >> 10;
    if (kb <= 1024) return DistCodes1M;
    if (kb <= 2048) return DistCodes2M;
    return DistCodes4M;
  }

  /// <summary>
  /// Gets the length-4+ symbol index for a given raw match length.
  /// </summary>
  /// <returns>Index into LenOffsets/LenExtraBits (0-23). Returns 24 for lengths requiring code 308 (14-bit escape).</returns>
  public static int GetLenCode(int length) {
    var rawLen = length - 4; // subtract base of 4
    // Code 24 (sym 308) is the escape code for lengths > 224+max_extra
    // Normal codes are 0-23; code 24 uses 14 raw bits
    if (rawLen > 224 + (1 << LenExtraBits[23]) - 1)
      return 24; // escape: 14 raw bits + 257
    for (var i = 23; i >= 0; --i) {
      if (rawLen >= LenOffsets[i])
        return i;
    }
    return 0;
  }

  /// <summary>
  /// Gets the length-2 distance code index.
  /// </summary>
  public static int GetLen2DistCode(int distance) {
    for (var i = Len2Codes - 1; i >= 0; --i) {
      if (distance >= Len2Offsets[i])
        return i;
    }
    return 0;
  }

  /// <summary>
  /// Gets the length-3 distance code index.
  /// </summary>
  public static int GetLen3DistCode(int distance) {
    for (var i = Len3Codes - 1; i >= 0; --i) {
      if (distance >= Len3Offsets[i])
        return i;
    }
    return 0;
  }
}
