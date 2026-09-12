#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Refs;

/// <summary>
/// ReFS-specific interleave planner.  Unlike the legacy generic interleave
/// sweep it treats every MetadataReserved/Bad cluster as a hard barrier and
/// bounds each owner's resulting extent count to what its B+ leaf can hold.
/// </summary>
internal static class RefsInterleavePlanner {
  private readonly record struct ClusterRange(long Start, long End);

  public static IReadOnlyList<ClusterMove> Plan(
      IReadOnlyList<DefragBlockInfo> extents,
      RefsMetadataReader metadata,
      long imageSize,
      DefragMode mode,
      int stride,
      int maxRunsPerFile) {
    if (stride < 2 || stride > 256) throw new ArgumentOutOfRangeException(nameof(stride));
    if (maxRunsPerFile < 1) throw new ArgumentOutOfRangeException(nameof(maxRunsPerFile));
    if (mode is not (DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd))
      throw new NotSupportedException("ReFS interleave supports consolidate-at-start/end layouts.");

    var clusterSize = metadata.ClusterSize;
    var byFile = new Dictionary<string, List<DefragBlockInfo>>(StringComparer.OrdinalIgnoreCase);
    foreach (var extent in extents)
      if (extent.Kind == DefragBlockKind.Used && extent.FileName is { Length: > 0 } name) {
        if (!byFile.TryGetValue(name, out var list)) byFile[name] = list = [];
        list.Add(extent);
      }
    if (byFile.Count == 0) return [];

    var sources = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
    long totalBlocks = 0;
    foreach (var (name, runs) in byFile) {
      var blocks = new List<long>();
      foreach (var run in runs) {
        if (run.Offset % clusterSize != 0 || run.Length % clusterSize != 0)
          throw new InvalidDataException($"ReFS extent '{name}' is not cluster aligned.");
        for (long p = 0; p < run.Length; p += clusterSize)
          blocks.Add(run.Offset + p);
      }
      sources[name] = blocks.ToArray();
      totalBlocks = checked(totalBlocks + blocks.Count);
    }

    var allowed = BuildAllowedRanges(extents, imageSize, clusterSize);
    var targetPool = TakeTargetBlocks(allowed, totalBlocks, clusterSize, mode == DefragMode.ConsolidateAtEnd);
    if (targetPool.Count != totalBlocks)
      throw new InvalidOperationException("ReFS interleave has insufficient non-metadata clusters for the live file allocation.");

    var ordered = sources.Keys.OrderBy(n => sources[n].Length == 0 ? long.MaxValue : sources[n][0]).ToArray();
    var targets = ordered.ToDictionary(n => n, _ => new List<long>(), StringComparer.OrdinalIgnoreCase);
    var poolIndex = 0;

    for (var batchStart = 0; batchStart < ordered.Length; batchStart += stride) {
      var batch = ordered.Skip(batchStart).Take(stride).ToArray();
      var consumed = new int[batch.Length];
      var stripe = new int[batch.Length];
      for (var i = 0; i < batch.Length; ++i) {
        var blockCount = sources[batch[i]].Length;
        stripe[i] = Math.Max(1, (blockCount + maxRunsPerFile - 1) / maxRunsPerFile);
      }

      while (true) {
        var progress = false;
        for (var lane = 0; lane < batch.Length; ++lane) {
          var name = batch[lane];
          var remaining = sources[name].Length - consumed[lane];
          if (remaining <= 0) continue;
          progress = true;
          var take = Math.Min(stripe[lane], remaining);
          for (var i = 0; i < take; ++i)
            targets[name].Add(targetPool[poolIndex++]);
          consumed[lane] += take;
        }
        if (!progress) break;
      }
    }

    if (poolIndex != targetPool.Count)
      throw new InvalidOperationException("ReFS interleave target accounting is inconsistent.");

    var rawMoves = new List<ClusterMove>();
    foreach (var name in ordered) {
      var src = sources[name];
      var dst = targets[name].ToArray();
      var runs = RefsStreamLayoutEditor.BuildExtents(metadata, dst);
      if (runs.Count > maxRunsPerFile)
        throw new InvalidOperationException(
          $"ReFS interleave for '{name}' produces {runs.Count} virtual extents but its metadata row can hold {maxRunsPerFile}. " +
          "Reduce the stride or use contiguous defragmentation.");

      for (var i = 0; i < src.Length;) {
        if (src[i] == dst[i]) { ++i; continue; }
        var count = 1;
        while (i + count < src.Length
               && src[i + count] == src[i] + (long)count * clusterSize
               && dst[i + count] == dst[i] + (long)count * clusterSize)
          ++count;
        rawMoves.Add(new ClusterMove(src[i], dst[i], checked((long)count * clusterSize), name));
        i += count;
      }
    }

    return ResolveDependencies(rawMoves);
  }

  private static List<long> TakeTargetBlocks(
      IReadOnlyList<ClusterRange> allowed,
      long count,
      int clusterSize,
      bool fromEnd) {
    if (count > int.MaxValue)
      throw new InvalidOperationException("ReFS interleave currently exceeds the planner's in-memory block-index limit.");
    var result = new List<long>(checked((int)count));
    if (!fromEnd) {
      foreach (var range in allowed) {
        for (var c = range.Start; c < range.End && result.Count < count; ++c)
          result.Add(checked(c * (long)clusterSize));
        if (result.Count == count) break;
      }
      return result;
    }

    for (var r = allowed.Count - 1; r >= 0 && result.Count < count; --r)
      for (var c = allowed[r].End - 1; c >= allowed[r].Start && result.Count < count; --c)
        result.Add(checked(c * (long)clusterSize));
    result.Reverse();
    return result;
  }

  private static List<ClusterRange> BuildAllowedRanges(
      IReadOnlyList<DefragBlockInfo> extents,
      long imageSize,
      int clusterSize) {
    var totalClusters = imageSize / clusterSize;
    var forbidden = new List<ClusterRange>();
    foreach (var extent in extents) {
      if (extent.Kind is not (DefragBlockKind.MetadataReserved or DefragBlockKind.Bad) || extent.Length <= 0) continue;
      var start = Math.Max(0, extent.Offset / clusterSize);
      var end = Math.Min(totalClusters, (extent.Offset + extent.Length + clusterSize - 1) / clusterSize);
      if (end > start) forbidden.Add(new ClusterRange(start, end));
    }
    forbidden.Sort((a, b) => a.Start.CompareTo(b.Start));
    var merged = new List<ClusterRange>();
    foreach (var range in forbidden) {
      if (merged.Count == 0 || range.Start > merged[^1].End) merged.Add(range);
      else if (range.End > merged[^1].End) merged[^1] = new ClusterRange(merged[^1].Start, range.End);
    }

    var allowed = new List<ClusterRange>();
    long cursor = 0;
    foreach (var range in merged) {
      if (range.Start > cursor) allowed.Add(new ClusterRange(cursor, range.Start));
      cursor = Math.Max(cursor, range.End);
    }
    if (cursor < totalClusters) allowed.Add(new ClusterRange(cursor, totalClusters));
    return allowed;
  }

  /// <summary>
  /// Topological move ordering with memory/scratch staging for cycles.  ReFS
  /// advertises held-run support, so no on-volume temporary extent is required.
  /// </summary>
  private static IReadOnlyList<ClusterMove> ResolveDependencies(List<ClusterMove> rawMoves) {
    rawMoves.RemoveAll(m => m.SrcOffset == m.DstOffset || m.Length <= 0);
    if (rawMoves.Count == 0) return [];
    var result = new List<ClusterMove>(rawMoves.Count + rawMoves.Count / 8);
    var pending = new List<ClusterMove>(rawMoves);
    var stagingSlot = 0;
    var safety = rawMoves.Count * 3 + 16;

    while (pending.Count > 0 && safety-- > 0) {
      var progress = false;
      for (var i = pending.Count - 1; i >= 0; --i) {
        var move = pending[i];
        var blocked = false;
        for (var j = 0; j < pending.Count; ++j) {
          if (i == j || pending[j].Staging == DefragStaging.Unpark) continue;
          if (Overlaps(move.DstOffset, move.Length, pending[j].SrcOffset, pending[j].Length)) {
            blocked = true;
            break;
          }
        }
        if (blocked) continue;
        result.Add(move);
        pending.RemoveAt(i);
        progress = true;
      }
      if (progress) continue;

      var blockedMove = pending[0];
      var occupant = -1;
      for (var i = 0; i < pending.Count; ++i) {
        if (pending[i].Staging == DefragStaging.Unpark) continue;
        if (Overlaps(blockedMove.DstOffset, blockedMove.Length, pending[i].SrcOffset, pending[i].Length)) {
          occupant = i;
          break;
        }
      }
      if (occupant < 0)
        occupant = pending.FindIndex(m => m.Staging != DefragStaging.Unpark);
      if (occupant < 0)
        throw new InvalidOperationException("ReFS interleave dependency cycle contains only already-held runs.");

      var stuck = pending[occupant];
      result.Add(new ClusterMove(stuck.SrcOffset, stuck.SrcOffset, stuck.Length, stuck.FileName) {
        Staging = DefragStaging.Park,
        StagingSlot = stagingSlot,
      });
      pending[occupant] = stuck with { Staging = DefragStaging.Unpark, StagingSlot = stagingSlot };
      ++stagingSlot;
    }

    if (pending.Count != 0)
      throw new InvalidOperationException("ReFS interleave dependency resolver did not converge.");
    return result;
  }

  private static bool Overlaps(long aOffset, long aLength, long bOffset, long bLength)
    => aOffset < bOffset + bLength && bOffset < aOffset + aLength;
}
