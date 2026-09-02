#pragma warning disable CS1591
namespace FileSystem.Jfs1;

/// <summary>Optimal JFS1 layout as picked by <see cref="Jfs1Optimizer.Find"/>.</summary>
public sealed record Jfs1OptimalParameters(
  int BlockSize,
  int AggregateBlockSize,
  long EstimatedImageBytes);

/// <summary>
/// Fileset-driven optimizer for OS/2 JFS1. Picks the smallest block size
/// where total wasted slack is ≤ 5% AND the estimated image size is ≤ 2× the
/// sum of file sizes. Tiebreaks toward larger blocks (fewer dinodes per
/// content byte, smaller dmap chain).
/// </summary>
public static class Jfs1Optimizer {
  private static readonly int[] CandidateBlockSizes = [1024, 2048, 4096];
  private const double MaxSlackRatio = 0.05;
  private const double MaxBloatFactor = 2.0;

    /// <summary>
  /// Performs the find operation.
  /// </summary>
public static Jfs1OptimalParameters Find(IReadOnlyList<long> fileSizes) {
    ArgumentNullException.ThrowIfNull(fileSizes);
    if (fileSizes.Count == 0)
      return new Jfs1OptimalParameters(1024, AggregateBlockSize: 1024, EstimatedImageBytes: 3 * 1024);

    var totalData = fileSizes.Sum(s => Math.Max(0, s));

    int? best = null;
    long bestEstimate = long.MaxValue;
    foreach (var bs in CandidateBlockSizes) {
      var totalAllocated = 0L;
      foreach (var s in fileSizes) {
        var blocks = (Math.Max(0, s) + bs - 1) / bs;
        totalAllocated += blocks * bs;
      }
      var slack = totalAllocated - totalData;
      var slackRatio = totalData > 0 ? (double)slack / totalData : 0;
      var estimated = totalAllocated + 3L * bs; // + sb + inode block + root dir
      var withinSlack = slackRatio <= MaxSlackRatio;
      var withinBloat = totalData == 0 || estimated <= totalData * MaxBloatFactor;
      if (withinSlack && withinBloat) {
        // Tiebreak: prefer LARGER blocks (less metadata overhead).
        if (best == null || bs > best) {
          best = bs;
          bestEstimate = estimated;
        }
      }
    }

    if (best == null) {
      best = CandidateBlockSizes[0];
      bestEstimate = 0;
      foreach (var s in fileSizes)
        bestEstimate += ((s + best.Value - 1) / best.Value) * best.Value;
      bestEstimate += 3L * best.Value;
    }

    return new Jfs1OptimalParameters(best.Value, best.Value, bestEstimate);
  }
}
