#pragma warning disable CS1591
namespace FileSystem.Gemdos;

/// <summary>
/// Fileset-driven sector / cluster geometry picker for GEMDOS images. Returns
/// the smallest BytesPerSector × SectorsPerCluster combination whose total
/// slack waste is ≤ 5% of the total file payload, tiebreaking toward the
/// bigger cluster (better FAT-table efficiency). The TotalSectors result is
/// the smallest standard Atari size (360 KB / 720 KB / 1.44 MB / 2.88 MB)
/// that fits all files plus reserved + FAT + root-directory overhead.
/// </summary>
public static class GemdosOptimizer {

  /// <summary>Result of a geometry pick. Pass these straight into
  /// <see cref="GemdosWriter.Build"/>.</summary>
  public sealed record GemdosGeometry(
      int BytesPerSector,
      int SectorsPerCluster,
      int TotalSectors,
      int RootEntries);

  // Standard Atari ST floppy sizes (total sectors at 512 B/sec).
  private static readonly int[] StdSizes = [
       720, // 360 KB single-sided DD
      1440, // 720 KB double-sided DD
      2880, // 1.44 MB DS HD
      5760, // 2.88 MB DS ED
  ];

  /// <summary>
  /// Picks the smallest GEMDOS geometry whose total slack waste is ≤ 5% of the
  /// total file payload, tiebreaking toward bigger clusters. Always returns
  /// 512-byte sectors because all Atari TOS releases support 512 B/sector and
  /// it's the most compatible across emulators and real hardware.
  /// </summary>
  public static GemdosGeometry Find(System.Collections.Generic.IReadOnlyList<long> fileSizes) {
    System.ArgumentNullException.ThrowIfNull(fileSizes);
    const int bps = 512;
    var totalBytes = 0L;
    foreach (var s in fileSizes) totalBytes += System.Math.Max(0, s);

    // Candidate cluster sizes (in sectors) — Atari supports 1, 2, 4.
    var candidates = new System.Collections.Generic.List<GemdosGeometry>();
    foreach (var spc in new[] { 1, 2, 4 }) {
      var clusterBytes = (long)spc * bps;
      // Slack: sum of "round up file to cluster" — file size.
      var slack = 0L;
      foreach (var s in fileSizes) {
        if (s <= 0) continue;
        var rounded = ((s + clusterBytes - 1) / clusterBytes) * clusterBytes;
        slack += rounded - s;
      }
      // Find smallest standard size that fits payload + a generous metadata
      // budget (we pick a safe 16 KB metadata reserve which covers boot+FAT×2+root).
      var needed = totalBytes + slack + 16 * 1024L;
      var sectorsNeeded = (needed + bps - 1) / bps;
      int picked = 0;
      foreach (var size in StdSizes)
        if (size * (long)bps >= sectorsNeeded * bps) { picked = size; break; }
      if (picked == 0) picked = StdSizes[^1];
      var rootEntries = picked <= 1440 ? 112 : 224;

      var slackRatio = totalBytes > 0 ? (double)slack / totalBytes : 0.0;
      if (slackRatio <= 0.05 || candidates.Count == 0)
        candidates.Add(new GemdosGeometry(bps, spc, picked, rootEntries));
    }

    if (candidates.Count == 0)
      return new GemdosGeometry(bps, 2, StdSizes[1], 112);

    // Tiebreak: prefer biggest spc that still meets the ≤ 5% slack rule.
    // Sort by (spc DESC) — biggest first wins.
    candidates.Sort((a, b) => b.SectorsPerCluster.CompareTo(a.SectorsPerCluster));
    return candidates[0];
  }
}
