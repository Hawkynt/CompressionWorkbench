#pragma warning disable CS1591
namespace FileSystem.Nilfs1;

/// <summary>
/// Picks NILFS v1 superblock parameters for a given fileset. Returns the
/// smallest BlockSize whose total slack waste is ≤ 5%, tiebreaking toward
/// bigger blocks (less FAT-style overhead in the segment). SegmentSize is
/// always 8 × BlockSize because our minimal writer emits one full segment.
/// </summary>
public static class Nilfs1Optimizer {

  /// <summary>
  /// Represents a nilfs 1 geometry.
  /// </summary>
public sealed record Nilfs1Geometry(int BlockSize, int SegmentSize);

  // NILFS v1 supports 1024 .. 65536 byte blocks (power of two).
  private static readonly int[] BlockSizes = [1024, 2048, 4096, 8192, 16384, 32768, 65536];

  /// <summary>
  /// Performs the find operation.
  /// </summary>
public static Nilfs1Geometry Find(System.Collections.Generic.IReadOnlyList<long> fileSizes) {
    System.ArgumentNullException.ThrowIfNull(fileSizes);
    var totalBytes = 0L;
    foreach (var s in fileSizes) totalBytes += System.Math.Max(0, s);

    var candidates = new System.Collections.Generic.List<Nilfs1Geometry>();
    foreach (var block in BlockSizes) {
      var slack = 0L;
      foreach (var s in fileSizes) {
        if (s <= 0) continue;
        var rounded = ((s + block - 1L) / block) * block;
        slack += rounded - s;
      }
      var slackRatio = totalBytes > 0 ? (double)slack / totalBytes : 0.0;
      if (slackRatio <= 0.05 || candidates.Count == 0)
        candidates.Add(new Nilfs1Geometry(block, block * 8));
    }

    if (candidates.Count == 0)
      return new Nilfs1Geometry(4096, 4096 * 8);

    // Tiebreak toward bigger block.
    candidates.Sort((a, b) => b.BlockSize.CompareTo(a.BlockSize));
    return candidates[0];
  }
}
