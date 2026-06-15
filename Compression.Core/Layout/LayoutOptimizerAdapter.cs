namespace Compression.Core.Layout;

/// <summary>
/// One-call front-end over <see cref="FilesystemLayoutOptimizer"/> for the common
/// case: a filesystem whose only tunable is a single power-of-two allocation unit
/// (cluster / block size) and whose cost is dominated by per-file tail slack plus a
/// per-size fixed structural overhead.
///
/// <para>A writer opts in by supplying only three things:</para>
/// <list type="number">
///   <item><description>the candidate sizes that are <b>legal for the format</b>
///   (the caller is responsible for keeping these inside the documented range — a
///   size outside the legal window can corrupt the image);</description></item>
///   <item><description>the file-set sizes that will be stored;</description></item>
///   <item><description>a per-candidate fixed-overhead function (allocation tables,
///   metadata zones, bitmaps, …) — return <c>null</c> for a candidate that violates
///   a format constraint so it is pruned.</description></item>
/// </list>
///
/// <para>The total cost for each candidate is <c>slack(fileSizes, size) + overhead(size)</c>;
/// the adapter returns the candidate with the global minimum, tie-breaking toward the
/// smaller size (the same semantics as <see cref="FilesystemLayoutOptimizer.SelectClusterSize"/>).
/// All filesystem-specific knowledge lives in the caller's overhead function;
/// the slack term is computed here so writers don't re-implement it by hand.</para>
///
/// <para>Typical usage — a writer with reader-agnostic clusters:</para>
/// <code>
/// var clusterBytes = LayoutOptimizerAdapter.SelectAllocationUnit(
///     candidateSizes: [512, 1024, 2048, 4096, 8192, 16384, 32768, 65536],
///     fileSizes:      files.Select(f => (long)f.Data.Length).ToList(),
///     fixedOverhead:  size => SuperblockBytes + FatBytesFor(size));
/// </code>
/// </summary>
public static class LayoutOptimizerAdapter {
  /// <summary>
  /// Picks the allocation-unit size (cluster / block, in bytes) from
  /// <paramref name="candidateSizes"/> that minimises total internal slack across
  /// <paramref name="fileSizes"/> plus the per-size <paramref name="fixedOverhead"/>.
  /// </summary>
  /// <param name="candidateSizes">
  /// The sizes to consider, all of which must be legal for the target format. Must be
  /// non-empty. The first entry is the safe fallback used when every candidate is
  /// pruned (overhead returns <c>null</c>).
  /// </param>
  /// <param name="fileSizes">
  /// The logical byte length of every file in the set. Zero-length and negative
  /// entries contribute no slack and no clusters.
  /// </param>
  /// <param name="fixedOverhead">
  /// Per-candidate structural overhead in bytes (tables, metadata zones, …). Return
  /// <c>null</c> to prune a candidate that violates a format constraint. When omitted
  /// (null), overhead is treated as zero and the choice is driven purely by slack.
  /// </param>
  /// <returns>The chosen allocation-unit size in bytes.</returns>
  public static int SelectAllocationUnit(
      IReadOnlyList<int> candidateSizes,
      IReadOnlyList<long> fileSizes,
      Func<int, long?>? fixedOverhead = null) {
    ArgumentNullException.ThrowIfNull(candidateSizes);
    ArgumentNullException.ThrowIfNull(fileSizes);
    if (candidateSizes.Count == 0)
      throw new ArgumentException("At least one candidate size is required.", nameof(candidateSizes));

    return FilesystemLayoutOptimizer.SelectClusterSize(
      candidateSizes,
      size => {
        var overhead = fixedOverhead?.Invoke(size);
        // A null overhead means "constraint violated" — propagate the prune.
        if (fixedOverhead != null && !overhead.HasValue) return null;
        var slack = FilesystemLayoutOptimizer.Slack(fileSizes, size);
        return slack + (overhead ?? 0L);
      });
  }

  /// <summary>
  /// Total internal slack (wasted last-cluster tail bytes) the supplied file-set
  /// would incur at <paramref name="unitSize"/>. Exposed so callers can report the
  /// before/after savings of a chosen unit without reaching into
  /// <see cref="FilesystemLayoutOptimizer"/> directly.
  /// </summary>
  public static long SlackAt(IReadOnlyList<long> fileSizes, int unitSize)
    => FilesystemLayoutOptimizer.Slack(fileSizes, unitSize);
}
