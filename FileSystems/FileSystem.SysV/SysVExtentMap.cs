#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.SysV;

/// <summary>
/// Reports where a System V volume's bytes are: its structures, each file's
/// blocks under its name, and what nothing holds.
/// </summary>
/// <remarks>
/// The volume had no layout to report at all, which left every layout-aware
/// verb with nothing to work from. It tracks free space with a chained cache in
/// the superblock rather than a bitmap, so what is taken is answered by walking
/// the inodes — which also answers by whom, and what is left over is free.
/// </remarks>
public static class SysVExtentMap {

    /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new SysVReader(image);

      // The boot block, the superblock and the inode list are located by
      // nothing that could be repointed.
      var firstData = reader.FirstDataByte;
      result.Add(new DefragBlockInfo(0, firstData, DefragBlockKind.MetadataReserved,
        "boot block, superblock and inode list"));

      var claimed = new List<(long Start, long End)>();
      foreach (var (offset, length, _, owner) in reader.EnumerateLayout()) {
        if (length <= 0 || offset < firstData || offset + length > image.Length) continue;
        result.Add(new DefragBlockInfo(offset, length,
          owner == null ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used, owner));
        claimed.Add((offset, offset + length));
      }
      claimed.Sort((a, b) => a.Start.CompareTo(b.Start));

      // Nothing else is spoken for. On this filesystem that is exactly the free
      // space, including the blocks a removed file used to occupy.
      var cursor = firstData;
      foreach (var (start, end) in claimed) {
        if (start > cursor)
          result.Add(new DefragBlockInfo(cursor, start - cursor, DefragBlockKind.Free));
        cursor = Math.Max(cursor, end);
      }
      if (cursor < image.Length)
        result.Add(new DefragBlockInfo(cursor, image.Length - cursor, DefragBlockKind.Free));
    } catch {
      // A volume we cannot walk claims nothing; wiping it would zero live data.
      return [];
    }
    return result;
  }
}
