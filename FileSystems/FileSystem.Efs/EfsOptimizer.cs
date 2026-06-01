#pragma warning disable CS1591
namespace FileSystem.Efs;

/// <summary>
/// Optimal EFS layout parameters as picked by <see cref="EfsOptimizer.Find"/>.
/// </summary>
/// <param name="BlockSize">Selected basic-block size in bytes (always 512 for EFS).</param>
/// <param name="CylinderGroupSize">Cylinder-group size in basic blocks.</param>
/// <param name="EstimatedImageBytes">Predicted on-disk image size for the input set.</param>
public sealed record EfsOptimalParameters(
  int BlockSize,
  int CylinderGroupSize,
  long EstimatedImageBytes);

/// <summary>
/// Fileset-driven optimizer for EFS. The basic-block size is fixed at 512 (per
/// the IRIX spec), so the optimizer's only real lever is the cylinder-group
/// size — bigger groups for big filesets (fewer groups, less metadata), smaller
/// groups for tiny filesets (better locality). The image-size sanity guard
/// (≤ 2× sum of files) and the slack guard (≤ 5%) are applied at the
/// blocks-per-file level since EFS' allocation unit is one basic block.
/// </summary>
public static class EfsOptimizer {
  private const int BlockSize = EfsWriter.BasicBlock; // 512
  private const double MaxSlackRatio = 0.05;
  private const double MaxBloatFactor = 2.0;

  /// <summary>
  /// Picks an EFS layout for <paramref name="fileSizes"/>. Empty input collapses
  /// to the minimum image (one inode block + one directory block).
  /// </summary>
  public static EfsOptimalParameters Find(IReadOnlyList<long> fileSizes) {
    ArgumentNullException.ThrowIfNull(fileSizes);
    if (fileSizes.Count == 0)
      return new EfsOptimalParameters(BlockSize, CylinderGroupSize: 8, EstimatedImageBytes: 2 * BlockSize);

    // Compute total data BBs and total slack at the fixed 512 B block size.
    var totalData = 0L;
    var totalAllocated = 0L;
    foreach (var size in fileSizes) {
      var s = Math.Max(0, size);
      totalData += s;
      var blocks = (s + BlockSize - 1) / BlockSize;
      totalAllocated += blocks * BlockSize;
    }
    var slack = totalAllocated - totalData;
    var slackRatio = totalData > 0 ? (double)slack / totalData : 0;

    // Tiebreak toward larger groups (fewer pointers); only fall back to
    // smaller groups if the slack ratio blows the 5% budget AND going smaller
    // helps (which on EFS it never does, because the block size is fixed at
    // 512 B). The cylinder-group size therefore tracks the workload: big files
    // → big groups, small files → small groups.
    var avg = totalData / fileSizes.Count;
    var groupSize = avg switch {
      < 4 * 1024 => 8,
      < 64 * 1024 => 32,
      < 1024 * 1024 => 128,
      _ => 512,
    };

    // Sanity: estimated image must not exceed 2× total file bytes.
    var estimated = totalAllocated + (fileSizes.Count + 4) * BlockSize; // + metadata reserve
    if (slackRatio > MaxSlackRatio || (totalData > 0 && estimated > totalData * MaxBloatFactor))
      groupSize = Math.Max(8, groupSize / 2);

    return new EfsOptimalParameters(BlockSize, groupSize, estimated);
  }
}
