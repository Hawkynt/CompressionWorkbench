#pragma warning disable CS1591

namespace Compression.Analysis.Structure;

/// <summary>
/// A repeated byte pattern found in binary data, with all offsets where it occurs.
/// </summary>
/// <param name="Pattern">The repeated byte sequence.</param>
/// <param name="Offsets">All offsets where this pattern was found.</param>
public sealed record RepeatedPattern(byte[] Pattern, List<int> Offsets);

/// <summary>
/// A candidate alignment boundary detected in the data.
/// </summary>
/// <param name="Alignment">The alignment size in bytes (e.g. 4, 8, 16, 512).</param>
/// <param name="Confidence">Confidence score (0.0-1.0) based on null-byte padding analysis.</param>
/// <param name="PaddedBoundaryCount">Number of boundaries that showed padding evidence.</param>
/// <param name="TotalBoundaryCount">Total number of boundaries tested.</param>
public sealed record AlignmentCandidate(int Alignment, double Confidence, int PaddedBoundaryCount, int TotalBoundaryCount);

/// <summary>
/// A candidate fixed-size record/field length detected via repeating structure analysis.
/// </summary>
/// <param name="Length">The candidate record length in bytes.</param>
/// <param name="Confidence">Confidence score (0.0-1.0) based on autocorrelation strength.</param>
/// <param name="MatchCount">Number of positions where the repeating structure was observed.</param>
public sealed record FieldLengthCandidate(int Length, double Confidence, int MatchCount);

/// <summary>
/// Discovers structural properties of unknown binary data: repeated byte patterns,
/// alignment boundaries, and fixed-size record lengths.
/// </summary>
public static class StructureDiscovery {

  /// <summary>
  /// Finds byte patterns that repeat at least <paramref name="minOccurrences"/> times in the data.
  /// Uses a rolling hash approach for efficiency. Searches patterns up to 64 bytes long.
  /// </summary>
  /// <param name="data">Binary data to analyze.</param>
  /// <param name="minPatternLength">Minimum pattern length in bytes (default 4).</param>
  /// <param name="minOccurrences">Minimum number of occurrences to report (default 3).</param>
  /// <param name="maxResults">Maximum number of patterns to return (default 100).</param>
  /// <returns>List of repeated patterns sorted by occurrence count descending.</returns>
  public static List<RepeatedPattern> FindRepeatedPatterns(
    ReadOnlySpan<byte> data,
    int minPatternLength = 4,
    int minOccurrences = 3,
    int maxResults = 100
  ) {
    if (minPatternLength < 1) minPatternLength = 1;
    if (minOccurrences < 2) minOccurrences = 2;
    if (data.Length < minPatternLength) return [];

    // Limit scan to first 256KB for performance
    var scanLen = Math.Min(data.Length, 256 * 1024);
    var maxPatternLength = Math.Min(64, scanLen / minOccurrences);

    if (maxPatternLength < minPatternLength) return [];

    // For each pattern length, collect all patterns and their offsets using a dictionary
    // keyed by a hash, then verify exact matches.
    var allPatterns = new List<RepeatedPattern>();

    for (var patLen = minPatternLength; patLen <= maxPatternLength; patLen++) {
      var patternMap = new Dictionary<long, List<int>>();

      for (var i = 0; i <= scanLen - patLen; i++) {
        var hash = ComputeHash(data, i, patLen);
        if (!patternMap.TryGetValue(hash, out var offsets)) {
          offsets = [i];
          patternMap[hash] = offsets;
        } else {
          offsets.Add(i);
        }
      }

      // Filter for patterns meeting minimum occurrences and verify exact matches
      foreach (var (_, offsets) in patternMap) {
        if (offsets.Count < minOccurrences) continue;

        // Group by exact byte content (hash collisions)
        var groups = GroupByExactMatch(data, offsets, patLen);
        foreach (var group in groups) {
          if (group.Count < minOccurrences) continue;

          // Skip patterns that are just repetitions of a single byte
          var pattern = data.Slice(group[0], patLen).ToArray();
          if (IsUniformPattern(pattern)) continue;

          allPatterns.Add(new RepeatedPattern(pattern, group));
        }
      }

      // Early exit if we have enough results at this length
      if (allPatterns.Count > maxResults * 10) break;
    }

    // Sort by occurrence count descending, then pattern length descending
    allPatterns.Sort((a, b) => {
      var c = b.Offsets.Count.CompareTo(a.Offsets.Count);
      return c != 0 ? c : b.Pattern.Length.CompareTo(a.Pattern.Length);
    });

    // Deduplicate: remove patterns that are subsets of longer patterns
    var result = DeduplicatePatterns(allPatterns, maxResults);

    return result.Count > maxResults ? result[..maxResults] : result;
  }

  /// <summary>
  /// Detects likely alignment boundaries by analyzing null-byte padding at regular intervals.
  /// Higher confidence means more evidence of padding at those alignment boundaries.
  /// </summary>
  /// <param name="data">Binary data to analyze.</param>
  /// <returns>List of alignment candidates sorted by confidence descending.</returns>
  public static List<AlignmentCandidate> FindAlignmentBoundaries(ReadOnlySpan<byte> data) {
    if (data.Length < 16) return [];

    var candidates = new List<AlignmentCandidate>();
    int[] alignments = [4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096];

    // Limit scan to first 256KB for performance
    var scanLen = Math.Min(data.Length, 256 * 1024);

    foreach (var alignment in alignments) {
      if (alignment >= scanLen) continue;

      var totalBoundaries = 0;
      var paddedBoundaries = 0;

      for (var offset = alignment; offset < scanLen; offset += alignment) {
        totalBoundaries++;

        // Check for evidence of padding: null bytes immediately before this boundary
        var paddingEvidence = 0;
        var checkLen = Math.Min(4, offset);
        for (var j = 1; j <= checkLen; j++) {
          if (data[offset - j] == 0x00)
            paddingEvidence++;
        }

        // Also check if the boundary itself starts a non-null sequence after nulls
        if (paddingEvidence >= 1 && offset < scanLen && data[offset] != 0x00)
          paddedBoundaries++;
        else if (paddingEvidence >= 2)
          paddedBoundaries++;
      }

      if (totalBoundaries == 0) continue;

      var ratio = (double)paddedBoundaries / totalBoundaries;

      // Require at least some evidence; scale confidence
      if (paddedBoundaries >= 2 && ratio > 0.05) {
        // Bonus for common alignment sizes
        var bonus = alignment is 4 or 8 or 16 or 512 or 4096 ? 0.05 : 0.0;
        var confidence = Math.Min(1.0, ratio + bonus);
        candidates.Add(new AlignmentCandidate(alignment, confidence, paddedBoundaries, totalBoundaries));
      }
    }

    candidates.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
    return candidates;
  }

  /// <summary>
  /// Heuristically identifies repeating structure lengths (e.g., fixed-size records)
  /// by computing autocorrelation of the byte sequence at various lag values.
  /// </summary>
  /// <param name="data">Binary data to analyze.</param>
  /// <param name="minLength">Minimum candidate record length (default 4).</param>
  /// <param name="maxLength">Maximum candidate record length (default 4096).</param>
  /// <returns>List of field length candidates sorted by confidence descending.</returns>
  public static List<FieldLengthCandidate> FindFieldLengthCandidates(
    ReadOnlySpan<byte> data,
    int minLength = 4,
    int maxLength = 4096
  ) {
    if (data.Length < minLength * 3) return [];

    // Limit scan to first 64KB for performance
    var scanLen = Math.Min(data.Length, 64 * 1024);
    var effectiveMax = Math.Min(maxLength, scanLen / 3);

    if (effectiveMax < minLength) return [];

    var candidates = new List<FieldLengthCandidate>();

    // Compute autocorrelation at each lag
    for (var lag = minLength; lag <= effectiveMax; lag++) {
      var matches = 0;
      var comparisons = 0;

      // Compare bytes at offset i with bytes at offset i+lag
      var limit = scanLen - lag;
      // Sample up to 4096 positions for large data
      var step = Math.Max(1, limit / 4096);

      for (var i = 0; i < limit; i += step) {
        comparisons++;
        if (data[i] == data[i + lag])
          matches++;
      }

      if (comparisons == 0) continue;

      var correlation = (double)matches / comparisons;

      // For random data, expected match rate is ~1/256 = 0.0039
      // Significant correlation indicates repeating structure
      const double randomBaseline = 1.0 / 256.0;
      if (correlation <= randomBaseline * 3) continue;

      // Normalize confidence: 0 at baseline, 1 at perfect correlation
      var confidence = Math.Min(1.0, (correlation - randomBaseline) / (1.0 - randomBaseline));

      // Require meaningful confidence
      if (confidence < 0.02) continue;

      candidates.Add(new FieldLengthCandidate(lag, confidence, matches));
    }

    // Sort by confidence descending
    candidates.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

    // Filter out harmonics: if lag N has high confidence, its multiples 2N, 3N etc.
    // are expected and less interesting; keep only if they have notably higher confidence.
    var filtered = FilterHarmonics(candidates);

    return filtered.Count > 50 ? filtered[..50] : filtered;
  }

  // ── Private helpers ──────────────────────────────────────────────────

  private static long ComputeHash(ReadOnlySpan<byte> data, int offset, int length) {
    // FNV-1a hash for the pattern
    var hash = unchecked((long)0xcbf29ce484222325);
    for (var i = 0; i < length; i++) {
      hash ^= data[offset + i];
      hash = unchecked(hash * 0x100000001b3);
    }
    return hash;
  }

  private static List<List<int>> GroupByExactMatch(ReadOnlySpan<byte> data, List<int> offsets, int patternLength) {
    var groups = new List<List<int>>();

    for (var i = 0; i < offsets.Count; i++) {
      var matched = false;
      for (var g = 0; g < groups.Count; g++) {
        if (data.Slice(offsets[i], patternLength).SequenceEqual(data.Slice(groups[g][0], patternLength))) {
          groups[g].Add(offsets[i]);
          matched = true;
          break;
        }
      }
      if (!matched)
        groups.Add([offsets[i]]);
    }

    return groups;
  }

  private static bool IsUniformPattern(byte[] pattern) {
    var first = pattern[0];
    for (var i = 1; i < pattern.Length; i++) {
      if (pattern[i] != first) return false;
    }
    return true;
  }

  private static List<RepeatedPattern> DeduplicatePatterns(List<RepeatedPattern> patterns, int maxResults) {
    var result = new List<RepeatedPattern>();
    var seen = new HashSet<int>(); // indices of patterns already covered

    for (var i = 0; i < patterns.Count && result.Count < maxResults; i++) {
      if (seen.Contains(i)) continue;

      result.Add(patterns[i]);

      // Mark shorter patterns that are substrings of this one
      for (var j = i + 1; j < patterns.Count; j++) {
        if (seen.Contains(j)) continue;
        if (patterns[j].Pattern.Length < patterns[i].Pattern.Length && IsSubPattern(patterns[i].Pattern, patterns[j].Pattern))
          seen.Add(j);
      }
    }

    return result;
  }

  private static bool IsSubPattern(byte[] longer, byte[] shorter) {
    for (var i = 0; i <= longer.Length - shorter.Length; i++) {
      var match = true;
      for (var j = 0; j < shorter.Length; j++) {
        if (longer[i + j] != shorter[j]) { match = false; break; }
      }
      if (match) return true;
    }
    return false;
  }

  private static List<FieldLengthCandidate> FilterHarmonics(List<FieldLengthCandidate> candidates) {
    if (candidates.Count == 0) return candidates;

    var kept = new List<FieldLengthCandidate>();
    var removed = new HashSet<int>();

    for (var i = 0; i < candidates.Count; i++) {
      if (removed.Contains(i)) continue;
      kept.Add(candidates[i]);

      // Remove multiples of this length that don't have notably higher confidence
      for (var j = i + 1; j < candidates.Count; j++) {
        if (removed.Contains(j)) continue;
        if (candidates[j].Length % candidates[i].Length == 0 &&
            candidates[j].Confidence <= candidates[i].Confidence * 1.1)
          removed.Add(j);
      }
    }

    return kept;
  }
}
