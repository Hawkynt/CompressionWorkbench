#pragma warning disable CS1591
namespace FileSystem.Trsdos;

/// <summary>
/// Picks the smallest TRSDOS / LDOS geometry whose data area fits the
/// supplied file set with ≤ 5 % wasted slack. The TRS-80 line shipped
/// only a handful of canonical disk geometries; the optimiser walks
/// them in ascending capacity order.
/// </summary>
public static class TrsdosOptimizer {

  /// <summary>One disk preset.</summary>
  public readonly record struct TrsdosGeometry(string Density, int Tracks, int SectorsPerTrack) {
    /// <summary>Sector size is fixed at 256 bytes by the reader.</summary>
    public int SectorSize => TrsdosReader.SectorSize;
    /// <summary>Total image size in bytes.</summary>
    public int TotalBytes => Tracks * SectorsPerTrack * this.SectorSize;
    /// <summary>Bytes available for file data (track 17 reserved for directory).</summary>
    public int DataBytes => (Tracks - 1) * SectorsPerTrack * this.SectorSize;
    /// <summary>Granules per cylinder (auto-derived from spt; 5 sectors/granule).</summary>
    public int GranulesPerCylinder => Math.Max(1, SectorsPerTrack / 5);
  }

  /// <summary>Canonical TRSDOS / LDOS geometries in ascending data-capacity order.</summary>
  public static readonly IReadOnlyList<TrsdosGeometry> Geometries = [
    // Single density (35 tracks × 10 spt × 256 B = 89 600 B raw).
    new("Single", 35, 10),
    // Double density 40 tracks × 18 spt (Model III/4 classic).
    new("Double", 40, 18),
    // Double density 80 tracks × 18 spt (Model 4 high-track-density).
    new("Double", 80, 18),
  ];

  /// <summary>
  /// Returns the smallest geometry whose data area holds <paramref name="fileSizes"/>
  /// with ≤ 5 % slack. Falls back to the largest geometry if nothing fits cleanly.
  /// </summary>
  public static TrsdosGeometry Find(IReadOnlyList<long> fileSizes) {
    ArgumentNullException.ThrowIfNull(fileSizes);
    var sectorSize = TrsdosReader.SectorSize;
    long need = 0;
    foreach (var s in fileSizes) {
      if (s < 0) continue;
      need += (s + sectorSize - 1) / sectorSize * sectorSize;
    }

    foreach (var g in Geometries) {
      if (g.DataBytes < need) continue;
      if (g.DataBytes == 0) continue;
      var slackPct = (g.DataBytes - need) * 100.0 / g.DataBytes;
      if (slackPct <= 5.0) return g;
    }
    foreach (var g in Geometries)
      if (g.DataBytes >= need) return g;
    return Geometries[^1];
  }
}
