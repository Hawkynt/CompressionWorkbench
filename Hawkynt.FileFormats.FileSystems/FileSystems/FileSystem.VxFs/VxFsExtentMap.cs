#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.VxFs;

/// <summary>
/// Describes a VxFS volume block by block: what the walk to the files needs,
/// what each file owns, and what is left.
/// </summary>
/// <remarks>
/// Everything the driver reads before it reaches a file is reserved here — the
/// superblock, the object location table, the raw inode array, the fileset
/// headers, both inode lists and the root directory. A file moved onto any of
/// them would be a volume that no longer mounts, which is a harder failure than
/// a fragmented one.
/// </remarks>
public static class VxFsExtentMap {

  /// <summary>The layout a pass plans against.</summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var volume = new VxFsVolume(image);
    if (!volume.Valid) yield break;

    var bs = volume.BlockSize;
    var claimed = new List<(long Start, long Length, DefragBlockKind Kind, string? Name)>();

    foreach (var extent in volume.ReservedExtents) {
      if (extent.Count <= 0) continue;
      claimed.Add((extent.Block * bs, extent.Count * bs, DefragBlockKind.MetadataReserved,
        "VxFS structure"));
    }

    foreach (var extent in volume.RootDirectoryExtents) {
      if (extent.Count <= 0) continue;
      claimed.Add((extent.Block * bs, extent.Count * bs, DefragBlockKind.MetadataReserved,
        "Root directory"));
    }

    foreach (var file in volume.Files)
      foreach (var extent in file.Extents) {
        if (extent.Count <= 0) continue;
        claimed.Add((extent.Block * bs, extent.Count * bs, DefragBlockKind.Used, file.Name));
      }

    claimed.Sort((a, b) => a.Start.CompareTo(b.Start));

    // What nothing claimed is free, and the gaps between claims are where a
    // pass has room to work.
    var cursor = 0L;
    foreach (var (start, length, kind, name) in claimed) {
      if (start > cursor)
        yield return new DefragBlockInfo(cursor, start - cursor, DefragBlockKind.Free, null);
      if (start < cursor) continue;   // an overlap is not something to describe twice

      yield return new DefragBlockInfo(start, length, kind, name);
      cursor = start + length;
    }

    if (cursor < volume.ImageLength)
      yield return new DefragBlockInfo(
        cursor, volume.ImageLength - cursor, DefragBlockKind.Free, null);
  }

  /// <summary>
  /// The first byte a file may occupy: past everything the walk to the files
  /// depends on.
  /// </summary>
  public static long FirstDataByte(VxFsVolume volume) {
    ArgumentNullException.ThrowIfNull(volume);

    var end = 0L;
    foreach (var extent in volume.ReservedExtents)
      end = Math.Max(end, (extent.Block + extent.Count) * volume.BlockSize);
    foreach (var extent in volume.RootDirectoryExtents)
      end = Math.Max(end, (extent.Block + extent.Count) * volume.BlockSize);

    return end;
  }
}
