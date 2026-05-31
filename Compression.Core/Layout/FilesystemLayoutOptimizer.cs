namespace Compression.Core.Layout;

/// <summary>
/// Generic grid-search optimizer for cluster-based filesystem layouts.
/// Minimises the sum of internal slack (wasted bytes in the tail of each file's
/// last cluster) and structural overhead (allocation tables, metadata zones, etc.)
/// across all valid combinations of the supplied parameter candidates.
///
/// <para>The optimizer is filesystem-agnostic: the caller supplies a cost function
/// that encodes all filesystem-specific knowledge. The optimizer just finds the
/// global minimum over the candidate space, pruning invalid combinations (cost == null).</para>
///
/// <para>Typical usage — FAT cluster size:</para>
/// <code>
/// var best = FilesystemLayoutOptimizer.SelectClusterSize(
///     FilesystemLayoutOptimizer.StandardClusterSizes,
///     cb => {
///         var clusters = FilesystemLayoutOptimizer.DataClusters(fileSizes, cb);
///         var slack    = FilesystemLayoutOptimizer.Slack(fileSizes, cb);
///         var overhead = ComputeFatOverhead(clusters, cb);
///         return slack + overhead; // null = invalid (constraint violated)
///     });
/// </code>
///
/// <para>Typical usage — NTFS cluster + MFT record size:</para>
/// <code>
/// var (cluster, mftRecord) = FilesystemLayoutOptimizer.SelectPair(
///     FilesystemLayoutOptimizer.StandardClusterSizes,
///     new[] { 512, 1024, 4096 },
///     (cb, mftSz) => {
///         var clusters  = FilesystemLayoutOptimizer.DataClusters(fileSizes, cb);
///         var slack     = FilesystemLayoutOptimizer.Slack(fileSizes, cb);
///         var mftWaste  = ComputeMftWaste(fileSizes.Count, mftSz);
///         var mftZone   = (long)(clusters * cb * 0.125); // 12.5 % reservation
///         return slack + mftWaste + mftZone;
///     });
/// </code>
/// </summary>
public static class FilesystemLayoutOptimizer {
  /// <summary>
  /// Standard cluster sizes for FAT, exFAT, NTFS, and similar filesystems.
  /// All are powers of two, from 512 B to 64 KB.
  /// </summary>
  public static readonly IReadOnlyList<int> StandardClusterSizes =
    [512, 1024, 2048, 4096, 8192, 16384, 32768, 65536];

  // ── Single-parameter optimisation ──────────────────────────────────────

  /// <summary>
  /// Selects the value from <paramref name="candidates"/> that minimises the
  /// cost returned by <paramref name="costFn"/>. Returns null (invalid) costs
  /// are skipped. When all costs are null, the first candidate is returned as a
  /// safe fallback.
  /// </summary>
  public static int SelectClusterSize(
      IReadOnlyList<int> candidates,
      Func<int, long?> costFn) {
    var best = (value: candidates[0], cost: long.MaxValue);
    foreach (var c in candidates) {
      var cost = costFn(c);
      if (cost.HasValue && cost.Value < best.cost)
        best = (c, cost.Value);
    }
    return best.value;
  }

  // ── Two-parameter optimisation ──────────────────────────────────────────

  /// <summary>
  /// Exhaustive search over all pairs from <paramref name="candidates1"/> ×
  /// <paramref name="candidates2"/>. Finds the pair that minimises
  /// <paramref name="costFn"/>. Null costs are treated as constraint violations
  /// and skipped. Falls back to (candidates1[0], candidates2[0]) if all pairs
  /// are invalid.
  /// </summary>
  public static (int p1, int p2) SelectPair(
      IReadOnlyList<int> candidates1,
      IReadOnlyList<int> candidates2,
      Func<int, int, long?> costFn) {
    var best = (p1: candidates1[0], p2: candidates2[0], cost: long.MaxValue);
    foreach (var p1 in candidates1)
      foreach (var p2 in candidates2) {
        var cost = costFn(p1, p2);
        if (cost.HasValue && cost.Value < best.cost)
          best = (p1, p2, cost.Value);
      }
    return (best.p1, best.p2);
  }

  // ── Shared building-block computations ─────────────────────────────────

  /// <summary>
  /// Number of clusters required to store all files. Zero-length files occupy
  /// no clusters. Positive file sizes are rounded up to the nearest cluster.
  /// </summary>
  public static long DataClusters(IReadOnlyList<long> fileSizes, long clusterBytes)
    => fileSizes.Sum(s => s <= 0 ? 0L : (s + clusterBytes - 1) / clusterBytes);

  /// <summary>
  /// Total internal slack: bytes wasted in the unfilled tail of the last cluster
  /// of every non-empty file. Zero-length files contribute zero slack.
  /// </summary>
  public static long Slack(IReadOnlyList<long> fileSizes, long clusterBytes)
    => fileSizes.Sum(s => s <= 0 ? 0L : (clusterBytes - s % clusterBytes) % clusterBytes);

  // ── Tier-aware single-parameter optimisation ────────────────────────────

  /// <summary>
  /// Like <see cref="SelectClusterSize"/> but groups candidates by the "tier"
  /// returned by <paramref name="tierFn"/> (e.g. FAT type: 12, 16, 32) and
  /// optimises within the <em>lowest</em> tier first. Only escalates to a higher
  /// tier when no candidate in the lower tier is valid.
  ///
  /// <para>This prevents a large-cluster option from appearing cheaper (less slack)
  /// while silently pushing the image into a costlier filesystem variant (FAT16 →
  /// FAT32, or NTFS with a larger MFT zone), which would add more structural
  /// overhead than the slack savings justify.</para>
  /// </summary>
  public static int SelectClusterSizeTiered(
      IReadOnlyList<int> candidates,
      Func<int, int?> tierFn,
      Func<int, long?> costFn) {
    // Collect valid (tier, cost, value) tuples.
    var valid = new List<(int tier, long cost, int value)>();
    foreach (var c in candidates) {
      var tier = tierFn(c);
      var cost = costFn(c);
      if (tier.HasValue && cost.HasValue)
        valid.Add((tier.Value, cost.Value, c));
    }
    if (valid.Count == 0) return candidates[0]; // fallback

    // Within the lowest tier, pick minimum cost.
    var lowestTier = valid.Min(e => e.tier);
    return valid
      .Where(e => e.tier == lowestTier)
      .OrderBy(e => e.cost)
      .First().value;
  }
}
