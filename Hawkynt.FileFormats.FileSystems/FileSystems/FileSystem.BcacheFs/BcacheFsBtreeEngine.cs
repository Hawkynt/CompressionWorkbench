#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Query surface over the post-recovery b-tree view. The underlying reader starts
/// at <see cref="BcacheFsCoreVolume.EffectiveRoots"/>, follows arbitrary interior
/// levels up to the on-disk depth limit, merges appended bsets and journal keys,
/// and retries replicated metadata pointers. This layer adds deterministic exact
/// lookup and ordered half-open range iteration over the resulting leaf slots.
/// </summary>
internal static class BcacheFsBtreeEngine {
  internal static BcacheFsBtreeLookupResult Lookup(
      BcacheFsCoreVolume volume,
      BcacheFsBtreeId id,
      Bpos position) {
    ArgumentNullException.ThrowIfNull(volume);

    var tree = BcacheFsBtreeReader.ReadTree(volume, id);
    var diagnostics = tree.Diagnostics.Concat(ValidateRanges(tree)).ToArray();
    if (!tree.Complete || diagnostics.Length != tree.Diagnostics.Count)
      return new BcacheFsBtreeLookupResult(id, position, null, tree.Nodes, diagnostics, false);

    var keys = tree.MaterializedLeafSlots;
    var index = LowerBound(keys, position);
    var key = index < keys.Count && BcacheFsFormat.Compare(keys[index].Position, position) == 0
      ? keys[index]
      : null;
    return new BcacheFsBtreeLookupResult(id, position, key, tree.Nodes, diagnostics, true);
  }

  /// <summary>
  /// Returns materialized leaf slots in the half-open interval [start,end).
  /// Deleted slots stay deleted because bset/journal last-writer-wins composition
  /// occurs before this filter is applied.
  /// </summary>
  internal static BcacheFsBtreeRangeResult ReadRange(
      BcacheFsCoreVolume volume,
      BcacheFsBtreeId id,
      Bpos start,
      Bpos endExclusive) {
    ArgumentNullException.ThrowIfNull(volume);
    if (BcacheFsFormat.Compare(start, endExclusive) > 0)
      throw new ArgumentOutOfRangeException(nameof(endExclusive), "Range end must not precede range start.");

    var tree = BcacheFsBtreeReader.ReadTree(volume, id);
    var diagnostics = tree.Diagnostics.Concat(ValidateRanges(tree)).ToArray();
    if (!tree.Complete || diagnostics.Length != tree.Diagnostics.Count)
      return new BcacheFsBtreeRangeResult(id, start, endExclusive, [], tree.Nodes, diagnostics, false);

    var keys = tree.MaterializedLeafSlots;
    var first = LowerBound(keys, start);
    var last = LowerBound(keys, endExclusive);
    var count = last - first;
    IReadOnlyList<BcacheFsRawKey> result = count == 0
      ? []
      : keys.Skip(first).Take(count).ToArray();
    return new BcacheFsBtreeRangeResult(id, start, endExclusive, result, tree.Nodes, diagnostics, true);
  }

  /// <summary>
  /// Validates the recovered B+tree topology, not merely individual nodes. Every
  /// populated level must be a gap-free partition of the root's closed range;
  /// raw node identity fields must match the traversal identity; and every live
  /// leaf key must lie in exactly the leaf range selected by ordered traversal.
  /// </summary>
  internal static IReadOnlyList<string> ValidateRanges(BcacheFsBtreeReadResult tree) {
    ArgumentNullException.ThrowIfNull(tree);
    var diagnostics = new List<string>();
    if (tree.Nodes.Count == 0) {
      diagnostics.Add($"btree {tree.BtreeId} contains no root node.");
      return diagnostics;
    }

    var maxLevel = tree.Nodes.Max(n => (int)n.Level);
    if (maxLevel >= BcacheFsOnDiskCatalog.MaxBtreeDepth)
      diagnostics.Add($"btree {tree.BtreeId} reaches level {maxLevel}; maximum valid level is {BcacheFsOnDiskCatalog.MaxBtreeDepth - 1}.");

    var roots = tree.Nodes.Where(n => n.Level == maxLevel).ToArray();
    if (roots.Length != 1) {
      diagnostics.Add($"btree {tree.BtreeId} has {roots.Length} nodes at root level {maxLevel}; expected exactly one.");
      return diagnostics;
    }
    var root = roots[0];

    foreach (var node in tree.Nodes) {
      if (BcacheFsFormat.Compare(node.MinKey, node.MaxKey) > 0)
        diagnostics.Add($"btree {tree.BtreeId} level {node.Level} node has min key after max key.");

      if (node.RawBytes.Length < 32) {
        diagnostics.Add($"btree {tree.BtreeId} level {node.Level} node is too short to contain identity flags.");
        continue;
      }

      var flags = BinaryPrimitives.ReadUInt64LittleEndian(node.RawBytes.AsSpan(24));
      var rawBtree = (flags & 0xFUL) | (((flags >> 9) & 0xFFFFUL) << 4);
      var rawLevel = (flags >> 4) & 0xFUL;
      if (rawBtree != (ulong)(byte)tree.BtreeId)
        diagnostics.Add($"btree {tree.BtreeId} node encodes raw btree id {rawBtree}; traversal identity does not match.");
      if (rawLevel != node.Level)
        diagnostics.Add($"btree {tree.BtreeId} node record level {node.Level} disagrees with raw level {rawLevel}.");
    }

    for (var level = 0; level <= maxLevel; ++level) {
      var levelNodes = tree.Nodes
        .Where(n => n.Level == level)
        .OrderBy(n => n.MinKey, BposComparer.Instance)
        .ToArray();
      if (levelNodes.Length == 0) {
        diagnostics.Add($"btree {tree.BtreeId} has no nodes at required level {level}.");
        continue;
      }

      if (BcacheFsFormat.Compare(levelNodes[0].MinKey, root.MinKey) != 0)
        diagnostics.Add($"btree {tree.BtreeId} level {level} starts at {Format(levelNodes[0].MinKey)}, root starts at {Format(root.MinKey)}.");
      if (BcacheFsFormat.Compare(levelNodes[^1].MaxKey, root.MaxKey) != 0)
        diagnostics.Add($"btree {tree.BtreeId} level {level} ends at {Format(levelNodes[^1].MaxKey)}, root ends at {Format(root.MaxKey)}.");

      for (var i = 1; i < levelNodes.Length; ++i) {
        var previous = levelNodes[i - 1];
        var current = levelNodes[i];
        var cmp = BcacheFsFormat.Compare(previous.MaxKey, current.MinKey);
        if (cmp >= 0) {
          diagnostics.Add($"btree {tree.BtreeId} level {level} ranges overlap at {Format(current.MinKey)}.");
          continue;
        }
        if (!TrySuccessor(previous.MaxKey, out var expectedMin) ||
            BcacheFsFormat.Compare(expectedMin, current.MinKey) != 0)
          diagnostics.Add($"btree {tree.BtreeId} level {level} has a range gap between {Format(previous.MaxKey)} and {Format(current.MinKey)}.");
      }
    }

    var leaves = tree.Nodes
      .Where(n => n.Level == 0)
      .OrderBy(n => n.MinKey, BposComparer.Instance)
      .ToArray();
    var leafIndex = 0;
    Bpos? previousKey = null;
    foreach (var key in tree.MaterializedLeafSlots) {
      if (previousKey is { } previous && BcacheFsFormat.Compare(previous, key.Position) >= 0)
        diagnostics.Add($"btree {tree.BtreeId} materialized leaf keys are not strictly ordered at {Format(key.Position)}.");
      previousKey = key.Position;

      while (leafIndex < leaves.Length && BcacheFsFormat.Compare(key.Position, leaves[leafIndex].MaxKey) > 0)
        ++leafIndex;
      if (leafIndex >= leaves.Length ||
          BcacheFsFormat.Compare(key.Position, leaves[leafIndex].MinKey) < 0)
        diagnostics.Add($"btree {tree.BtreeId} key {Format(key.Position)} does not belong to a recovered leaf range.");
    }

    return diagnostics;
  }

  private static int LowerBound(IReadOnlyList<BcacheFsRawKey> keys, Bpos position) {
    var lo = 0;
    var hi = keys.Count;
    while (lo < hi) {
      var mid = lo + ((hi - lo) >> 1);
      if (BcacheFsFormat.Compare(keys[mid].Position, position) < 0)
        lo = mid + 1;
      else
        hi = mid;
    }
    return lo;
  }

  private static bool TrySuccessor(Bpos position, out Bpos successor) {
    if (position.Snapshot != uint.MaxValue) {
      successor = position with { Snapshot = position.Snapshot + 1 };
      return true;
    }
    if (position.Offset != ulong.MaxValue) {
      successor = new Bpos(position.Inode, position.Offset + 1, 0);
      return true;
    }
    if (position.Inode != ulong.MaxValue) {
      successor = new Bpos(position.Inode + 1, 0, 0);
      return true;
    }
    successor = default;
    return false;
  }

  private static string Format(Bpos position)
    => $"{position.Inode}:{position.Offset}:{position.Snapshot}";

  private sealed class BposComparer : IComparer<Bpos> {
    internal static readonly BposComparer Instance = new();
    public int Compare(Bpos x, Bpos y) => BcacheFsFormat.Compare(x, y);
  }
}

internal sealed record BcacheFsBtreeLookupResult(
  BcacheFsBtreeId BtreeId,
  Bpos Position,
  BcacheFsRawKey? Key,
  IReadOnlyList<BcacheFsBtreeNodeRecord> Nodes,
  IReadOnlyList<string> Diagnostics,
  bool Complete);

internal sealed record BcacheFsBtreeRangeResult(
  BcacheFsBtreeId BtreeId,
  Bpos Start,
  Bpos EndExclusive,
  IReadOnlyList<BcacheFsRawKey> Keys,
  IReadOnlyList<BcacheFsBtreeNodeRecord> Nodes,
  IReadOnlyList<string> Diagnostics,
  bool Complete);
