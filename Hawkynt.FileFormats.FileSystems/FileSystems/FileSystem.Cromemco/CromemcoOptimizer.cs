#pragma warning disable CS1591
namespace FileSystem.Cromemco;

/// <summary>
/// Picks the smallest Cromemco RDOS geometry whose data area fits a
/// supplied file set with ≤ 5 % wasted slack. The Cromemco RDOS line
/// shipped a small handful of well-defined disk geometries; the
/// optimiser walks them in ascending capacity order and returns the
/// first that fits.
/// </summary>
public static class CromemcoOptimizer {

  /// <summary>One disk preset: density label + track count + sectors/track.</summary>
  public readonly record struct CromemcoGeometry(string Density, int Tracks, int SectorsPerTrack) {
    /// <summary>Sector size is fixed at 128 bytes by the reader.</summary>
    public int SectorSize => CromemcoReader.SectorSize;
    /// <summary>Total image bytes.</summary>
    public int TotalBytes => Tracks * SectorsPerTrack * this.SectorSize;
    /// <summary>Bytes available for file data (after boot + directory).</summary>
    public int DataBytes => (Tracks * SectorsPerTrack - CromemcoWriter.FirstDataSector) * this.SectorSize;
  }

  /// <summary>Canonical Cromemco RDOS geometries, in ascending capacity order.</summary>
  public static readonly IReadOnlyList<CromemcoGeometry> Geometries = [
    // Single density (35 tracks, 18 spt, 128-byte sector → 80 640 bytes raw).
    new("Single", 35, 18),
    // Single density on the 77-track System Three drives.
    new("Single", 77, 18),
    // Double density (26 spt) on the System Three.
    new("Double", 77, 26),
  ];

  /// <summary>
  /// Returns the smallest geometry whose data area holds <paramref name="fileSizes"/>
  /// with at most 5 % slack. Sums each file rounded up to the 128-byte sector boundary
  /// (since the writer always allocates whole sectors). Falls back to the largest
  /// geometry when nothing fits cleanly.
  /// </summary>
  public static CromemcoGeometry Find(IReadOnlyList<long> fileSizes) {
    ArgumentNullException.ThrowIfNull(fileSizes);
    var sectorSize = CromemcoReader.SectorSize;
    long need = 0;
    foreach (var s in fileSizes) {
      if (s < 0) continue;
      need += (s + sectorSize - 1) / sectorSize * sectorSize;
    }

    foreach (var g in Geometries) {
      if (g.DataBytes < need) continue;
      var slack = g.DataBytes - need;
      if (g.DataBytes == 0) continue;
      var slackPct = slack * 100.0 / g.DataBytes;
      if (slackPct <= 5.0) return g;
    }
    // Nothing fits with ≤ 5 % slack — return the smallest geometry that fits at all.
    foreach (var g in Geometries)
      if (g.DataBytes >= need) return g;
    // Doesn't fit anywhere — return the largest as best effort.
    return Geometries[^1];
  }
}
