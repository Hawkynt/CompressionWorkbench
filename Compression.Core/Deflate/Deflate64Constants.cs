namespace Compression.Core.Deflate;

/// <summary>
/// Constants for Deflate64 (Enhanced Deflate) as used by ZIP method 9.
/// Extends standard Deflate with a 64KB window and larger distance/length ranges.
/// </summary>
public static class Deflate64Constants {
  /// <summary>Sliding window size in bytes (64 KB).</summary>
  public const int WindowSize = 65536;

  /// <summary>Number of distance symbols (0-31).</summary>
  public const int DistanceAlphabetSize = 32;

  /// <summary>Maximum match length in Deflate64.</summary>
  public const int MaxMatchLength = 65538;

  /// <summary>
  /// Base distances for distance codes 0-31.
  /// Extends standard Deflate with codes 30-31 for distances up to 65536.
  /// </summary>
  public static ReadOnlySpan<int> DistanceBase =>
  [
    1, 2, 3, 4,                      // 0-3
    5, 7, 9, 13,                     // 4-7
    17, 25, 33, 49,                  // 8-11
    65, 97, 129, 193,                // 12-15
    257, 385, 513, 769,              // 16-19
    1025, 1537, 2049, 3073,          // 20-23
    4097, 6145, 8193, 12289,         // 24-27
    16385, 24577, 32769, 49153       // 28-31
  ];

  /// <summary>
  /// Extra bits for distance codes 0-31.
  /// </summary>
  public static ReadOnlySpan<int> DistanceExtraBits =>
  [
    0, 0, 0, 0,                      // 0-3
    1, 1, 2, 2,                      // 4-7
    3, 3, 4, 4,                      // 8-11
    5, 5, 6, 6,                      // 12-15
    7, 7, 8, 8,                      // 16-19
    9, 9, 10, 10,                    // 20-23
    11, 11, 12, 12,                  // 24-27
    13, 13, 14, 14                   // 28-31
  ];

  /// <summary>
  /// Base lengths for length codes 257-285.
  /// In Deflate64, code 285 has base 3 with 16 extra bits (range 3-65538).
  /// </summary>
  public static ReadOnlySpan<int> LengthBase =>
  [
    3, 4, 5, 6, 7, 8, 9, 10,       // 257-264
    11, 13, 15, 17,                  // 265-268
    19, 23, 27, 31,                  // 269-272
    35, 43, 51, 59,                  // 273-276
    67, 83, 99, 115,                 // 277-280
    131, 163, 195, 227,              // 281-284
    3                                // 285: base 3, 16 extra bits
  ];

  /// <summary>
  /// Extra bits for length codes 257-285.
  /// In Deflate64, code 285 has 16 extra bits.
  /// </summary>
  public static ReadOnlySpan<int> LengthExtraBits =>
  [
    0, 0, 0, 0, 0, 0, 0, 0,         // 257-264
    1, 1, 1, 1,                      // 265-268
    2, 2, 2, 2,                      // 269-272
    3, 3, 3, 3,                      // 273-276
    4, 4, 4, 4,                      // 277-280
    5, 5, 5, 5,                      // 281-284
    16                               // 285
  ];

  /// <summary>
  /// Maps a match distance (1-65536) to the corresponding distance code (0-31).
  /// </summary>
  /// <param name="distance">The match distance (1-65536).</param>
  /// <returns>The distance code (0-31).</returns>
  public static int GetDistanceCode(int distance) {
    if (distance is < 1 or > 65536)
      throw new ArgumentOutOfRangeException(nameof(distance), distance, "Distance must be between 1 and 65536.");

    var bases = DistanceBase;
    int lo = 0, hi = bases.Length - 1;
    while (lo < hi) {
      var mid = (lo + hi + 1) / 2;
      if (bases[mid] <= distance)
        lo = mid;
      else
        hi = mid - 1;
    }

    return lo;
  }
}
