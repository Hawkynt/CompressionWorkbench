namespace Compression.Core.Deflate;

/// <summary>
/// Constants defined by RFC 1951 (DEFLATE Compressed Data Format).
/// </summary>
public static class DeflateConstants {
  /// <summary>Block type: uncompressed (no compression).</summary>
  public const int BlockTypeUncompressed = 0;

  /// <summary>Block type: compressed with fixed Huffman codes.</summary>
  public const int BlockTypeStaticHuffman = 1;

  /// <summary>Block type: compressed with dynamic Huffman codes.</summary>
  public const int BlockTypeDynamicHuffman = 2;

  /// <summary>Symbol for end of block in the literal/length alphabet.</summary>
  public const int EndOfBlock = 256;

  /// <summary>Maximum bit length for literal/length and distance Huffman codes.</summary>
  public const int MaxBits = 15;

  /// <summary>Maximum bit length for code-length Huffman codes.</summary>
  public const int MaxCodeLengthBits = 7;

  /// <summary>Sliding window size in bytes (32 KB).</summary>
  public const int WindowSize = 32768;

  /// <summary>Number of literal/length symbols (0–285).</summary>
  public const int LiteralLengthAlphabetSize = 286;

  /// <summary>Number of distance symbols (0–29).</summary>
  public const int DistanceAlphabetSize = 30;

  /// <summary>Number of code-length symbols (0–18).</summary>
  public const int CodeLengthAlphabetSize = 19;

  /// <summary>
  /// Base lengths for length codes 257–285.
  /// Index i corresponds to length code (257 + i).
  /// </summary>
  public static ReadOnlySpan<int> LengthBase =>
  [
    3, 4, 5, 6, 7, 8, 9, 10,       // 257–264
    11, 13, 15, 17,                  // 265–268
    19, 23, 27, 31,                  // 269–272
    35, 43, 51, 59,                  // 273–276
    67, 83, 99, 115,                 // 277–280
    131, 163, 195, 227,              // 281–284
    258                              // 285
  ];

  /// <summary>
  /// Extra bits for length codes 257–285.
  /// Index i corresponds to length code (257 + i).
  /// </summary>
  public static ReadOnlySpan<int> LengthExtraBits =>
  [
    0, 0, 0, 0, 0, 0, 0, 0,         // 257–264
    1, 1, 1, 1,                      // 265–268
    2, 2, 2, 2,                      // 269–272
    3, 3, 3, 3,                      // 273–276
    4, 4, 4, 4,                      // 277–280
    5, 5, 5, 5,                      // 281–284
    0                                // 285
  ];

  /// <summary>
  /// Base distances for distance codes 0–29.
  /// </summary>
  public static ReadOnlySpan<int> DistanceBase =>
  [
    1, 2, 3, 4,                      // 0–3
    5, 7, 9, 13,                     // 4–7
    17, 25, 33, 49,                  // 8–11
    65, 97, 129, 193,                // 12–15
    257, 385, 513, 769,              // 16–19
    1025, 1537, 2049, 3073,          // 20–23
    4097, 6145, 8193, 12289,         // 24–27
    16385, 24577                     // 28–29
  ];

  /// <summary>
  /// Extra bits for distance codes 0–29.
  /// </summary>
  public static ReadOnlySpan<int> DistanceExtraBits =>
  [
    0, 0, 0, 0,                      // 0–3
    1, 1, 2, 2,                      // 4–7
    3, 3, 4, 4,                      // 8–11
    5, 5, 6, 6,                      // 12–15
    7, 7, 8, 8,                      // 16–19
    9, 9, 10, 10,                    // 20–23
    11, 11, 12, 12,                  // 24–27
    13, 13                           // 28–29
  ];

  /// <summary>
  /// Permuted order for code-length code lengths in dynamic Huffman block headers.
  /// </summary>
  public static ReadOnlySpan<int> CodeLengthOrder =>
    [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

  /// <summary>
  /// Returns the fixed literal/length code lengths as defined in RFC 1951 section 3.2.6.
  /// </summary>
  public static int[] GetStaticLiteralLengths() {
    // RFC 1951 section 3.2.6 defines 288 codes (0–287) for the fixed table.
    // Symbols 286–287 are unused but must be included to produce correct
    // canonical code assignments for the other symbols.
    var lengths = new int[288];
    for (int i = 0; i <= 143; i++) lengths[i] = 8;
    for (int i = 144; i <= 255; i++) lengths[i] = 9;
    for (int i = 256; i <= 279; i++) lengths[i] = 7;
    for (int i = 280; i <= 287; i++) lengths[i] = 8;
    return lengths;
  }

  /// <summary>
  /// Returns the fixed distance code lengths as defined in RFC 1951 section 3.2.6.
  /// </summary>
  public static int[] GetStaticDistanceLengths() {
    var lengths = new int[DistanceAlphabetSize];
    Array.Fill(lengths, 5);
    return lengths;
  }

  /// <summary>
  /// Maps a match length (3–258) to the corresponding literal/length code (257–285).
  /// </summary>
  /// <param name="length">The match length (3–258).</param>
  /// <returns>The length code (257–285).</returns>
  public static int GetLengthCode(int length) {
    if (length < 3 || length > 258)
      throw new ArgumentOutOfRangeException(nameof(length), length, "Length must be between 3 and 258.");

    if (length == 258)
      return 285;

    // Binary search through LengthBase
    ReadOnlySpan<int> bases = LengthBase;
    int lo = 0, hi = bases.Length - 2; // exclude code 285 (handled above)
    while (lo < hi) {
      int mid = (lo + hi + 1) / 2;
      if (bases[mid] <= length)
        lo = mid;
      else
        hi = mid - 1;
    }

    return 257 + lo;
  }

  /// <summary>
  /// Maps a match distance (1–32768) to the corresponding distance code (0–29).
  /// </summary>
  /// <param name="distance">The match distance (1–32768).</param>
  /// <returns>The distance code (0–29).</returns>
  public static int GetDistanceCode(int distance) {
    if (distance < 1 || distance > 32768)
      throw new ArgumentOutOfRangeException(nameof(distance), distance, "Distance must be between 1 and 32768.");

    ReadOnlySpan<int> bases = DistanceBase;
    int lo = 0, hi = bases.Length - 1;
    while (lo < hi) {
      int mid = (lo + hi + 1) / 2;
      if (bases[mid] <= distance)
        lo = mid;
      else
        hi = mid - 1;
    }

    return lo;
  }
}
