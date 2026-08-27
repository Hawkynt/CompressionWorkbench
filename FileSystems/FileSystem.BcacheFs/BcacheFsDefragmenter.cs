#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Full offline bcachefs layout pipeline for the writable single-device profile.
/// Metadata is first published as a new COW generation at its requested physical
/// zone. The data pass then sees that actual generation as barriers and packs file
/// extents through every remaining reusable bucket, including the old metadata
/// region that the new generation released.
/// </summary>
internal static class BcacheFsDefragmenter {
  internal static void Defragment(Stream image, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("bcachefs defragmentation needs a readable, writable, seekable stream.", nameof(image));

    options.OnProgress?.Invoke(new DefragProgressEvent(
      "metadata", 0, -1, -1, image.Length, null,
      options.MetadataZonePlacement == MetadataZone.Unchanged
        ? "Mapping bcachefs allocation"
        : "Planning bcachefs metadata placement"));

    if (options.MetadataZonePlacement != MetadataZone.Unchanged)
      BcacheFsMetadataRelocator.Relocate(image, options);

    image.Position = 0;
    var core = BcacheFsCoreVolume.Open(image);
    if (!core.Recoverable)
      throw new InvalidDataException("bcachefs volume is not recoverable before data defragmentation: "
        + string.Join("; ", core.Diagnostics));
    if (core.Members.Count != 1 || core.Devices.Count != 1)
      throw new NotSupportedException(
        "in-place bcachefs data defragmentation currently requires the single-device writable profile; " +
        "the cluster map and placement planner understand multiple members, but publishing cross-device " +
        "replica/EC relocation requires the multi-device transaction writer.");

    var map = BcacheFsClusterMap.Build(core);
    var fatalMap = map.Diagnostics.Where(d =>
      d.Contains("marked free but referenced", StringComparison.OrdinalIgnoreCase)
      || d.Contains("conflicting live owners", StringComparison.OrdinalIgnoreCase)).ToArray();
    if (fatalMap.Length != 0)
      throw new InvalidDataException("bcachefs physical map is inconsistent: " + string.Join("; ", fatalMap));

    var blockMap = BuildPlannerMap(map, image.Length);
    var mover = new BcacheFsBlockMover();
    mover.Init(image);

    var member = core.Members[0];
    var clusterBytes = checked(member.BucketSizeSectors * SectorSize);
    if (clusterBytes != mover.BlockSize)
      throw new NotSupportedException(
        $"bcachefs mover uses {mover.BlockSize}-byte buckets but member geometry is {clusterBytes} bytes.");

    var dataOrigin = checked((long)member.FirstBucket * clusterBytes);
    var moves = DefragPlanner.Plan(
      blockMap,
      dataOrigin,
      image.Length,
      clusterBytes,
      options.Profile,
      options.Mode,
      options.InterleaveStride,
      options.HoleSize,
      options.HoleAt,
      MetadataZone.Unchanged,
      options.LayoutTemplate,
      movableMetadata: null,
      allowMemoryStaging: true);

    DefragPlannerExecutor.Execute(image, options, mover, moves, image.Length);
    mover.Settle(image);

    // Moving extent pointers changes alloc/freespace/backpointer facts. The old
    // SettleAllocation() can only rewrite one-node bookkeeping trees, which would
    // silently reintroduce the exact metadata-size limit this engine removes.
    // Re-materialize the metadata generation instead. When a placement policy was
    // requested this also performs a second COW placement against the FINAL data
    // map, so BeforeContent/interleaving follows where the data actually ended up.
    image.Position = 0;
    if (options.MetadataZonePlacement == MetadataZone.Unchanged)
      BcacheFsInPlaceModifier.NormalizeMetadata(image);
    else
      BcacheFsMetadataRelocator.Relocate(image, options);

    BcacheFsSuperblockEditor.Restamp(image);
    image.Flush();

    image.Position = 0;
    var after = BcacheFsCoreVolume.Open(image);
    if (!after.Recoverable)
      throw new InvalidDataException("bcachefs defragmentation produced an unrecoverable volume: "
        + string.Join("; ", after.Diagnostics));
    var finalMap = BcacheFsClusterMap.Build(after);

    var verifier = new BcacheFsBlockMover();
    verifier.Init(image);
    var allocationErrors = verifier.DescribeAllocationDiscrepancies(image);
    if (allocationErrors.Count != 0)
      throw new InvalidDataException("bcachefs defragmentation left allocation discrepancies: "
        + string.Join("; ", allocationErrors));

    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, image.Length,
      BuildPlannerMap(finalMap, image.Length),
      $"Defragmentation complete: {moves.Count} data moves, " +
      $"{finalMap.Allocations.Count(a => a.Kind == BcacheFsPhysicalKind.BtreeNode)} metadata nodes mapped"));
  }

  /// <summary>
  /// Projects the lossless bucket map into the generic planner's interval model.
  /// Every byte inside an allocation bucket is accounted for. Internal slack in
  /// an occupied bucket is reserved, not called free: bcachefs allocates buckets,
  /// so sub-bucket holes are not legal destinations even when they contain zeros.
  /// </summary>
  internal static IReadOnlyList<DefragBlockInfo> BuildPlannerMap(BcacheFsClusterMap map, long imageLength) {
    ArgumentNullException.ThrowIfNull(map);
    var result = new List<DefragBlockInfo>();
    if (map.Volume.Members.Count == 0) return result;
    var member = map.Volume.Members[0];
    var bucketBytes = checked((long)member.BucketSizeSectors * SectorSize);
    var firstByte = checked((long)member.FirstBucket * bucketBytes);
    if (firstByte > 0)
      result.Add(new DefragBlockInfo(0, firstByte, DefragBlockKind.MetadataReserved,
        "bcachefs pre-allocation/device metadata"));

    foreach (var bucket in map.Buckets.Where(b => b.Device == 0).OrderBy(b => b.Bucket)) {
      var bucketStart = checked(bucket.FirstSector * (long)SectorSize);
      var bucketEnd = Math.Min(imageLength, checked(bucket.EndSector * (long)SectorSize));
      if (bucketEnd <= bucketStart) continue;

      if (bucket.Reusable && bucket.Overlays.Count == 0) {
        result.Add(new DefragBlockInfo(bucketStart, bucketEnd - bucketStart,
          DefragBlockKind.Free, null));
        continue;
      }

      var userRuns = bucket.Overlays
        .Where(a => a.Kind == BcacheFsPhysicalKind.UserExtent && a.Movable)
        .Select(a => {
          var start = Math.Max(bucketStart, checked(a.Sector * (long)SectorSize));
          var end = Math.Min(bucketEnd, checked(a.EndSector * (long)SectorSize));
          var owner = a.Position is { } p
            ? $"inode:{p.Inode}:snapshot:{p.Snapshot}"
            : a.Label;
          return (Start: start, End: end, Owner: owner);
        })
        .Where(r => r.End > r.Start)
        .OrderBy(r => r.Start)
        .ToArray();

      if (userRuns.Length == 0) {
        result.Add(new DefragBlockInfo(bucketStart, bucketEnd - bucketStart,
          DefragBlockKind.MetadataReserved, DescribeBucket(bucket)));
        continue;
      }

      var cursor = bucketStart;
      foreach (var run in userRuns) {
        if (run.Start < cursor)
          throw new InvalidDataException(
            $"bcachefs device 0 bucket {bucket.Bucket} has overlapping user allocations.");
        if (run.Start > cursor)
          result.Add(new DefragBlockInfo(cursor, run.Start - cursor,
            DefragBlockKind.MetadataReserved, $"bucket {bucket.Bucket} allocated slack"));
        result.Add(new DefragBlockInfo(run.Start, run.End - run.Start,
          DefragBlockKind.Used, run.Owner));
        cursor = run.End;
      }
      if (cursor < bucketEnd)
        result.Add(new DefragBlockInfo(cursor, bucketEnd - cursor,
          DefragBlockKind.MetadataReserved, $"bucket {bucket.Bucket} allocated slack"));
    }

    var memberEnd = checked((long)member.BucketCount * bucketBytes);
    if (memberEnd < imageLength)
      result.Add(new DefragBlockInfo(memberEnd, imageLength - memberEnd,
        DefragBlockKind.MetadataReserved, "bcachefs trailing device metadata"));

    return Coalesce(result);
  }

  private static IReadOnlyList<DefragBlockInfo> Coalesce(IReadOnlyList<DefragBlockInfo> input) {
    if (input.Count < 2) return input;
    var ordered = input.OrderBy(e => e.Offset).ThenBy(e => e.Length).ToArray();
    var result = new List<DefragBlockInfo>(ordered.Length);
    foreach (var extent in ordered) {
      if (result.Count != 0) {
        var previous = result[^1];
        if (previous.Offset + previous.Length == extent.Offset
            && previous.Kind == extent.Kind
            && string.Equals(previous.FileName, extent.FileName, StringComparison.Ordinal)) {
          result[^1] = previous with { Length = previous.Length + extent.Length };
          continue;
        }
      }
      result.Add(extent);
    }
    return result;
  }

  private static string DescribeBucket(BcacheFsClusterBucket bucket) {
    var labels = bucket.Overlays.Select(o => o.Label).Distinct().Take(3).ToArray();
    return labels.Length == 0
      ? $"bucket {bucket.Bucket} {bucket.DataType}"
      : string.Join("; ", labels);
  }
}
