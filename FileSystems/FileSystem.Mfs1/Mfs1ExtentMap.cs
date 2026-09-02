#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Mfs1;

/// <summary>
/// Reports where an MFS-1 disk's bytes are: the two catalog sectors, each
/// file's run of sectors under its name, and the rest as free.
/// </summary>
/// <remarks>
/// The disk had no layout to report at all, which left every layout-aware verb
/// with nothing to work from. Acorn's catalog is two sectors of fixed slots and
/// each slot records where its file starts, so both questions — which sectors
/// are taken and by whom — are answered by reading it.
/// </remarks>
public static class Mfs1ExtentMap {

  /// <summary>Sectors the catalog occupies before any file data.</summary>
  public const int CatalogSectors = 2;

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new Mfs1Reader(image);
      if (reader.Entries.Count == 0) return [];

      var sectorSize = Mfs1Reader.SectorSize;
      var head = (long)CatalogSectors * sectorSize;
      result.Add(new DefragBlockInfo(0, head, DefragBlockKind.MetadataReserved, "catalog"));

      var claimed = new List<(long Start, long End)>();
      foreach (var entry in reader.Entries) {
        // A file occupies whole sectors: the tail of its last one is slack the
        // catalog's byte length already excludes.
        var start = (long)entry.StartSector * sectorSize;
        var sectors = (entry.Size + sectorSize - 1) / sectorSize;
        var length = (long)sectors * sectorSize;
        if (start < head || length <= 0 || start + length > image.Length) continue;
        result.Add(new DefragBlockInfo(start, length, DefragBlockKind.Used, entry.FullName));
        claimed.Add((start, start + length));
      }
      claimed.Sort((a, b) => a.Start.CompareTo(b.Start));

      var cursor = head;
      foreach (var (start, end) in claimed) {
        if (start > cursor)
          result.Add(new DefragBlockInfo(cursor, start - cursor, DefragBlockKind.Free));
        cursor = Math.Max(cursor, end);
      }
      if (cursor < image.Length)
        result.Add(new DefragBlockInfo(cursor, image.Length - cursor, DefragBlockKind.Free));
    } catch {
      // A disk we cannot walk claims nothing; wiping it would zero live data.
      return [];
    }
    return result;
  }
}
