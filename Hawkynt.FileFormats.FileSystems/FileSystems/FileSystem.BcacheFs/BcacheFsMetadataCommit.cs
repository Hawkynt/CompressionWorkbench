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
    BtreeExtents, BtreeInodes, BtreeDirents,
    BtreeSubvolumes, BtreeSnapshots, BtreeSnapshotTrees,
    BtreeAlloc, BtreeBucketGens, BtreeFreespace, BtreeLru, BtreeBackpointers,
  ];

  /// <summary>The trees whose contents are derived rather than carried.</summary>
  internal static readonly int[] DerivedTrees = [
    BtreeAlloc, BtreeBucketGens, BtreeFreespace, BtreeLru, BtreeBackpointers,
  ];

  /// <summary>A bucket as it was before this commit: its generations and what it held.</summary>
  internal readonly record struct BucketState(byte Generation, byte OldestGeneration, byte DataType);

  /// <summary>Chooses buckets for the nodes of each tree, in the order the shapes list them.</summary>
  internal delegate IReadOnlyDictionary<int, long[]> Placement(
    IReadOnlyDictionary<int, IReadOnlyList<BcacheFsTreeNodeShape>> shapes);

  internal sealed record Published(
    IReadOnlyList<BcacheFsSuperblockComposer.Root> Roots,
    BcacheFsAllocationBuilder.Usage Usage,
    ulong Inodes,
    IReadOnlyDictionary<long, BucketState> FinalStates,
    IReadOnlySet<long> MetadataBuckets);

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
      Placement place) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(geometry);
    ArgumentNullException.ThrowIfNull(logical);
    ArgumentNullException.ThrowIfNull(previous);
    ArgumentNullException.ThrowIfNull(place);

    foreach (var id in logical.Keys)
      if (DerivedTrees.Contains(id) || !TreeOrder.Contains(id))
        throw new ArgumentException($"bcachefs tree {id} is not a logical tree of this profile.", nameof(logical));

    var trees = TreeOrder.ToDictionary(id => id, id => logical.TryGetValue(id, out var keys) ? keys.ToList() : []);
    var extents = trees[BtreeExtents];

    Dictionary<int, IReadOnlyList<BcacheFsTreeNodeShape>> Describe() =>
      TreeOrder.Where(id => trees[id].Count != 0)
        .ToDictionary(id => id, id => BcacheFsTreeLayout.DescribeNodes(id, trees[id]));

    var shapes = Describe();
    IReadOnlyDictionary<int, long[]> placement = new Dictionary<int, long[]>();
    BcacheFsAllocationBuilder.Result derived = null!;
    Dictionary<long, BucketState> final = [];

    for (var attempt = 0; ; ++attempt) {
      if (attempt == 16)
        throw new InvalidOperationException("bcachefs metadata layout did not reach a fixed point.");

      placement = place(shapes);
      var nodes = new List<BcacheFsAllocationBuilder.Node>();
      foreach (var (id, treeShapes) in shapes) {
        var buckets = placement[id];
        if (buckets.Length != treeShapes.Count)
          throw new InvalidOperationException($"bcachefs tree {id} was offered {buckets.Length} buckets for {treeShapes.Count} nodes.");
        for (var i = 0; i < treeShapes.Count; ++i)
          nodes.Add(new(id, treeShapes[i].Level, treeShapes[i].MaxKey, buckets[i] * BucketSectors));
      }

      // What every bucket will hold decides its generation: a bucket that held
      // something and now holds nothing has been emptied, and emptying a bucket
      // advances its generation so no stale pointer can be mistaken for a live one.
      var types = BcacheFsAllocationBuilder.Build(geometry, nodes, extents).DataType;
      final = [];
      var generations = new Dictionary<long, BcacheFsAllocationBuilder.Generation>();
      for (var b = 0L; b < types.LongLength; ++b) {
        var before = previous.GetValueOrDefault(b);
        var gen = before.Generation;
        var oldest = before.OldestGeneration;
        if (before.DataType != DataFree && types[b] == DataFree) {
          gen = unchecked((byte)(gen + 1));
          oldest = gen;
        }
        if (gen != 0 || oldest != 0) generations[b] = new(gen, oldest);
        if (gen != 0 || oldest != 0 || types[b] != DataFree) final[b] = new(gen, oldest, types[b]);
      }

      foreach (var extent in extents) StampGeneration(extent, final);
      derived = BcacheFsAllocationBuilder.Build(geometry, nodes, extents, generations);
      trees[BtreeAlloc] = derived.Alloc;
      trees[BtreeFreespace] = derived.Freespace;
      trees[BtreeLru] = derived.Lru;
      trees[BtreeBackpointers] = derived.Backpointers;
      trees[BtreeBucketGens] = derived.BucketGens;

      var next = Describe();
      if (Signature(next) == Signature(shapes)) break;
      shapes = next;
    }

    var metadataBuckets = placement.Values.SelectMany(b => b).ToHashSet();
    foreach (var bucket in metadataBuckets) ZeroRange(image, bucket * (long)BucketBytes, BucketBytes);

    byte GenerationOf(long bucket) => final.GetValueOrDefault(bucket).Generation;

    var roots = new List<BcacheFsSuperblockComposer.Root>();
    foreach (var id in TreeOrder) {
      if (!shapes.ContainsKey(id)) continue;
      using var buckets = ((IEnumerable<long>)placement[id]).GetEnumerator();
      var written = BcacheFsTreeLayout.Write(image, magic, id, trees[id], buckets, GenerationOf);
      roots.Add(new(id, written.Level, written.RootPointer));
    }

    var inodes = (ulong)trees[BtreeInodes].Count(k => k.Type == KeyInodeV3);
    return new Published(roots, derived.Usage, inodes, final, metadataBuckets);
  }

  /// <summary>Stamps the generation of the bucket an extent's data sits in into its pointer.</summary>
  private static void StampGeneration(Key extent, IReadOnlyDictionary<long, BucketState> states) {
    for (var at = 0; at + 8 <= extent.Value.Length; at += 8) {
      var word = BinaryPrimitives.ReadUInt64LittleEndian(extent.Value.AsSpan(at));
      if (!IsPointer(word)) continue;
      var gen = states.GetValueOrDefault(PointerSector(word) / BucketSectors).Generation;
      word = (word & ~(0xFFUL << 56)) | ((ulong)gen << 56);
      BinaryPrimitives.WriteUInt64LittleEndian(extent.Value.AsSpan(at), word);
      return;
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
