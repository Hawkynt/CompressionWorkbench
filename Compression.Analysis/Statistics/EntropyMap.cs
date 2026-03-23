namespace Compression.Analysis.Statistics;

/// <summary>
/// Computes a per-region entropy profile over binary data using adaptive windowing.
/// Adjacent regions with the same classification are merged into larger blocks.
/// </summary>
public static class EntropyMap {

  /// <summary>
  /// Profiles the data, optionally using boundary detection for data &gt; 4KB.
  /// </summary>
  /// <param name="data">The binary data to profile.</param>
  /// <param name="windowSize">Base probe window size in bytes.</param>
  /// <param name="useBoundaryDetection">When true and data &gt; 4KB, uses CUSUM boundary detection.</param>
  /// <param name="cancellationToken">Optional cancellation token.</param>
  /// <returns>Region profiles covering the entire data.</returns>
  public static List<RegionProfile> Profile(ReadOnlySpan<byte> data, int windowSize,
      bool useBoundaryDetection, CancellationToken cancellationToken = default) {
    if (useBoundaryDetection && data.Length >= 4096)
      return BoundaryDetector.DetectBoundaries(data, Math.Max(windowSize, 256), cancellationToken: cancellationToken);
    return Profile(data, windowSize);
  }

  /// <summary>
  /// Profiles the data using an adaptive strategy: starts with small probe windows,
  /// then merges adjacent regions with the same classification, and re-analyzes
  /// the merged blocks for accurate statistics.
  /// </summary>
  /// <param name="data">The binary data to profile.</param>
  /// <param name="windowSize">Base probe window size in bytes (default 256).</param>
  /// <returns>Merged region profiles covering the entire data.</returns>
  public static List<RegionProfile> Profile(ReadOnlySpan<byte> data, int windowSize = 256) {
    if (data.Length == 0) return [];
    if (windowSize < 16) windowSize = 16;

    // Phase 1: Probe with base window size
    var probes = ProbeRegions(data, windowSize);

    // Phase 2: Merge adjacent regions with the same classification
    var merged = MergeAdjacent(probes);

    // Phase 3: Re-compute accurate statistics for merged regions
    var results = new List<RegionProfile>(merged.Count);
    foreach (var m in merged) {
      var slice = data.Slice((int)m.Offset, m.Length);
      var freq = BinaryStatistics.ComputeByteFrequency(slice);
      var entropy = BinaryStatistics.ComputeEntropy(freq, m.Length);
      var chiSq = BinaryStatistics.ComputeChiSquare(freq, m.Length);
      var mean = BinaryStatistics.ComputeMean(slice);
      var classification = Classify(entropy);
      results.Add(new RegionProfile(m.Offset, m.Length, entropy, chiSq, mean, classification));
    }

    // Phase 4: Split large regions that span classification boundaries
    // (a merged "constant" block might have a transition inside it)
    return RefineTransitions(data, results, windowSize);
  }

  private static List<RegionProfile> ProbeRegions(ReadOnlySpan<byte> data, int windowSize) {
    var results = new List<RegionProfile>();
    for (var offset = 0; offset < data.Length; offset += windowSize) {
      var len = Math.Min(windowSize, data.Length - offset);
      var window = data.Slice(offset, len);
      var freq = BinaryStatistics.ComputeByteFrequency(window);
      var entropy = BinaryStatistics.ComputeEntropy(freq, len);
      var chiSq = BinaryStatistics.ComputeChiSquare(freq, len);
      var mean = BinaryStatistics.ComputeMean(window);
      var classification = Classify(entropy);
      results.Add(new RegionProfile(offset, len, entropy, chiSq, mean, classification));
    }
    return results;
  }

  private static List<RegionProfile> MergeAdjacent(List<RegionProfile> regions) {
    if (regions.Count == 0) return [];
    var merged = new List<RegionProfile> { regions[0] };

    for (var i = 1; i < regions.Count; i++) {
      var prev = merged[^1];
      var curr = regions[i];

      if (prev.Classification == curr.Classification) {
        // Merge: extend previous region (stats will be recomputed in Phase 3)
        merged[^1] = prev with { Length = (int)(curr.Offset - prev.Offset) + curr.Length };
      }
      else {
        merged.Add(curr);
      }
    }

    return merged;
  }

  private static List<RegionProfile> RefineTransitions(
      ReadOnlySpan<byte> data, List<RegionProfile> regions, int baseWindow) {
    // Only refine regions larger than 4x the base window that might hide transitions
    var refined = new List<RegionProfile>();
    var minRefineSize = baseWindow * 4;

    foreach (var r in regions) {
      if (r.Length <= minRefineSize) {
        refined.Add(r);
        continue;
      }

      // Re-probe this large region at half the base window to check for transitions
      var probeSize = Math.Max(baseWindow / 2, 64);
      var subProbes = new List<RegionProfile>();
      var regionEnd = (int)r.Offset + r.Length;

      for (var off = (int)r.Offset; off < regionEnd; off += probeSize) {
        var len = Math.Min(probeSize, regionEnd - off);
        var window = data.Slice(off, len);
        var freq = BinaryStatistics.ComputeByteFrequency(window);
        var entropy = BinaryStatistics.ComputeEntropy(freq, len);
        var chiSq = BinaryStatistics.ComputeChiSquare(freq, len);
        var mean = BinaryStatistics.ComputeMean(window);
        var classification = Classify(entropy);
        subProbes.Add(new RegionProfile(off, len, entropy, chiSq, mean, classification));
      }

      // Merge the sub-probes and recompute
      var subMerged = MergeAdjacent(subProbes);
      foreach (var sm in subMerged) {
        var slice = data.Slice((int)sm.Offset, sm.Length);
        var freq = BinaryStatistics.ComputeByteFrequency(slice);
        var entropy = BinaryStatistics.ComputeEntropy(freq, sm.Length);
        var chiSq = BinaryStatistics.ComputeChiSquare(freq, sm.Length);
        var mean = BinaryStatistics.ComputeMean(slice);
        var classification = Classify(entropy);
        refined.Add(new RegionProfile(sm.Offset, sm.Length, entropy, chiSq, mean, classification));
      }
    }

    return refined;
  }

  /// <summary>Classifies data by entropy level.</summary>
  public static string Classify(double entropy) => entropy switch {
    < 1.0 => "Constant/empty",
    < 4.0 => "Plaintext/structured",
    < 6.0 => "Structured data",
    < 7.5 => "Dictionary-compressed",
    < 7.95 => "Strong compression",
    _ => "Encrypted/random",
  };
}
