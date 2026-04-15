#pragma warning disable CS1591

namespace Compression.Core.DiskImage;

/// <summary>
/// Detects and parses partition tables from raw disk data.
/// Tries GPT first (since GPT disks also have a protective MBR), then falls back to MBR.
/// </summary>
public static class PartitionTableDetector {

  /// <summary>
  /// Result of partition table detection, including the scheme used and the discovered partitions.
  /// </summary>
  public sealed class DetectionResult {
    /// <summary>The partition scheme detected: "GPT", "MBR", or "None".</summary>
    public required string Scheme { get; init; }

    /// <summary>Discovered partitions. Empty if no partition table was found.</summary>
    public required List<PartitionEntry> Partitions { get; init; }
  }

  /// <summary>Minimum data size needed to attempt detection (GPT header is at offset 512).</summary>
  private const int MinimumSize = 1024;

  /// <summary>
  /// Attempts to detect a partition table in the given stream.
  /// Tries GPT first (higher precedence), then MBR.
  /// Returns an empty partition list if no valid partition table is found.
  /// </summary>
  /// <param name="diskData">A seekable stream containing raw disk image data.</param>
  /// <returns>Detection result with scheme name and partitions.</returns>
  public static DetectionResult Detect(Stream diskData) {
    if (!diskData.CanSeek || diskData.Length < MinimumSize)
      return new DetectionResult { Scheme = "None", Partitions = [] };

    // Read the first 1024 bytes for quick signature checks.
    var header = new byte[Math.Min(4096, (int)diskData.Length)];
    diskData.Position = 0;
    var bytesRead = diskData.Read(header, 0, header.Length);
    diskData.Position = 0;

    if (bytesRead < MinimumSize)
      return new DetectionResult { Scheme = "None", Partitions = [] };

    // Try GPT first (GPT disks have a protective MBR, so GPT takes priority).
    if (GptParser.IsGpt(header.AsSpan(0, bytesRead))) {
      try {
        var gptPartitions = GptParser.Parse(diskData);
        if (gptPartitions.Count > 0)
          return new DetectionResult { Scheme = "GPT", Partitions = gptPartitions };
      } catch {
        // GPT parsing failed — fall through to MBR.
      }
    }

    // Try MBR.
    if (MbrParser.IsMbr(header.AsSpan(0, bytesRead))) {
      try {
        var mbrPartitions = MbrParser.Parse(diskData);
        // Filter out partitions that reference data beyond the stream.
        var validPartitions = mbrPartitions
          .Where(p => p.StartOffset >= 0 && p.StartOffset < diskData.Length && p.Size > 0)
          .ToList();
        if (validPartitions.Count > 0)
          return new DetectionResult { Scheme = "MBR", Partitions = validPartitions };
      } catch {
        // MBR parsing failed — no partition table.
      }
    }

    return new DetectionResult { Scheme = "None", Partitions = [] };
  }

  /// <summary>
  /// Attempts to detect a partition table in the given byte array.
  /// Convenience overload that wraps the data in a <see cref="MemoryStream"/>.
  /// </summary>
  /// <param name="diskData">Raw disk image data.</param>
  /// <returns>Detection result with scheme name and partitions.</returns>
  public static DetectionResult Detect(byte[] diskData) {
    using var ms = new MemoryStream(diskData, writable: false);
    return Detect(ms);
  }

  /// <summary>
  /// Extracts partition data from a disk image stream.
  /// Returns the raw bytes of the partition at the given offset and size.
  /// </summary>
  /// <param name="diskData">A seekable stream containing the full disk image.</param>
  /// <param name="partition">The partition entry describing offset and size.</param>
  /// <returns>A byte array containing the partition's raw data, or empty if out of range.</returns>
  public static byte[] ExtractPartitionData(Stream diskData, PartitionEntry partition) {
    if (partition.StartOffset < 0 || partition.StartOffset >= diskData.Length || partition.Size <= 0)
      return [];

    var actualSize = (int)Math.Min(partition.Size, diskData.Length - partition.StartOffset);
    if (actualSize <= 0)
      return [];

    var data = new byte[actualSize];
    diskData.Position = partition.StartOffset;
    var read = diskData.Read(data, 0, actualSize);
    return read < actualSize ? data[..read] : data;
  }

  /// <summary>
  /// Extracts partition data from a byte array.
  /// </summary>
  /// <param name="diskData">Raw disk image data.</param>
  /// <param name="partition">The partition entry describing offset and size.</param>
  /// <returns>A byte array containing the partition's raw data, or empty if out of range.</returns>
  public static byte[] ExtractPartitionData(byte[] diskData, PartitionEntry partition) {
    if (partition.StartOffset < 0 || partition.StartOffset >= diskData.Length || partition.Size <= 0)
      return [];

    var offset = (int)partition.StartOffset;
    var size = (int)Math.Min(partition.Size, diskData.Length - offset);
    return size <= 0 ? [] : diskData.AsSpan(offset, size).ToArray();
  }
}
