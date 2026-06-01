#pragma warning disable CS1591
namespace FileSystem.Htfs;

/// <summary>Optimal HTFS layout as picked by <see cref="HtfsOptimizer.Find"/>.</summary>
public sealed record HtfsOptimalParameters(
  int BlockSize,
  int InodeCount,
  long EstimatedImageBytes);

/// <summary>
/// Fileset-driven optimizer for HTFS. Picks the smallest 512/1024/2048 block
/// size where total wasted slack is ≤ 5% AND the estimated image size is ≤
/// 2× the sum of file sizes. Tiebreaks toward larger blocks (fewer inodes,
/// less indirect-pointer overhead).
/// </summary>
public static class HtfsOptimizer {
  private static readonly int[] CandidateBlockSizes = [512, 1024, 2048];
  private const double MaxSlackRatio = 0.05;
  private const double MaxBloatFactor = 2.0;

  public static HtfsOptimalParameters Find(IReadOnlyList<long> fileSizes) {
    ArgumentNullException.ThrowIfNull(fileSizes);
    if (fileSizes.Count == 0)
      return new HtfsOptimalParameters(512, InodeCount: 16, EstimatedImageBytes: 4 * 512);

    var totalData = fileSizes.Sum(s => Math.Max(0, s));
    var inodeCount = Math.Max(16, fileSizes.Count + 8);

    // Try each candidate block size; pick the smallest acceptable, otherwise
    // tiebreak toward the largest (less overhead).
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
      var inodeBytes = (long)inodeCount * HtfsWriter.InodeSize;
      var estimated = totalAllocated + inodeBytes + 2 * bs; // + sb + reserved
      var withinSlack = slackRatio <= MaxSlackRatio;
      var withinBloat = totalData == 0 || estimated <= totalData * MaxBloatFactor;
      if (withinSlack && withinBloat) {
        // Tiebreak: prefer LARGER blocks once both budgets are met.
        if (best == null || bs > best) {
          best = bs;
          bestEstimate = estimated;
        }
      }
    }

    // No candidate met the budget: pick the smallest block to minimise slack.
    if (best == null) {
      best = CandidateBlockSizes[0];
      foreach (var s in fileSizes)
        bestEstimate += ((s + best.Value - 1) / best.Value) * best.Value;
      bestEstimate += (long)inodeCount * HtfsWriter.InodeSize + 2 * best.Value;
    }

    return new HtfsOptimalParameters(best.Value, inodeCount, bestEstimate);
  }
}
