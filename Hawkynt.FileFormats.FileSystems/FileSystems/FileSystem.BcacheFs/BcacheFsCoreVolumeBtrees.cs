#pragma warning disable CS1591
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

internal static class BcacheFsCoreVolumeBtrees {
  internal static BcacheFsBtreeReadResult ReadTree(this BcacheFsCoreVolume volume, BcacheFsBtreeId id) {
    ArgumentNullException.ThrowIfNull(volume);
    var tree = BcacheFsBtreeReader.ReadTree(volume, id);
    var structural = BcacheFsBtreeEngine.ValidateRanges(tree);
    if (structural.Count == 0)
      return tree;

    return tree with {
      Diagnostics = tree.Diagnostics.Concat(structural).ToArray(),
      Complete = false,
    };
  }

  internal static BcacheFsBtreeLookupResult Lookup(
      this BcacheFsCoreVolume volume,
      BcacheFsBtreeId id,
      Bpos position) {
    ArgumentNullException.ThrowIfNull(volume);
    return BcacheFsBtreeEngine.Lookup(volume, id, position);
  }

  internal static BcacheFsBtreeRangeResult ReadRange(
      this BcacheFsCoreVolume volume,
      BcacheFsBtreeId id,
      Bpos start,
      Bpos endExclusive) {
    ArgumentNullException.ThrowIfNull(volume);
    return BcacheFsBtreeEngine.ReadRange(volume, id, start, endExclusive);
  }
}