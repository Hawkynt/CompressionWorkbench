#pragma warning disable CS1591
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

internal static class BcacheFsTreeLayout {
  private const int PointerKeyBytes = BkeyBytes + 48;

  internal static IReadOnlyList<BcacheFsTreeNodeShape> DescribeNodes(int btree, IReadOnlyList<Key> keys) {
    var sorted = keys.OrderBy(k => k,
      Comparer<Key>.Create((a, b) => Compare(a.Position, b.Position))).ToArray();
    var leaves = PartitionKeys(sorted);
    var shapes = new List<BcacheFsTreeNodeShape>();
    var current = new List<BcacheFsTreeNodeShape>(leaves.Count);
    for (var i = 0; i < leaves.Count; ++i) {
      var min = i == 0 ? Bpos.Min : Successor(leaves[i - 1][^1].Position);
      var max = i == leaves.Count - 1 ? Bpos.Max : leaves[i][^1].Position;
      var shape = new BcacheFsTreeNodeShape(btree, 0, min, max);
      shapes.Add(shape);
      current.Add(shape);
    }

    var level = 0;
    while (current.Count > 1) {
      if (++level >= BcacheFsOnDiskCatalog.MaxBtreeDepth)
        throw new NotSupportedException(
          $"bcachefs tree {btree} exceeds maximum level {BcacheFsOnDiskCatalog.MaxBtreeDepth - 1}.");
      var next = new List<BcacheFsTreeNodeShape>();
      var index = 0;
      while (index < current.Count) {
        var start = index;
        var bytes = BcacheFsNodeBuilder.KeysOffset;
        while (index < current.Count && bytes + PointerKeyBytes <= BucketBytes) {
          bytes += PointerKeyBytes;
          ++index;
        }
        if (index == start) throw new NotSupportedException("bcachefs interior pointer does not fit in a node.");
        var shape = new BcacheFsTreeNodeShape(btree, level,
          current[start].MinKey, current[index - 1].MaxKey);
        shapes.Add(shape);
        next.Add(shape);
      }
      current = next;
    }
    return shapes;
  }

  internal static int CountNodes(IReadOnlyList<Key> keys) => DescribeNodes(-1, keys).Count;

  internal static BcacheFsWrittenTree Write(
      Stream image,
      ulong magic,
      int btree,
      IReadOnlyList<Key> keys,
      IEnumerator<long> targetBuckets) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(keys);
    ArgumentNullException.ThrowIfNull(targetBuckets);

    var written = new List<BcacheFsWrittenNode>();
    var sorted = keys.OrderBy(k => k,
      Comparer<Key>.Create((a, b) => Compare(a.Position, b.Position))).ToArray();
    var leaves = PartitionKeys(sorted);
    var current = new List<WrittenNode>(leaves.Count);
    for (var i = 0; i < leaves.Count; ++i) {
      var min = i == 0 ? Bpos.Min : Successor(leaves[i - 1][^1].Position);
      var max = i == leaves.Count - 1 ? Bpos.Max : leaves[i][^1].Position;
      current.Add(WriteNode(image, magic, btree, 0, min, max, leaves[i], targetBuckets, written));
    }

    var level = 0;
    while (current.Count > 1) {
      if (++level >= BcacheFsOnDiskCatalog.MaxBtreeDepth)
        throw new NotSupportedException(
          $"bcachefs tree {btree} exceeds maximum level {BcacheFsOnDiskCatalog.MaxBtreeDepth - 1}.");
      var next = new List<WrittenNode>();
      var index = 0;
      while (index < current.Count) {
        var group = new List<WrittenNode>();
        var bytes = BcacheFsNodeBuilder.KeysOffset;
        while (index < current.Count && bytes + current[index].Pointer.Bytes <= BucketBytes) {
          bytes += current[index].Pointer.Bytes;
          group.Add(current[index++]);
        }
        if (group.Count == 0) throw new NotSupportedException("bcachefs interior pointer does not fit in a node.");
        next.Add(WriteNode(image, magic, btree, level,
          group[0].MinKey, group[^1].MaxKey,
          group.Select(n => n.Pointer).ToArray(), targetBuckets, written));
      }
      current = next;
    }

    var root = current[0];
    return new BcacheFsWrittenTree(btree, level, root.Pointer, root.Bucket,
      written.OrderBy(n => n.Level).ThenBy(n => n.Bucket).ToArray());
  }

  private static WrittenNode WriteNode(
      Stream image,
      ulong magic,
      int btree,
      int level,
      Bpos min,
      Bpos max,
      IReadOnlyList<Key> keys,
      IEnumerator<long> targetBuckets,
      List<BcacheFsWrittenNode> written) {
    if (!targetBuckets.MoveNext())
      throw new InvalidOperationException($"bcachefs placement ran out of target buckets while writing tree {btree}.");
    var bucket = targetBuckets.Current;
    if (bucket < 0) throw new InvalidOperationException("bcachefs metadata target bucket is negative.");

    var builder = new BcacheFsNodeBuilder {
      BtreeId = btree,
      Seq = NextSequence(),
      SuperblockMagic = magic,
      Level = level,
      MinKey = min,
      MaxKey = max,
    };
    foreach (var key in keys) builder.Add(key);

    var buffer = new byte[BucketBytes];
    var sectors = builder.Write(buffer);
    var sector = checked(bucket * (long)BucketSectors);
    image.Position = checked(sector * (long)SectorSize);
    image.Write(buffer, 0, sectors * SectorSize);

    written.Add(new BcacheFsWrittenNode(btree, level, bucket, sector, sectors, min, max));
    return new WrittenNode(min, max, builder.Pointer(sector, sectors), bucket);
  }

  private static List<Key[]> PartitionKeys(IReadOnlyList<Key> sorted) {
    if (sorted.Count == 0) return [Array.Empty<Key>()];
    var result = new List<Key[]>();
    var index = 0;
    while (index < sorted.Count) {
      var start = index;
      var bytes = BcacheFsNodeBuilder.KeysOffset;
      while (index < sorted.Count && bytes + sorted[index].Bytes <= BucketBytes) {
        bytes += sorted[index].Bytes;
        ++index;
      }
      if (index == start) throw new NotSupportedException("bcachefs key is too large for one b-tree node.");
      result.Add(sorted.Skip(start).Take(index - start).ToArray());
    }
    return result;
  }

  private static long _sequence = DateTime.UtcNow.Ticks;
  private static ulong NextSequence()
    => unchecked((ulong)Interlocked.Increment(ref _sequence) * 0x9E3779B97F4A7C15UL);

  private sealed record WrittenNode(Bpos MinKey, Bpos MaxKey, Key Pointer, long Bucket);
}

internal sealed record BcacheFsTreeNodeShape(int Btree, int Level, Bpos MinKey, Bpos MaxKey);

internal sealed record BcacheFsWrittenTree(
  int Btree,
  int Level,
  Key RootPointer,
  long RootBucket,
  IReadOnlyList<BcacheFsWrittenNode> Nodes);

internal sealed record BcacheFsWrittenNode(
  int Btree,
  int Level,
  long Bucket,
  long Sector,
  int SectorsWritten,
  Bpos MinKey,
  Bpos MaxKey);
