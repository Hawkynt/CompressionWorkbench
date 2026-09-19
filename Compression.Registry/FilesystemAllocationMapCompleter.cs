namespace Compression.Registry;

/// <summary>
/// Completes a decoded filesystem layout so that every byte of the image is
/// accounted for, using the filesystem's authoritative free-space structure.
/// </summary>
/// <remarks>
/// <para>
/// The decoded extents are the walker's own answer and are passed through
/// verbatim — same offsets, same lengths, same names, same kinds. They may
/// legitimately overlap: a map that describes an inode chunk and the individual
/// inodes inside it is describing two real things at two granularities, and a
/// file run that stops at its last valid byte rather than at the end of its
/// allocation unit is what makes cluster-tip wiping possible. Rewriting either
/// shape would destroy information the maintenance consumers depend on.
/// </para>
/// <para>
/// Completion is therefore additive. Bytes no decoded extent covers are
/// classified from the free-space structure: a byte the allocator positively
/// proves free becomes <see cref="DefragBlockKind.Free"/>, and every remaining
/// byte becomes <see cref="DefragBlockKind.MetadataReserved"/>. That is the
/// fail-closed rule <see cref="IFilesystemExtentMap"/> states: allocated but
/// undecoded bytes — external xattr blocks, extent-tree and indirect blocks,
/// journals, quota and orphan metadata, and metadata a future revision adds —
/// stay reserved instead of silently reading as free space.
/// </para>
/// <para>
/// A proven-free range is trimmed against the decoded extents before it is
/// emitted, so an emitted <see cref="DefragBlockKind.Free"/> run can never
/// overlap a byte something else claims. The generic wiper zeroes exactly the
/// <see cref="DefragBlockKind.Free"/> runs it is given, so that trimming is what
/// keeps a disagreement between the allocator and the walker from becoming a
/// destructive write.
/// </para>
/// </remarks>
public static class FilesystemAllocationMapCompleter {

  /// <summary>
  /// Produces a complete map covering <c>[0, imageLength)</c> from the decoded
  /// extents and the ranges the allocator proves free.
  /// </summary>
  /// <param name="imageLength">Physical length of the image in bytes.</param>
  /// <param name="decoded">Extents the filesystem walker decoded. Emitted verbatim.</param>
  /// <param name="provenFree">Ranges the on-disk allocator positively proves free.</param>
  /// <returns>The decoded extents followed by the classification of every byte they left uncovered.</returns>
  public static IReadOnlyList<DefragBlockInfo> Complete(
      long imageLength,
      IEnumerable<DefragBlockInfo> decoded,
      IEnumerable<(long Offset, long Length)> provenFree) {
    ArgumentNullException.ThrowIfNull(decoded);
    ArgumentNullException.ThrowIfNull(provenFree);
    if (imageLength <= 0) return [];

    var result = new List<DefragBlockInfo>();
    var claimed = new List<(long Start, long End)>();

    foreach (var extent in decoded) {
      result.Add(extent);
      if (TryClip(extent.Offset, extent.Length, imageLength, out var start, out var end))
        claimed.Add((start, end));
    }

    // Nothing decoded at all is not a licence to call the image empty: the
    // inherited wipe treats an empty map as "walk failed" and writes nothing.
    if (result.Count == 0) return [];

    var free = new List<(long Start, long End)>();
    foreach (var (offset, length) in provenFree)
      if (TryClip(offset, length, imageLength, out var start, out var end))
        free.Add((start, end));

    var claimedRuns = Merge(claimed);
    foreach (var (start, end) in Subtract(Merge(free), claimedRuns))
      result.Add(new DefragBlockInfo(start, end - start, DefragBlockKind.Free));

    // Whatever is left is allocated as far as the allocator is concerned but no
    // walker named it. It stays reserved.
    var accounted = Merge([.. claimedRuns, .. Merge(free)]);
    foreach (var (start, end) in Subtract([(0L, imageLength)], accounted))
      result.Add(new DefragBlockInfo(start, end - start, DefragBlockKind.MetadataReserved,
        "allocated metadata/unknown", DefragBlockClass.Directory));

    return result;
  }

  /// <summary>Coalesces overlapping and touching ranges into ordered disjoint runs.</summary>
  private static List<(long Start, long End)> Merge(List<(long Start, long End)> ranges) {
    var result = new List<(long Start, long End)>();
    if (ranges.Count == 0) return result;

    ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
    var (currentStart, currentEnd) = ranges[0];
    for (var i = 1; i < ranges.Count; ++i) {
      var (start, end) = ranges[i];
      if (start <= currentEnd) {
        if (end > currentEnd) currentEnd = end;
        continue;
      }

      result.Add((currentStart, currentEnd));
      (currentStart, currentEnd) = (start, end);
    }

    result.Add((currentStart, currentEnd));
    return result;
  }

  /// <summary>Removes <paramref name="holes"/> from the already disjoint, ordered <paramref name="ranges"/>.</summary>
  private static List<(long Start, long End)> Subtract(
      List<(long Start, long End)> ranges,
      List<(long Start, long End)> holes) {
    var result = new List<(long Start, long End)>();
    var holeIndex = 0;

    foreach (var (start, end) in ranges) {
      var cursor = start;
      while (holeIndex > 0 && holes[holeIndex - 1].End > cursor) --holeIndex;

      while (cursor < end) {
        while (holeIndex < holes.Count && holes[holeIndex].End <= cursor) ++holeIndex;
        if (holeIndex >= holes.Count || holes[holeIndex].Start >= end) {
          result.Add((cursor, end));
          break;
        }

        if (holes[holeIndex].Start > cursor)
          result.Add((cursor, holes[holeIndex].Start));
        cursor = Math.Max(cursor, holes[holeIndex].End);
      }
    }

    return result;
  }

  private static bool TryClip(long offset, long length, long limit, out long start, out long end) {
    start = 0;
    end = 0;
    if (length <= 0 || offset >= limit) return false;

    var rawEnd = offset > long.MaxValue - length ? long.MaxValue : offset + length;
    start = Math.Max(0, offset);
    end = Math.Min(limit, rawEnd);
    return end > start;
  }
}
