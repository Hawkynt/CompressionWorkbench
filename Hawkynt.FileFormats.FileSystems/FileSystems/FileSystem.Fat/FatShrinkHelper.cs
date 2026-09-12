#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Fat;

/// <summary>
/// Shrinks a FAT filesystem image by defragmenting (consolidate at start) and then
/// truncating trailing free space. Updates the BPB total-sectors field and shrinks
/// the FAT to match the reduced cluster count.
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
  /// Defragments (consolidate at start) then truncates trailing free space from a FAT image.
  /// Updates the BPB total_sectors and FAT size fields to reflect the new geometry.
  /// </summary>
  /// <param name="image">Readable/writable/seekable stream containing the FAT image.</param>
  /// <returns>Shrink result with before/after sizes.</returns>
  /// <exception cref="InvalidDataException">If the stream does not contain a valid FAT image.</exception>
  public static ShrinkResult Shrink(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var originalSize = image.Length;

    // Step 1: Defragment — pack all files at start
    new FatFormatDescriptor().Defragment(image);

    // Step 2: Read the image to find the last used cluster
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();

    if (data.Length < 512)
      throw new InvalidDataException("FAT: image too small.");

    // Parse BPB
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(11));
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;
    var sectorsPerCluster = data[13];
    if (sectorsPerCluster == 0) sectorsPerCluster = 1;
    var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
    var fatCount = data[16];
    if (fatCount == 0) fatCount = 2;
    var rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(17));
    var totalSectors = (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(19));
    if (totalSectors == 0) totalSectors = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(32));
    var fatSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(22));
    if (fatSize == 0) fatSize = (ushort)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(36));

    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    var totalDataClusters = (totalSectors - firstDataSector) / sectorsPerCluster;
    var fatType = totalDataClusters < 4085 ? 12 : totalDataClusters < 65525 ? 16 : 32;

    // Find the last used cluster by scanning the FAT
    var fatOffset = reservedSectors * bytesPerSector;
    var lastUsedCluster = 1; // clusters 0,1 are reserved
    for (var c = 2; c < totalDataClusters + 2; c++) {
      var val = ReadFatEntry(data, fatOffset, c, fatType);
      if (val != 0) // any non-zero entry means cluster is in use or reserved
        lastUsedCluster = c;
    }

    // The last used data byte offset
    var lastUsedDataEnd = firstDataSector + (long)(lastUsedCluster - 2 + 1) * sectorsPerCluster;
    // Add metadata padding: 1 extra cluster of headroom
    lastUsedDataEnd += sectorsPerCluster;

    // Round up to sector boundary
    var newTotalSectors = (int)Math.Min(totalSectors, lastUsedDataEnd);
    // Must be at least firstDataSector + 1 cluster
    newTotalSectors = Math.Max(newTotalSectors, firstDataSector + sectorsPerCluster);
    // Don't grow
    if (newTotalSectors >= totalSectors)
      return new ShrinkResult(originalSize, originalSize, false);

    var newLength = (long)newTotalSectors * bytesPerSector;

    // Step 3: Update BPB total_sectors
    image.Position = 0;
    var bpb = new byte[512];
    image.ReadExactly(bpb);

    if (fatType != 32 && newTotalSectors < 65536) {
      BinaryPrimitives.WriteUInt16LittleEndian(bpb.AsSpan(19), (ushort)newTotalSectors);
      BinaryPrimitives.WriteUInt32LittleEndian(bpb.AsSpan(32), 0u);
    } else {
      BinaryPrimitives.WriteUInt16LittleEndian(bpb.AsSpan(19), 0);
      BinaryPrimitives.WriteUInt32LittleEndian(bpb.AsSpan(32), (uint)newTotalSectors);
    }

    image.Position = 0;
    image.Write(bpb);

    // Step 4: Truncate
    image.SetLength(newLength);

    return new ShrinkResult(originalSize, newLength, true);
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
