namespace Compression.Core.Dictionary.MsLzh;

/// <summary>
/// Alphabet constants for the MS LZH codec used by Microsoft DriveSpace 3
/// (Windows 95 Plus! Pack, 1995). The alphabet structure (286 literal/length
/// symbols + 30 distance symbols + end-of-block marker at 256) mirrors the
/// DEFLATE family, but with smaller window sizes and a different framing.
/// </summary>
internal static class MsLzhConstants {
  /// <summary>Sliding-window size (4 KiB) — DriveSpace 3 design point.</summary>
  internal const int WindowSize = 4096;

  /// <summary>Minimum match length emitted as a back-reference.</summary>
  internal const int MinMatch = 3;

  /// <summary>Maximum match length emitted as a back-reference.</summary>
  internal const int MaxMatch = 64;

  /// <summary>Literal byte values plus the end-of-block marker plus length symbols (286).</summary>
  internal const int LitLenAlphabetSize = 286;

  /// <summary>End-of-block symbol within the literal/length alphabet.</summary>
  internal const int EndOfBlockSymbol = 256;

  /// <summary>First length symbol within the literal/length alphabet.</summary>
  internal const int FirstLengthSymbol = 257;

  /// <summary>Distance alphabet size — 30 codes, like DEFLATE.</summary>
  internal const int DistanceAlphabetSize = 30;

  /// <summary>
  /// Length codes table (DEFLATE-style RFC 1951 §3.2.5): for each length symbol
  /// (257..285) the base length and the number of extra bits to read after the
  /// Huffman code. Symbol 257 = length 3 with 0 extra bits ... symbol 285 =
  /// length 64+ (capped at 64 in this codec, so symbol 285 means exactly 64).
  /// </summary>
  internal static readonly (int Base, int ExtraBits)[] LengthCodes = [
    (3, 0),   (4, 0),   (5, 0),   (6, 0),   (7, 0),   (8, 0),
    (9, 0),   (10, 0),
    (11, 1),  (13, 1),  (15, 1),  (17, 1),
    (19, 2),  (23, 2),  (27, 2),  (31, 2),
    (35, 3),  (43, 3),  (51, 3),  (59, 3),
    (67, 0),  // Will be capped — match length max is 64.
    (67, 0), (67, 0), (67, 0),
    (67, 0), (67, 0), (67, 0), (67, 0),
    (64, 0),  // Symbol 285 special-cased to exactly 64.
  ];

  /// <summary>
  /// Distance codes table (DEFLATE-style RFC 1951 §3.2.5): for each distance
  /// symbol (0..29) the base distance and the number of extra bits to read
  /// after the Huffman code. Symbol 0 = distance 1 with 0 extra bits, ...
  /// We only use codes 0..15 in this codec because the 4 KiB window caps
  /// distance at 4096 (covered by symbol 23).
  /// </summary>
  internal static readonly (int Base, int ExtraBits)[] DistanceCodes = [
    (1, 0),     (2, 0),     (3, 0),     (4, 0),
    (5, 1),     (7, 1),     (9, 2),     (13, 2),
    (17, 3),    (25, 3),    (33, 4),    (49, 4),
    (65, 5),    (97, 5),    (129, 6),   (193, 6),
    (257, 7),   (385, 7),   (513, 8),   (769, 8),
    (1025, 9),  (1537, 9),  (2049, 10), (3073, 10),
    (4097, 11), (6145, 11), (8193, 12), (12289, 12),
    (16385, 13),(24577, 13),
  ];

  /// <summary>
  /// Returns the length symbol (257..285) and the extra bits/value to emit for
  /// a given match length in [<see cref="MinMatch"/>, <see cref="MaxMatch"/>].
  /// </summary>
  internal static (int Symbol, int ExtraBits, int ExtraValue) EncodeLength(int length) {
    if (length == MaxMatch)
      return (285, 0, 0);
    for (var s = 0; s < LengthCodes.Length - 1; s++) {
      var (baseLen, extraBits) = LengthCodes[s];
      var maxLen = baseLen + (1 << extraBits) - 1;
      if (length >= baseLen && length <= maxLen)
        return (FirstLengthSymbol + s, extraBits, length - baseLen);
    }
    // Should not happen — length is clamped before this is called.
    return (285, 0, 0);
  }

  /// <summary>
  /// Returns the distance symbol (0..29) and the extra bits/value to emit for
  /// a given distance in [1, <see cref="WindowSize"/>].
  /// </summary>
  internal static (int Symbol, int ExtraBits, int ExtraValue) EncodeDistance(int distance) {
    for (var s = 0; s < DistanceCodes.Length; s++) {
      var (baseDist, extraBits) = DistanceCodes[s];
      var maxDist = baseDist + (1 << extraBits) - 1;
      if (distance >= baseDist && distance <= maxDist)
        return (s, extraBits, distance - baseDist);
    }
    // Should not happen — distance is clamped to WindowSize before call.
    return (0, 0, 0);
  }

  /// <summary>
  /// Decodes a length symbol (257..285) plus its extra-bits value into an
  /// actual match length, clamped to <see cref="MaxMatch"/>.
  /// </summary>
  internal static int DecodeLength(int symbol, int extraValue) {
    if (symbol == 285) return MaxMatch;
    var (baseLen, _) = LengthCodes[symbol - FirstLengthSymbol];
    var len = baseLen + extraValue;
    return len > MaxMatch ? MaxMatch : len;
  }

  /// <summary>
  /// Decodes a distance symbol (0..29) plus its extra-bits value into an
  /// actual distance.
  /// </summary>
  internal static int DecodeDistance(int symbol, int extraValue) {
    var (baseDist, _) = DistanceCodes[symbol];
    return baseDist + extraValue;
  }
}
