#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Fat;

/// <summary>
/// Shrinks a FAT filesystem image in place by defragmenting (consolidate at start)
/// and then trimming the unused tail through <see cref="FatInPlaceShrinker"/>. The
/// FAT type, label, serial, attributes and timestamps are kept.
/// </summary>
public static class FatShrinkHelper {

  /// <summary>
  /// Result of a FAT shrink operation: original and new sizes, plus whether the
  /// image was actually reduced.
  /// </summary>
  public sealed record ShrinkResult(long OriginalSize, long NewSize, bool WasReduced);

  /// <summary>
  /// Result of a cluster-size analysis: per-cluster-size slack stats plus a recommendation.
  /// </summary>
  public sealed record ClusterHintResult(
    int CurrentClusterSize,
    double CurrentSlackPercent,
    int RecommendedClusterSize,
    double RecommendedSlackPercent,
    IReadOnlyList<ClusterSizeStats> AllStats);

  /// <summary>Per-cluster-size slack computation.</summary>
  public sealed record ClusterSizeStats(int ClusterSize, long TotalSlack, long TotalAllocated, double SlackPercent);

  /// <summary>
  /// Defragments (consolidate at start, in place) then trims trailing free space from a
  /// FAT image, never below the cluster count its FAT type requires.
  /// </summary>
  /// <param name="image">Readable/writable/seekable stream containing the FAT image.</param>
  /// <returns>Shrink result with before/after sizes.</returns>
  /// <exception cref="InvalidDataException">If the stream does not contain a valid FAT image.</exception>
  public static ShrinkResult Shrink(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var originalSize = image.Length;

    // Pack all files at the start, in place; a layout the planner cannot reach in
    // place is simply not packed further (nothing is rebuilt).
    try {
      new FatFormatDescriptor().Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
    } catch (NotSupportedException) {
      // The trim below still takes whatever tail is free.
    }

    var newLength = FatInPlaceShrinker.ShrinkToFit(image);
    return new ShrinkResult(originalSize, newLength, newLength < originalSize);
  }

  /// <summary>
  /// Analyzes a FAT image and computes slack waste at various cluster sizes.
  /// Returns a recommendation for the cluster size that minimizes slack.
  /// </summary>
  public static ClusterHintResult AnalyzeClusterSizes(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var reader = new FatReader(image);

    // Gather all file sizes
    var fileSizes = reader.Entries
      .Where(e => !e.IsDirectory && e.Size > 0)
      .Select(e => e.Size)
      .ToList();

    // Read current cluster size from BPB
    image.Position = 0;
    Span<byte> bpb = stackalloc byte[64];
    image.ReadExactly(bpb);
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bpb[11..]);
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;
    var sectorsPerCluster = bpb[13];
    if (sectorsPerCluster == 0) sectorsPerCluster = 1;
    var currentClusterSize = bytesPerSector * sectorsPerCluster;

    // Candidate cluster sizes: 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536
    var candidates = new[] { 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 };
    var stats = new List<ClusterSizeStats>(candidates.Length);

    foreach (var cs in candidates) {
      var totalSlack = 0L;
      var totalAllocated = 0L;
      foreach (var size in fileSizes) {
        var clusters = (size + cs - 1) / cs;
        var allocated = clusters * cs;
        totalAllocated += allocated;
        totalSlack += allocated - size;
      }
      var slackPct = totalAllocated > 0 ? 100.0 * totalSlack / totalAllocated : 0;
      stats.Add(new ClusterSizeStats(cs, totalSlack, totalAllocated, slackPct));
    }

    var currentStats = stats.FirstOrDefault(s => s.ClusterSize == currentClusterSize)
      ?? ComputeSlackForSize(fileSizes, currentClusterSize);
    var bestStats = stats.MinBy(s => s.SlackPercent)!;

    return new ClusterHintResult(
      currentClusterSize,
      currentStats.SlackPercent,
      bestStats.ClusterSize,
      bestStats.SlackPercent,
      stats);
  }

  private static ClusterSizeStats ComputeSlackForSize(List<long> fileSizes, int clusterSize) {
    var totalSlack = 0L;
    var totalAllocated = 0L;
    foreach (var size in fileSizes) {
      var clusters = (size + clusterSize - 1) / clusterSize;
      var allocated = clusters * clusterSize;
      totalAllocated += allocated;
      totalSlack += allocated - size;
    }
    var slackPct = totalAllocated > 0 ? 100.0 * totalSlack / totalAllocated : 0;
    return new ClusterSizeStats(clusterSize, totalSlack, totalAllocated, slackPct);
  }

  private static int ReadFatEntry(byte[] data, int fatOffset, int cluster, int fatType) {
    if (fatType == 12) {
      var bytePos = fatOffset + cluster * 3 / 2;
      if (bytePos + 2 > data.Length) return 0;
      var val = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(bytePos));
      return (cluster & 1) != 0 ? val >> 4 : val & 0xFFF;
    }
    if (fatType == 16) {
      var pos = fatOffset + cluster * 2;
      if (pos + 2 > data.Length) return 0;
      return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
    }
    // FAT32
    var pos32 = fatOffset + cluster * 4;
    if (pos32 + 4 > data.Length) return 0;
    return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos32)) & 0x0FFFFFFF;
  }
}
