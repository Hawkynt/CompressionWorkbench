#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.MinixV2;

/// <summary>
/// Reports where a Minix V1 volume's bytes are: its structures, each file's
/// zones under its name, and what nothing holds.
/// </summary>
/// <remarks>
/// The volume had no layout to report at all, which left every layout-aware
/// verb with nothing to work from — a wipe could not tell live bytes from a
/// removed file's leftovers, and a defragmentation had to read every file out
/// and write a fresh volume to move anything.
/// </remarks>
public static class MinixV2ExtentMap {

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new MinixV2Reader(image);
      var zoneSize = MinixV2Reader.ZoneSize;
      var volumeZones = image.Length / zoneSize;

      // The boot block, the superblock, both bitmaps and the inode table are
      // located by nothing that could be repointed.
      var firstData = reader.FirstDataZoneOffset;
      result.Add(new DefragBlockInfo(0, firstData, DefragBlockKind.MetadataReserved,
        "boot block, superblock, bitmaps and inode table"));

      var owned = new List<(long Start, long End)>();
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory) continue;
        foreach (var (offset, length, _) in reader.EnumerateDataExtents(entry)) {
          if (length <= 0) continue;
          result.Add(new DefragBlockInfo(offset, length, DefragBlockKind.Used, entry.Name));
          owned.Add((offset, offset + length));
        }
      }
      owned.Sort((a, b) => a.Start.CompareTo(b.Start));

      // The zone bitmap decides the rest: a zone it marks taken that no file
      // claims is a directory's or an indirect block's, and one it leaves clear
      // is where a removed file's bytes are still sitting.
      long runStart = -1;
      var bitmapOffset = reader.ZoneBitmapOffset;
      var firstDataZone = firstData / zoneSize;
      for (var zone = firstDataZone; zone <= volumeZones; ++zone) {
        var taken = zone < volumeZones && IsTaken(image, bitmapOffset, zone, firstDataZone);
        if (taken) {
          if (runStart < 0) runStart = zone;
          continue;
        }
        if (runStart >= 0) {
          AddUnowned(result, owned, runStart * zoneSize, zone * zoneSize);
          runStart = -1;
        }
      }
    } catch {
      // A volume we cannot walk claims nothing; wiping it would zero live data.
      return [];
    }
    return result;
  }

  /// <summary>
  /// Whether the zone bitmap marks a zone allocated. Minix numbers the bitmap
  /// from the first data zone, with bit zero reserved, so a zone's bit is one
  /// past its distance from that first zone.
  /// </summary>
  private static bool IsTaken(Stream image, long bitmapOffset, long zone, long firstDataZone) {
    var bit = zone - firstDataZone + 1;
    var at = bitmapOffset + bit / 8;
    if (at < 0 || at >= image.Length) return false;
    image.Position = at;
    var value = image.ReadByte();
    return value >= 0 && (value & (1 << (int)(bit % 8))) != 0;
  }

  /// <summary>
  /// Reports the parts of an allocated run that no file claims as the volume's
  /// own structures. Reporting the whole run would describe a file's zones
  /// twice — once under its name and once as immovable — and a layout pass
  /// would then refuse to move anything.
  /// </summary>
  private static void AddUnowned(List<DefragBlockInfo> result,
      List<(long Start, long End)> owned, long start, long end) {
    var cursor = start;
    foreach (var (ownedStart, ownedEnd) in owned) {
      if (ownedEnd <= cursor) continue;
      if (ownedStart >= end) break;
      if (ownedStart > cursor)
        result.Add(new DefragBlockInfo(cursor, ownedStart - cursor, DefragBlockKind.MetadataReserved));
      cursor = Math.Max(cursor, ownedEnd);
      if (cursor >= end) return;
    }
    if (cursor < end)
      result.Add(new DefragBlockInfo(cursor, end - cursor, DefragBlockKind.MetadataReserved));
  }
}
