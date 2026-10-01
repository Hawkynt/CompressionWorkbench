#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Publishes a new clean metadata generation at arbitrary free buckets for the
/// single-device writable profile. All logical keys are materialized first; new
/// trees are written COW, roots are published last, and only then are the old
/// metadata buckets reclaimed. Allocation, freespace, LRU, backpointers and the
/// clean section's usage totals are generated from the future placement itself.
/// </summary>
internal static class BcacheFsMetadataRelocator {
  private const int FirstMetadataBucket = BcacheFsWriter.FirstMetadataBucket;

  internal static BcacheFsMetadataRelocationResult Relocate(Stream image, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    if (options.MetadataZonePlacement == MetadataZone.Unchanged)
      return new BcacheFsMetadataRelocationResult([], [], true);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("bcachefs metadata relocation needs a readable, writable, seekable stream.", nameof(image));

    BcacheFsInPlaceModifier.RequireWritableProfile(image);
    var volume = BcacheFsVolume.Open(image);
    if (!volume.Valid) throw new InvalidDataException(volume.Status);
    if (volume.BucketSectorCount != BucketSectors)
      throw new NotSupportedException(
        $"bcachefs writable profile uses {BucketSectors}-sector buckets; volume uses {volume.BucketSectorCount}.");
    var foreign = volume.Roots.Keys.Except(BcacheFsMetadataCommit.TreeOrder).ToArray();
    if (foreign.Length != 0)
      throw new NotSupportedException(
        $"bcachefs metadata relocation refuses extra live b-trees ({string.Join(",", foreign)}).");

    var logical = BcacheFsMetadataCommit.TreeOrder
      .Where(id => !BcacheFsMetadataCommit.DerivedTrees.Contains(id))
      .ToDictionary(id => id, id => volume.Keys(id).Select(e => new Key(e.Type, e.Position, e.Size, e.Value)).ToList());
    var oldMetadata = BcacheFsMetadataCommit.TreeOrder.SelectMany(volume.NodeSectors)
      .Select(s => s / BucketSectors).ToHashSet();
    var userBuckets = logical[BtreeExtents]
      .Select(e => BcacheFsAllocationBuilder.ExtentDataSector(e) / BucketSectors).ToHashSet();
    var previous = new Dictionary<long, BcacheFsMetadataCommit.BucketState>();
    foreach (var key in volume.Keys(BtreeAlloc))
      if (key.Type == KeyAllocV4 && key.Value.Length >= 20)
        previous[(long)key.Position.Offset] = new(key.Value[12], key.Value[13], key.Value[14]);

    var diagnostics = new List<string>();
    var geometry = BcacheFsSuperblockEditor.ReadGeometry(image);
    var published = BcacheFsMetadataCommit.Publish(image, volume.InternalMagic, geometry, logical, previous,
      shapes => AssignTargets(shapes,
        ChooseTargets(volume, oldMetadata, userBuckets, shapes.Values.Sum(s => s.Count), options, diagnostics),
        options));
    BcacheFsSuperblockEditor.PublishClean(image, published);

    // Once a new clean root set is durable, old metadata that is no longer part
    // of the generation is dead and may be wiped. Do this after publication so
    // a failure before the root switch always leaves the old generation intact.
    var targetSet = published.MetadataBuckets;
    foreach (var bucket in oldMetadata.Except(targetSet))
      BcacheFsMetadataCommit.ZeroRange(image, checked(bucket * (long)BucketBytes), BucketBytes);
    image.Flush();

    image.Position = 0;
    var core = BcacheFsCoreVolume.Open(image);
    if (!core.Recoverable)
      throw new InvalidDataException("bcachefs relocated metadata did not reopen as a recoverable volume: "
        + string.Join("; ", core.Diagnostics));
    foreach (var id in BcacheFsOnDiskCatalog.KnownBtrees) {
      if (core.Root(id) == null) continue;
      var tree = BcacheFsBtreeReader.ReadTree(core, id);
      if (!tree.Complete)
        throw new InvalidDataException($"bcachefs relocated {id} tree is incomplete: {string.Join("; ", tree.Diagnostics)}");
    }

    return new BcacheFsMetadataRelocationResult(
      oldMetadata.OrderBy(x => x).ToArray(),
      targetSet.OrderBy(x => x).ToArray(),
      true);
  }

  private static long[] ChooseTargets(
      BcacheFsVolume volume,
      IReadOnlySet<long> oldMetadata,
      IEnumerable<long> userBuckets,
      int count,
      DefragOptions options,
      List<string> diagnostics) {
    var totalBuckets = volume.DeviceSectors / BucketSectors;
    var tailSbFirst = (volume.DeviceSectors - SbSlotSectors) / BucketSectors;
    var user = userBuckets.ToHashSet();
    var forbidden = new HashSet<long>(oldMetadata);
    foreach (var b in user) forbidden.Add(b);
    for (var b = 0L; b < FirstMetadataBucket; ++b) forbidden.Add(b);
    for (var b = tailSbFirst; b < totalBuckets; ++b) forbidden.Add(b);

    var free = Enumerable.Range(0, checked((int)Math.Min(totalBuckets, int.MaxValue)))
      .Select(i => (long)i)
      .Where(b => b >= FirstMetadataBucket && b < tailSbFirst && !forbidden.Contains(b))
      .ToArray();
    if (free.Length < count)
      throw new IOException(
        $"bcachefs needs {count} COW metadata buckets but only {free.Length} are free outside the current generation.");

    IEnumerable<long> ordered = options.MetadataZonePlacement switch {
      MetadataZone.Back => free.OrderByDescending(b => b),
      MetadataZone.Middle => OrderAround(free, totalBuckets / 2),
      MetadataZone.BeforeContent => BeforeContentOrder(free, user, options.InterleaveStride),
      _ => free.OrderBy(b => b),
    };

    var targets = ordered.Take(count).ToArray();
    if (targets.Length != count)
      throw new IOException("bcachefs metadata target selection exhausted free buckets.");
    if (options.MetadataZonePlacement == MetadataZone.BeforeContent && user.Count != 0
        && !targets.Any(t => user.Any(u => t < u && u - t <= Math.Max(2, options.InterleaveStride))))
      diagnostics.Add("no free holes exist inside the packed data run; metadata fell back to the nearest available buckets.");
    return targets;
  }

  private static Dictionary<int, long[]> AssignTargets(
      IReadOnlyDictionary<int, IReadOnlyList<BcacheFsTreeNodeShape>> shapes,
      IReadOnlyList<long> targets,
      DefragOptions options) {
    var order = MetadataTreeOrder(options.MetadataZonePlacement).Where(shapes.ContainsKey).ToArray();
    var result = order.ToDictionary(id => id, _ => new List<long>());
    var queues = order.ToDictionary(id => id, id => new Queue<BcacheFsTreeNodeShape>(shapes[id]));
    var target = 0;

    // Round-robin tree assignment intentionally interleaves independent metadata
    // trees when the physical policy requests BeforeContent. Other zones keep each
    // tree clustered, which minimizes metadata seeks while still moving the zone.
    if (options.MetadataZonePlacement == MetadataZone.BeforeContent) {
      while (queues.Values.Any(q => q.Count != 0))
        foreach (var btree in order)
          if (queues[btree].Count != 0) {
            queues[btree].Dequeue();
            result[btree].Add(targets[target++]);
          }
    } else {
      foreach (var btree in order)
        while (queues[btree].Count != 0) {
          queues[btree].Dequeue();
          result[btree].Add(targets[target++]);
        }
    }

    return result.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
  }

  private static IEnumerable<long> OrderAround(IEnumerable<long> values, long anchor)
    => values.OrderBy(v => Math.Abs(v - anchor)).ThenBy(v => v);

  private static IEnumerable<long> BeforeContentOrder(
      IReadOnlyList<long> free,
      IReadOnlySet<long> user,
      int stride) {
    if (user.Count == 0) return free.OrderBy(b => b);
    var orderedUsers = user.OrderBy(b => b).ToArray();
    var anchors = orderedUsers.Where((_, i) => i % Math.Max(1, stride) == 0).ToArray();
    return free.OrderBy(b => anchors.Min(a => b <= a ? a - b : (b - a) * 4)).ThenBy(b => b);
  }

  private static IEnumerable<int> MetadataTreeOrder(MetadataZone zone) {
    var locality = BcacheFsMetadataCommit.TreeOrder;
    return zone == MetadataZone.Back ? locality.Reverse() : locality;
  }
}

internal sealed record BcacheFsMetadataRelocationResult(
  IReadOnlyList<long> OldBuckets,
  IReadOnlyList<long> NewBuckets,
  bool Complete);
