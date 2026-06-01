#pragma warning disable CS1591
namespace FileSystem.Pc98;

/// <summary>
/// Picks the smallest sectors-per-cluster value for a PC-98 disk that
/// fits the supplied file set with ≤ 5 % wasted slack. Sector size is
/// fixed at 512 B by default.
/// </summary>
public static class Pc98Optimizer {

  /// <summary>One layout preset.</summary>
  public readonly record struct Pc98Layout(int BytesPerSector, int SectorsPerCluster, int TotalSectors) {
    /// <summary>Bytes per cluster.</summary>
    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;
    /// <summary>Total raw image size.</summary>
    public int TotalBytes => BytesPerSector * TotalSectors;
  }

  /// <summary>
  /// Returns the smallest sectors-per-cluster whose cluster-aligned footprint
  /// is within 5 % of the payload size; falls back to SPC=1 if no candidate
  /// satisfies the slack threshold. TotalSectors is sized to fit IPL block +
  /// 1 reserved + 1 FAT + 32-entry root + clusters.
  /// </summary>
  public static Pc98Layout Find(IReadOnlyList<long> fileSizes) {
    ArgumentNullException.ThrowIfNull(fileSizes);
    const int bytesPerSector = 512;
    long payload = 0;
    foreach (var s in fileSizes)
      if (s >= 0) payload += s;

    var bestSpc = 1;
    foreach (var spc in new[] { 1, 2, 4, 8, 16 }) {
      var bpc = bytesPerSector * spc;
      var clusters = 0L;
      foreach (var s in fileSizes)
        if (s > 0) clusters += (s + bpc - 1) / bpc;
      var allocated = clusters * bpc;
      if (allocated == 0) { bestSpc = 1; continue; }
      var slackPct = (allocated - payload) * 100.0 / allocated;
      if (slackPct <= 5.0) { bestSpc = spc; break; }
    }

    var bpcOut = bytesPerSector * bestSpc;
    var clustersOut = 0L;
    foreach (var s in fileSizes)
      if (s > 0) clustersOut += (s + bpcOut - 1) / bpcOut;
    if (clustersOut == 0) clustersOut = 1;

    const int iplSectors = 1;
    const int reservedSectors = 1;
    const int fatCount = 1;
    const int rootEntries = 32;
    var rootDirSectors = (rootEntries * 32 + bytesPerSector - 1) / bytesPerSector;
    var fatBytes = (int)((2 + clustersOut) * 3 / 2 + 1);
    var sectorsForFat = Math.Max(1, (fatBytes + bytesPerSector - 1) / bytesPerSector);
    var metadataSectors = iplSectors + reservedSectors + fatCount * sectorsForFat + rootDirSectors;
    var dataSectors = (int)(clustersOut * bestSpc);
    var total = Math.Max(metadataSectors + dataSectors, 16);
    return new Pc98Layout(bytesPerSector, bestSpc, total);
  }
}
