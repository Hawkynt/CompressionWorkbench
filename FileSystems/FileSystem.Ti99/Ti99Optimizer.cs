#pragma warning disable CS1591
namespace FileSystem.Ti99;

/// <summary>
/// Picks the smallest standard TI-99 disk geometry that fits a given fileset.
/// Standard geometries: 35 or 40 tracks × 9 or 18 sectors-per-track × 1 or 2
/// sides = 256-byte sectors throughout. TIFiles mode is single-file so most
/// knobs don't apply; for SectorDump the optimizer iterates the eight
/// standard combinations and returns the smallest that holds payload +
/// FDR + VIB + FDIR overhead.
/// </summary>
public static class Ti99Optimizer {

  /// <summary>
  /// Represents a ti 99 geometry.
  /// </summary>
public sealed record Ti99Geometry(int Tracks, int SectorsPerTrack, int Sides, int TotalSectors);

  // (tracks, sectorsPerTrack, sides) — standard floppy geometries.
  private static readonly (int T, int S, int H)[] StdGeometries = [
    (35, 9, 1),   // SS/SD 35-track 79 KB
    (40, 9, 1),   // SS/SD 40-track 90 KB
    (35, 9, 2),   // DS/SD 35-track 158 KB
    (40, 9, 2),   // DS/SD 40-track 180 KB
    (40, 18, 1),  // SS/DD 40-track 180 KB
    (40, 18, 2),  // DS/DD 40-track 360 KB
    (80, 9, 2),   // DS/SD 80-track HD 360 KB
    (80, 18, 2),  // DS/DD 80-track HD 720 KB
  ];

  /// <summary>Picks the smallest geometry that fits payload + 2 (VIB+FDIR) +
  /// 1 sector per file (FDR).</summary>
  public static Ti99Geometry Find(System.Collections.Generic.IReadOnlyList<long> fileSizes) {
    System.ArgumentNullException.ThrowIfNull(fileSizes);
    var payloadSectors = 0L;
    foreach (var s in fileSizes) {
      if (s <= 0) continue;
      payloadSectors += (s + Ti99Reader.SectorSize - 1) / Ti99Reader.SectorSize;
    }
    var fdrSectors = fileSizes.Count;
    var neededSectors = 2 + fdrSectors + payloadSectors;

    foreach (var (t, s, h) in StdGeometries) {
      var total = (long)t * s * h;
      if (total >= neededSectors)
        return new Ti99Geometry(t, s, h, (int)total);
    }
    // Fallback: largest in the table.
    var (lt, ls, lh) = StdGeometries[^1];
    return new Ti99Geometry(lt, ls, lh, lt * ls * lh);
  }
}
