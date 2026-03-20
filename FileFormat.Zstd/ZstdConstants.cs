namespace FileFormat.Zstd;

/// <summary>
/// Constants for the Zstandard compression format (RFC 8878).
/// </summary>
internal static class ZstdConstants {
  /// <summary>Zstandard frame magic number.</summary>
  public const uint FrameMagic = 0xFD2FB528;

  /// <summary>Base magic for skippable frames (add 0..15).</summary>
  public const uint SkippableFrameMagicBase = 0x184D2A50;

  /// <summary>Block type: raw (uncompressed) data.</summary>
  public const byte BlockTypeRaw = 0;

  /// <summary>Block type: RLE (single repeated byte).</summary>
  public const byte BlockTypeRle = 1;

  /// <summary>Block type: compressed data (literals + sequences).</summary>
  public const byte BlockTypeCompressed = 2;

  /// <summary>Block type: reserved (invalid).</summary>
  public const byte BlockTypeReserved = 3;

  /// <summary>Maximum window log (log2 of window size).</summary>
  public const int MaxWindowLog = 31;

  /// <summary>Minimum window log (log2 of window size).</summary>
  public const int MinWindowLog = 10;

  /// <summary>Maximum block size in bytes (128 KB).</summary>
  public const int MaxBlockSize = 1 << 17;

  /// <summary>Minimum match length for sequence encoding.</summary>
  public const int MinMatch = 3;

  /// <summary>Default FSE table log for literal length codes.</summary>
  public const int LiteralLengthDefaultTableLog = 6;

  /// <summary>Default FSE table log for match length codes.</summary>
  public const int MatchLengthDefaultTableLog = 6;

  /// <summary>Default FSE table log for offset codes.</summary>
  public const int OffsetDefaultTableLog = 5;

  /// <summary>
  /// Predefined FSE distribution for literal length codes (codes 0-35).
  /// A value of -1 indicates sub-probability (exactly 1 table entry).
  /// </summary>
  public static readonly short[] DefaultLiteralLengthCounts =
  [
    4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1,
    2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1,
   -1, -1, -1, -1
  ];

  /// <summary>
  /// Predefined FSE distribution for match length codes (codes 0-52).
  /// A value of -1 indicates sub-probability (exactly 1 table entry).
  /// </summary>
  public static readonly short[] DefaultMatchLengthCounts =
  [
    1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
    1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1,
   -1, -1, -1, -1, -1
  ];

  /// <summary>
  /// Predefined FSE distribution for offset codes (codes 0-28).
  /// A value of -1 indicates sub-probability (exactly 1 table entry).
  /// </summary>
  public static readonly short[] DefaultOffsetCounts =
  [
    1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
    1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1
  ];

  /// <summary>Base values for literal length codes 0-35.</summary>
  public static readonly int[] LitLengthBase =
  [
    0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512,
    1024, 2048, 4096, 8192, 16384, 32768, 65536
  ];

  /// <summary>Extra bits for literal length codes 0-35.</summary>
  public static readonly int[] LitLengthExtraBits =
  [
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9,
    10, 11, 12, 13, 14, 15, 16
  ];

  /// <summary>Base values for match length codes 0-52.</summary>
  public static readonly int[] MatchLengthBase =
  [
    3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
    19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
    33, 34, 35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131,
    259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539
  ];

  /// <summary>Extra bits for match length codes 0-52.</summary>
  public static readonly int[] MatchLengthExtraBits =
  [
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7,
    8, 9, 10, 11, 12, 13, 14, 15, 16
  ];

  /// <summary>
  /// Returns the code for a given literal length value.
  /// </summary>
  /// <param name="litLength">The literal length value.</param>
  /// <returns>The code index (0-35).</returns>
  public static int GetLitLengthCode(int litLength) {
    // Codes 0-15 map directly
    if (litLength < 16)
      return litLength;

    // Search the base table for the correct code
    for (int code = LitLengthBase.Length - 1; code >= 16; --code) {
      if (litLength >= LitLengthBase[code])
        return code;
    }

    return 0;
  }

  /// <summary>
  /// Returns the code for a given match length value (already accounting for MinMatch offset).
  /// </summary>
  /// <param name="matchLength">The match length value (>= 3).</param>
  /// <returns>The code index (0-52).</returns>
  public static int GetMatchLengthCode(int matchLength) {
    // Codes 0-31 map to matchLength 3-34 directly
    if (matchLength <= 34)
      return matchLength - 3;

    // Search the base table for the correct code
    for (int code = MatchLengthBase.Length - 1; code >= 32; --code) {
      if (matchLength >= MatchLengthBase[code])
        return code;
    }

    return 0;
  }

  /// <summary>
  /// Returns the number of extra bits needed for an explicit offset value.
  /// Offset code N means the offset is in range [2^(N-1), 2^N) for N >= 1, or exactly 0 for N == 0.
  /// </summary>
  /// <param name="offset">The offset value (1-based).</param>
  /// <returns>The offset code.</returns>
  public static int GetOffsetCode(int offset) {
    // offset code = highest bit position
    var code = 0;
    int v = offset;
    while (v > 1) {
      v >>= 1;
      ++code;
    }

    return code;
  }
}
