namespace Compression.Registry;

/// <summary>
/// Converts a filesystem's authoritative free-space information plus any decoded
/// file extents into a non-overlapping, gap-free physical allocation map.
/// </summary>
/// <remarks>
/// <para>
/// The free-space structure is the authority. Bytes that are not positively proven
/// free are allocated, and allocated bytes that are not positively decoded as one
/// file are reported as <see cref="DefragBlockKind.MetadataReserved"/>. This is the
/// fail-closed rule required by <see cref="IFilesystemExtentMap"/>: new or unknown
/// metadata can reduce optimisation opportunities, but can never become a wipe target.
/// </para>
/// <para>
/// Decoded file extents are rounded outward to allocation-unit boundaries. A decoded
/// extent that starts off-boundary is ignored rather than guessed. If decoded file
/// data overlaps proven free space, or two different owners claim the same bytes, the
/// conflicting range becomes metadata-reserved instead of trusting either claim.
/// </para>
/// </remarks>
public static class FilesystemAllocationMapCompleter {

  /// <summary>
  /// Produces a complete physical map covering <c>[0, imageLength)</c>.
  /// </summary>
  public static IReadOnlyList<DefragBlockInfo> Complete(
      long imageLength,
      int allocationUnit,
      IEnumerable<DefragBlockInfo> decoded,
      IEnumerable<(long Offset, long Length)> provenFree) {
    if (imageLength <= 0) return [];
    if (allocationUnit <= 0) throw new ArgumentOutOfRangeException(nameof(allocationUnit));
    ArgumentNullException.ThrowIfNull(decoded);
    ArgumentNullException.ThrowIfNull(provenFree);

    var events = new List<BoundaryEvent>();

    foreach (var (offset, length) in provenFree) {
      if (!TryClip(offset, length, imageLength, out var start, out var end)) continue;
      events.Add(BoundaryEvent.Free(start, +1));
      events.Add(BoundaryEvent.Free(end, -1));
    }

    foreach (var extent in decoded) {
      if (extent.Kind != DefragBlockKind.Used || string.IsNullOrEmpty(extent.FileName)) continue;
      if (extent.Offset < 0 || extent.Length <= 0 || extent.Offset % allocationUnit != 0) continue;

      var endRaw = SaturatingAdd(extent.Offset, extent.Length);
      var endAligned = AlignUp(endRaw, allocationUnit);
      if (!TryClip(extent.Offset, endAligned - extent.Offset, imageLength, out var start, out var end)) continue;

      events.Add(BoundaryEvent.Owner(start, extent.FileName!, extent.Classification, +1));
      events.Add(BoundaryEvent.Owner(end, extent.FileName!, extent.Classification, -1));
    }

    events.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));

    var result = new List<DefragBlockInfo>();
    var activeOwners = new Dictionary<string, OwnerState>(StringComparer.Ordinal);
    var freeDepth = 0;
    var current = 0L;
    var eventIndex = 0;

    while (eventIndex < events.Count) {
      var at = Math.Clamp(events[eventIndex].Offset, 0, imageLength);
      if (at > current)
        Append(result, current, at - current, Classify(freeDepth, activeOwners));

      while (eventIndex < events.Count && events[eventIndex].Offset == at) {
        var change = events[eventIndex++];
        freeDepth += change.FreeDelta;
        if (change.Owner is not { } owner) continue;

        if (!activeOwners.TryGetValue(owner, out var state))
          state = new OwnerState(0, change.Classification);
        state = state with { Count = state.Count + change.OwnerDelta };
        if (state.Count <= 0)
          activeOwners.Remove(owner);
        else
          activeOwners[owner] = state;
      }

      current = at;
      if (current >= imageLength) break;
    }

    if (current < imageLength)
      Append(result, current, imageLength - current, Classify(freeDepth, activeOwners));

    return result;
  }

  private static (DefragBlockKind Kind, string? Owner, DefragBlockClass? Classification) Classify(
      int freeDepth,
      Dictionary<string, OwnerState> activeOwners) {
    if (freeDepth > 0)
      return activeOwners.Count == 0
        ? (DefragBlockKind.Free, null, null)
        : (DefragBlockKind.MetadataReserved, "allocation conflict", DefragBlockClass.Directory);

    if (activeOwners.Count == 1) {
      var pair = activeOwners.First();
      return (DefragBlockKind.Used, pair.Key, pair.Value.Classification);
    }

    return activeOwners.Count > 1
      ? (DefragBlockKind.MetadataReserved, "shared/ambiguous allocation", DefragBlockClass.Directory)
      : (DefragBlockKind.MetadataReserved, "allocated metadata/unknown", DefragBlockClass.Directory);
  }

  private static void Append(
      List<DefragBlockInfo> result,
      long offset,
      long length,
      (DefragBlockKind Kind, string? Owner, DefragBlockClass? Classification) state) {
    if (length <= 0) return;

    if (result.Count > 0) {
      var previous = result[^1];
      if (previous.Offset + previous.Length == offset &&
          previous.Kind == state.Kind &&
          string.Equals(previous.FileName, state.Owner, StringComparison.Ordinal) &&
          previous.Classification == state.Classification) {
        result[^1] = previous with { Length = previous.Length + length };
        return;
      }
    }

    result.Add(new DefragBlockInfo(offset, length, state.Kind, state.Owner, state.Classification));
  }

  private static bool TryClip(long offset, long length, long limit, out long start, out long end) {
    start = 0;
    end = 0;
    if (length <= 0 || offset >= limit) return false;

    var rawEnd = SaturatingAdd(offset, length);
    start = Math.Max(0, offset);
    end = Math.Min(limit, rawEnd);
    return end > start;
  }

  private static long AlignUp(long value, int alignment) {
    if (value <= 0) return 0;
    var remainder = value % alignment;
    if (remainder == 0) return value;
    return SaturatingAdd(value, alignment - remainder);
  }

  private static long SaturatingAdd(long left, long right) {
    if (right > 0 && left > long.MaxValue - right) return long.MaxValue;
    if (right < 0 && left < long.MinValue - right) return long.MinValue;
    return left + right;
  }

  private readonly record struct BoundaryEvent(
    long Offset,
    int FreeDelta,
    string? Owner,
    DefragBlockClass? Classification,
    int OwnerDelta) {

    public static BoundaryEvent Free(long offset, int delta)
      => new(offset, delta, null, null, 0);

    public static BoundaryEvent Owner(long offset, string owner, DefragBlockClass? classification, int delta)
      => new(offset, 0, owner, classification, delta);
  }

  private readonly record struct OwnerState(int Count, DefragBlockClass? Classification);
}
