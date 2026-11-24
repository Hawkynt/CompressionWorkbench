#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Writes one complete generation of a single-device volume's b-trees: the
/// logical trees it is handed, and the allocation trees derived from where
/// everything — those trees included — ends up.
/// </summary>
/// <remarks>
/// <para>The derived trees describe the buckets the b-tree nodes occupy, and are
/// themselves b-tree nodes, so how many buckets the metadata needs depends on the
/// metadata. The count is settled by repetition: lay the trees out, derive, lay
/// out again, until the shape stops changing.</para>
///
/// <para>Whole-volume creation, the in-place commit and metadata relocation all
/// publish through here; they differ only in which buckets they offer the nodes
/// and in what the superblock already says.</para>
/// </remarks>
internal static class BcacheFsMetadataCommit {

  /// <summary>The order trees are laid down in, and the trees this profile owns.</summary>
  internal static readonly int[] TreeOrder = [
    BtreeExtents, BtreeInodes, BtreeDirents, BtreeXattrs, BtreeReflink,
    BtreeSubvolumes, BtreeSnapshots, BtreeSnapshotTrees, BtreeLoggedOps, BtreeSubvolumeChildren,
    BtreeAlloc, BtreeBucketGens, BtreeFreespace, BtreeLru, BtreeBackpointers, BtreeAccounting,
  ];

  /// <summary>The trees whose contents are derived rather than carried (the accounting tree from 1.39).</summary>
  internal static readonly int[] DerivedTrees = [
    BtreeAlloc, BtreeBucketGens, BtreeFreespace, BtreeLru, BtreeBackpointers, BtreeAccounting,
  ];

  /// <summary>A bucket as it was before this commit: its generations and what it held.</summary>
  internal readonly record struct BucketState(byte Generation, byte OldestGeneration, byte DataType);

  /// <summary>
  /// Chooses node slots for the nodes of each tree, in the order the shapes list them, one per
  /// copy (<see cref="BcacheFsDevices.MetadataReplicas" /> consecutive slots per node, each on
  /// another member): slot <c>n</c> of a member is the node at sector <c>n × NodeSectors</c>, the
  /// same number as its bucket wherever a node fills a bucket, and the member index sits above
  /// it as in <see cref="BcacheFsBucketId" />.
  /// </summary>
  internal delegate IReadOnlyDictionary<int, long[]> Placement(
    IReadOnlyDictionary<int, IReadOnlyList<BcacheFsTreeNodeShape>> shapes);

  internal sealed record Published(
    IReadOnlyList<BcacheFsSuperblockComposer.Root> Roots,
    BcacheFsAllocationBuilder.VolumeUsage Usage,
    ulong Inodes,
    IReadOnlyDictionary<long, BucketState> FinalStates,
    IReadOnlySet<long> MetadataBuckets) {
    /// <summary>Every copy of every node the generation wrote, by member and sector.</summary>
    internal IReadOnlyList<BcacheFsAllocationBuilder.Node> Nodes { get; init; } = [];
  }

  /// <summary>
  /// Lays out, derives and writes every tree, and returns what the superblock's
  /// clean section has to say about them.
  /// </summary>
  /// <param name="logical">
  /// The trees carried as they are: extents, inodes, dirents and the subvolume
  /// and snapshot trees. Extent pointers are re-stamped with their bucket's final
  /// generation.
  /// </param>
  /// <param name="previous">What each bucket held before, for generation bookkeeping.</param>
  internal static Published Publish(
      Stream image,
      ulong magic,
      BcacheFsAllocationBuilder.Geometry geometry,
      IReadOnlyDictionary<int, List<Key>> logical,
      IReadOnlyDictionary<long, BucketState> previous,
      Placement place)
    => Publish(BcacheFsDevices.Single(image, geometry), magic, logical, previous, place);

  /// <summary>
  /// Lays out, derives and writes every tree across the members of <paramref name="devices" />.
  /// </summary>
  /// <param name="previous">What each bucket held before, by <see cref="BcacheFsBucketId" />.</param>
  internal static Published Publish(
      BcacheFsDevices devices,
      ulong magic,
      IReadOnlyDictionary<int, List<Key>> logical,
      IReadOnlyDictionary<long, BucketState> previous,
      Placement place) {
    ArgumentNullException.ThrowIfNull(devices);
    ArgumentNullException.ThrowIfNull(logical);
    ArgumentNullException.ThrowIfNull(previous);
    ArgumentNullException.ThrowIfNull(place);

    foreach (var id in logical.Keys)
      if (DerivedTrees.Contains(id) || !TreeOrder.Contains(id))
        throw new ArgumentException($"bcachefs tree {id} is not a logical tree of this profile.", nameof(logical));

    var copies = devices.MetadataReplicas;
    var trees = TreeOrder.ToDictionary(id => id, id => logical.TryGetValue(id, out var keys) ? keys.ToList() : []);
    var extents = trees[BtreeExtents];
    var indirect = trees[BtreeReflink];

    Dictionary<int, IReadOnlyList<BcacheFsTreeNodeShape>> Describe() =>
      TreeOrder.Where(id => trees[id].Count != 0)
        .ToDictionary(id => id, id => BcacheFsTreeLayout.DescribeNodes(id, trees[id]));

    var shapes = Describe();
    IReadOnlyDictionary<int, long[]> placement = new Dictionary<int, long[]>();
    BcacheFsAllocationBuilder.Result derived = null!;
    Dictionary<long, BucketState> final = [];

    var history = new List<string>();
    List<BcacheFsAllocationBuilder.Node> placedNodes = [];
    // Buckets offered to the new generation at any attempt. One of them left empty
    // by the final layout keeps its generation: bumping it would add a bucket_gens
    // key, which can add a node, which takes the bucket back - a layout that never
    // settles. Nothing can point into it stale: every key was re-published.
    var offered = new HashSet<long>();
    for (var attempt = 0; ; ++attempt) {
      if (attempt == 16)
        throw new InvalidOperationException("bcachefs metadata layout did not reach a fixed point: "
          + string.Join(" | ", history));

      placement = place(shapes);
      foreach (var slots in placement.Values) offered.UnionWith(slots.Select(BucketOfSlot));
      var nodes = placedNodes = [];
      foreach (var (id, treeShapes) in shapes) {
        var slots = placement[id];
        if (slots.Length != treeShapes.Count * copies)
          throw new InvalidOperationException(
            $"bcachefs tree {id} was offered {slots.Length} node slots for {treeShapes.Count} nodes of {copies} copies.");
        for (var i = 0; i < slots.Length; ++i)
          nodes.Add(new(id, treeShapes[i / copies].Level, treeShapes[i / copies].MaxKey,
            BcacheFsBucketId.Index(slots[i]) * NodeSectors, BcacheFsBucketId.Device(slots[i])));
      }

      // What every bucket will hold decides its generation: a bucket that held
      // something and now holds nothing has been emptied, and emptying a bucket
      // advances its generation so no stale pointer can be mistaken for a live one.
      var types = BcacheFsAllocationBuilder.Build(devices.Geometry, nodes, extents, indirect: indirect).DataTypes;
      final = [];
      var generations = new Dictionary<long, BcacheFsAllocationBuilder.Generation>();
      foreach (var (device, deviceTypes) in types)
        for (var index = 0L; index < deviceTypes.LongLength; ++index) {
          var b = BcacheFsBucketId.Of(device, index);
          var before = previous.GetValueOrDefault(b);
          var gen = before.Generation;
          var oldest = before.OldestGeneration;
          if (before.DataType != DataFree && deviceTypes[index] == DataFree && !offered.Contains(b)) {
            gen = unchecked((byte)(gen + 1));
            oldest = gen;
          }
          if (gen != 0 || oldest != 0) generations[b] = new(gen, oldest);
          if (gen != 0 || oldest != 0 || deviceTypes[index] != DataFree) final[b] = new(gen, oldest, deviceTypes[index]);
        }

      foreach (var extent in extents) StampGeneration(extent, final);
      foreach (var key in indirect) StampGeneration(key, final);
      derived = BcacheFsAllocationBuilder.Build(devices.Geometry, nodes, extents, generations, indirect);
      trees[BtreeAlloc] = derived.Alloc;
      trees[BtreeFreespace] = derived.Freespace;
      trees[BtreeLru] = derived.Lru;
      trees[BtreeBackpointers] = derived.Backpointers;
      trees[BtreeBucketGens] = derived.BucketGens;
      // From 1.39 the space accounts are keys of their own tree, which counts its own nodes too.
      if (BcacheFsBucketGeometry.Accounting)
        trees[BtreeAccounting] = BcacheFsAccounting.Build(derived.Usage, nodes, trees);

      var next = Describe();
      history.Add(string.Join(",", next.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value.Count}"))
        + " @" + string.Join(",", placement.Values.SelectMany(b => b).Order()));
      if (Signature(next) == Signature(shapes)) break;
      shapes = next;
    }

    var metadataBuckets = placement.Values.SelectMany(b => b).Select(BucketOfSlot).ToHashSet();
    foreach (var bucket in metadataBuckets)
      ZeroRange(devices[BcacheFsBucketId.Device(bucket)],
        BcacheFsBucketId.Index(bucket) * BcacheFsBucketGeometry.SectorsOf(BcacheFsBucketId.Device(bucket)) * SectorSize,
        BcacheFsBucketGeometry.SectorsOf(BcacheFsBucketId.Device(bucket)) * SectorSize);

    byte GenerationOf(long bucket) => final.GetValueOrDefault(bucket).Generation;

    var roots = new List<BcacheFsSuperblockComposer.Root>();
    foreach (var id in TreeOrder) {
      if (!shapes.ContainsKey(id)) continue;
      using var buckets = ((IEnumerable<long>)placement[id]).GetEnumerator();
      var written = BcacheFsTreeLayout.Write(devices, magic, id, trees[id], buckets, GenerationOf);
      roots.Add(new(id, written.Level, written.RootPointer));
    }

    var inodes = (ulong)trees[BtreeInodes].Count(k => k.Type == KeyInodeV3);
    return new Published(roots, derived.Usage, inodes, final, metadataBuckets) { Nodes = placedNodes };
  }

  private static long BucketOfSlot(long slot)
    => BcacheFsBucketId.Of(BcacheFsBucketId.Device(slot),
      BcacheFsBucketId.Index(slot) * NodeSectors / BcacheFsBucketGeometry.SectorsOf(BcacheFsBucketId.Device(slot)));

  /// <summary>Stamps the generation of the bucket an extent's data sits in into its pointer.</summary>
  private static void StampGeneration(Key extent, IReadOnlyDictionary<long, BucketState> states) {
    // A reflink_v's entries follow its refcount.
    var skip = extent.Type == KeyReflinkV ? 8 : 0;
    if (extent.Type is not (KeyExtent or KeyReflinkV) || extent.Value.Length <= skip
        || !BcacheFsExtentCodec.TryParseEntries(extent.Value.AsSpan(skip), out var entries, out _))
      return;
    // Every copy is stamped with the generation of the bucket it is in, on its own member.
    var at = skip;
    foreach (var entry in entries) {
      if (entry.KnownType == BcacheFsExtentEntryType.Pointer) {
        var word = BinaryPrimitives.ReadUInt64LittleEndian(extent.Value.AsSpan(at));
        var (device, sector) = BcacheFsBucketId.Pointer(word);
        var gen = states.GetValueOrDefault(BcacheFsBucketId.OfSector(device, sector)).Generation;
        word = (word & ~(0xFFUL << 56)) | ((ulong)gen << 56);
        BinaryPrimitives.WriteUInt64LittleEndian(extent.Value.AsSpan(at), word);
      }
      at += entry.RawBytes.Length;
    }
  }

  private static string Signature(IReadOnlyDictionary<int, IReadOnlyList<BcacheFsTreeNodeShape>> shapes)
    => string.Join("|", shapes.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value.Select(s =>
      $"{kv.Key}:{s.Level}:{s.MinKey}:{s.MaxKey}")));

  internal static void ZeroRange(Stream image, long offset, long length) {
    if (length <= 0) return;
    var zero = new byte[Math.Min(length, 1 << 20)];
    image.Position = offset;
    while (length > 0) {
      var take = (int)Math.Min(zero.Length, length);
      image.Write(zero, 0, take);
      length -= take;
    }
  }
}
