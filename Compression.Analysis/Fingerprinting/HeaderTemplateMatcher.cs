#pragma warning disable CS1591

using Compression.Registry;

namespace Compression.Analysis.Fingerprinting;

/// <summary>
/// A match of a known format's magic signature found at a specific offset in the data.
/// </summary>
/// <param name="Offset">Byte offset where the signature was found.</param>
/// <param name="FormatId">Format identifier from the registry.</param>
/// <param name="DisplayName">Human-readable format name.</param>
/// <param name="Confidence">Match confidence (0.0-1.0). Exact matches use the signature's
/// declared confidence; fuzzy matches are scaled down proportionally.</param>
/// <param name="MatchType">Whether this was an exact or fuzzy match.</param>
/// <param name="MatchedBytes">Number of bytes that matched in the signature.</param>
/// <param name="TotalBytes">Total bytes in the signature pattern.</param>
public sealed record HeaderMatch(
  int Offset,
  string FormatId,
  string DisplayName,
  double Confidence,
  string MatchType,
  int MatchedBytes,
  int TotalBytes
);

/// <summary>
/// Compares unknown file headers against all known magic signatures from the format registry,
/// supporting both exact and fuzzy (partial) matching for reverse engineering unknown formats.
/// </summary>
public static class HeaderTemplateMatcher {

  /// <summary>Minimum signature length to consider for matching.</summary>
  private const int MinSignatureLength = 2;

  /// <summary>Minimum fraction of bytes that must match for a fuzzy match to be reported.</summary>
  private const double MinFuzzyMatchRatio = 0.75;

  /// <summary>
  /// Scans data against all known magic signatures from the format registry.
  /// Searches the first <paramref name="maxOffset"/> bytes for each signature,
  /// supporting both exact and fuzzy matching.
  /// </summary>
  /// <param name="data">Binary data to scan.</param>
  /// <param name="maxOffset">Maximum offset to search within (default 1024).</param>
  /// <param name="enableFuzzy">When true, also reports fuzzy (partial) matches (default true).</param>
  /// <param name="maxResults">Maximum number of results to return (default 50).</param>
  /// <returns>List of header matches sorted by confidence descending, then offset ascending.</returns>
  public static List<HeaderMatch> FindMatches(
    ReadOnlySpan<byte> data,
    int maxOffset = 1024,
    bool enableFuzzy = true,
    int maxResults = 50
  ) {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    if (data.Length == 0) return [];

    var scanLimit = Math.Min(data.Length, maxOffset);
    var matches = new List<HeaderMatch>();

    foreach (var descriptor in FormatRegistry.All) {
      foreach (var sig in descriptor.MagicSignatures) {
        if (sig.Bytes.Length < MinSignatureLength) continue;

        // For signatures with a declared offset, only check at that specific position
        // relative to each scan position
        ScanForSignature(data, scanLimit, descriptor, sig, enableFuzzy, matches);
      }
    }

    // Sort by confidence descending, then offset ascending
    matches.Sort((a, b) => {
      var c = b.Confidence.CompareTo(a.Confidence);
      return c != 0 ? c : a.Offset.CompareTo(b.Offset);
    });

    // Deduplicate: same format at same offset, prefer higher confidence
    var seen = new HashSet<(int, string)>();
    var unique = new List<HeaderMatch>();
    foreach (var m in matches) {
      if (seen.Add((m.Offset, m.FormatId)))
        unique.Add(m);
    }

    return unique.Count > maxResults ? unique[..maxResults] : unique;
  }

  /// <summary>
  /// Finds the best matching formats for just the beginning of the data (offset 0).
  /// This is a convenience method for quick format identification.
  /// </summary>
  /// <param name="data">Binary data to check.</param>
  /// <param name="enableFuzzy">When true, also reports fuzzy matches.</param>
  /// <returns>List of matches at offset 0, sorted by confidence descending.</returns>
  public static List<HeaderMatch> IdentifyFormat(ReadOnlySpan<byte> data, bool enableFuzzy = true) {
    var all = FindMatches(data, maxOffset: 1, enableFuzzy: enableFuzzy);
    return all.FindAll(m => m.Offset == 0);
  }

  // ── Private scanning logic ──────────────────────────────────────────

  private static void ScanForSignature(
    ReadOnlySpan<byte> data,
    int scanLimit,
    IFormatDescriptor descriptor,
    MagicSignature sig,
    bool enableFuzzy,
    List<HeaderMatch> matches
  ) {
    var magicLen = sig.Bytes.Length;

    // Scan each possible offset
    for (var offset = 0; offset < scanLimit; offset++) {
      // The magic is expected at (offset + sig.Offset)
      var magicPos = offset + sig.Offset;
      if (magicPos + magicLen > data.Length) continue;

      // Try exact match first
      if (IsExactMatch(data, magicPos, sig)) {
        matches.Add(new HeaderMatch(
          offset,
          descriptor.Id,
          descriptor.DisplayName,
          sig.Confidence,
          "Exact",
          magicLen,
          magicLen
        ));
        continue; // Don't also report fuzzy for same offset
      }

      // Try fuzzy match
      if (enableFuzzy) {
        var (matched, total) = ComputeFuzzyMatch(data, magicPos, sig);
        var ratio = (double)matched / total;

        if (ratio >= MinFuzzyMatchRatio && matched >= MinSignatureLength) {
          // Scale confidence by the match ratio
          var fuzzyConfidence = sig.Confidence * ratio * 0.7; // 0.7 penalty for being fuzzy
          matches.Add(new HeaderMatch(
            offset,
            descriptor.Id,
            descriptor.DisplayName,
            fuzzyConfidence,
            "Fuzzy",
            matched,
            total
          ));
        }
      }
    }
  }

  private static bool IsExactMatch(ReadOnlySpan<byte> data, int position, MagicSignature sig) {
    var magic = sig.Bytes;
    var mask = sig.Mask;

    for (var i = 0; i < magic.Length; i++) {
      var dataByte = data[position + i];
      var expected = magic[i];

      if (mask != null && i < mask.Length)
        dataByte &= mask[i];

      if (dataByte != expected)
        return false;
    }

    return true;
  }

  private static (int Matched, int Total) ComputeFuzzyMatch(ReadOnlySpan<byte> data, int position, MagicSignature sig) {
    var magic = sig.Bytes;
    var mask = sig.Mask;
    var matched = 0;

    for (var i = 0; i < magic.Length; i++) {
      var dataByte = data[position + i];
      var expected = magic[i];

      if (mask != null && i < mask.Length)
        dataByte &= mask[i];

      if (dataByte == expected)
        matched++;
    }

    return (matched, magic.Length);
  }
}
