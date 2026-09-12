#pragma warning disable CS1591
namespace FileSystem.Gfs1;

/// <summary>Optimal GFS1 layout as picked by <see cref="Gfs1Optimizer.Find"/>.</summary>
public sealed record Gfs1OptimalParameters(
  int BlockSize,
  int JournalCount,
  long EstimatedImageBytes);

/// <summary>
/// Fileset-driven optimizer for GFS1. The block size is fixed at 4096 (per
/// Sistina GFS spec — all metadata I/O is page-sized). The optimizer trades
/// journal count: bigger filesets → more journals (each journal is
/// ~128 MB so this materially impacts image size); empty filesets get a
/// single journal.
/// </summary>
public static class Gfs1Optimizer {
  private const int BlockSize = Gfs1Writer.BlockSize;
  private const double MaxSlackRatio = 0.05;
  private const double MaxBloatFactor = 2.0;

  /// <summary>
  /// Performs the find operation.
  /// </summary>
  public static Gfs1OptimalParameters Find(IReadOnlyList<long> fileSizes) {
    ArgumentNullException.ThrowIfNull(fileSizes);
    if (fileSizes.Count == 0)
      return new Gfs1OptimalParameters(BlockSize, JournalCount: 1, EstimatedImageBytes: 17 * BlockSize);

    var totalData = fileSizes.Sum(s => Math.Max(0, s));
    var totalAllocated = 0L;
    foreach (var s in fileSizes) {
      var blocks = (Math.Max(0, s) + BlockSize - 1) / BlockSize;
      totalAllocated += blocks * BlockSize;
    }
    var slack = totalAllocated - totalData;
    var slackRatio = totalData > 0 ? (double)slack / totalData : 0;

    // Journal sizing: 1 per node, but with WORM we can't predict node count.
    // Use a workload-size heuristic — bigger filesets benefit from 2-4 journals
    // (multi-node deployments) while small ones stay at 1.
    var journals = totalData switch {
      < 1L * 1024 * 1024 => 1,
      < 64L * 1024 * 1024 => 2,
      < 1024L * 1024 * 1024 => 4,
      _ => 8,
    };
    var estimated = totalAllocated + 17 * BlockSize; // + sb + boot + inode table

    if (slackRatio > MaxSlackRatio || (totalData > 0 && estimated > totalData * MaxBloatFactor))
      journals = Math.Max(1, journals - 1);

    return new Gfs1OptimalParameters(BlockSize, journals, estimated);
  }
}
