#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Sfs;

/// <summary>
/// Describes an SFS volume block by block: what the volume needs to describe
/// itself, what each file owns, and what is free.
/// </summary>
/// <remarks>
/// The root block, its copy at the far end, the bitmap, the admin space, the
/// object node table, the extent tree and the root directory are all reserved.
/// Each records its own block number and is checksummed over its whole block,
/// so moving one without rewriting it would leave a block that fails both
/// checks — and the volume with it.
/// </remarks>
public static class SfsExtentMap {

  /// <summary>The layout a pass plans against.</summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var volume = new SfsVolume(image);
    if (!volume.Valid) yield break;

    var bs = volume.BlockSize;
    var claimed = new List<(long Start, long Length, DefragBlockKind Kind, string? Name)>();

    foreach (var block in volume.ReservedBlocks.Distinct())
      claimed.Add((block * bs, bs, DefragBlockKind.MetadataReserved, "SFS structure"));

    foreach (var file in volume.Files)
      foreach (var extent in file.Extents) {
        if (extent.Count <= 0) continue;
        claimed.Add((extent.Block * bs, extent.Count * bs, DefragBlockKind.Used, file.Name));
      }

    claimed.Sort((a, b) => a.Start.CompareTo(b.Start));

    var cursor = 0L;
    foreach (var (start, length, kind, name) in claimed) {
      if (start > cursor)
        yield return new DefragBlockInfo(cursor, start - cursor, DefragBlockKind.Free, null);
      if (start < cursor) continue;   // an overlap is not something to describe twice

      yield return new DefragBlockInfo(start, length, kind, name);
      cursor = start + length;
    }

    if (cursor < volume.ImageLength)
      yield return new DefragBlockInfo(cursor, volume.ImageLength - cursor, DefragBlockKind.Free, null);
  }

  /// <summary>
  /// The first byte a file may occupy: past the structures the volume opens
  /// with. The copy of the root block at the far end is reserved too, but it is
  /// behind the data rather than in front of it.
  /// </summary>
  public static long FirstDataByte(SfsVolume volume) {
    ArgumentNullException.ThrowIfNull(volume);

    var tail = volume.TotalBlocks - 1;
    var leading = volume.ReservedBlocks.Where(b => b != tail).DefaultIfEmpty(0).Max();
    return (leading + 1) * volume.BlockSize;
  }
}
