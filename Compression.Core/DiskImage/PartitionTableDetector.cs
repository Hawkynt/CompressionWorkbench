#pragma warning disable CS1591

namespace Compression.Core.DiskImage;

/// <summary>
/// Detects and parses partition tables from raw disk data.
/// </summary>
/// <remarks>
/// Probe order, first hit wins:
/// <list type="number">
///   <item><description>GPT, at 512- or 4096-byte logical blocks — ahead of the MBR, because a GPT
///     disk carries a protective (or hybrid) MBR too;</description></item>
///   <item><description>SGI volume header and Sun label — sector-0 labels with a magic and a
///     checksum, so they are certain when they match, and their last bytes could otherwise pass
///     for an MBR boot signature;</description></item>
///   <item><description>MBR, with its extended/logical (EBR) chain;</description></item>
///   <item><description>Apple Partition Map;</description></item>
///   <item><description>a stand-alone BSD disklabel (no MBR around it), both magics and its geometry verified;</description></item>
///   <item><description>an Amiga Rigid Disk Block in the first 16 blocks, checksum verified.</description></item>
/// </list>
/// </remarks>
public static class PartitionTableDetector {

  /// <summary>
  /// Result of partition table detection, including the scheme used and the discovered partitions.
  /// </summary>
  public sealed class DetectionResult {
    /// <summary>The partition scheme detected: "GPT", "MBR", "APM", "BSD", "RDB", "Sun", "SGI", or "None".</summary>
    public required string Scheme { get; init; }

    /// <summary>Discovered partitions. Empty if no partition table was found.</summary>
    public required List<PartitionEntry> Partitions { get; init; }

    /// <summary>Logical block size the table counts in (512 unless a 4Kn GPT was found).</summary>
    public int SectorSize { get; init; } = 512;
  }

  /// <summary>Minimum data size needed to attempt detection (GPT header is at offset 512).</summary>
  private const int MinimumSize = 1024;

  private static DetectionResult None => new() { Scheme = "None", Partitions = [] };

  /// <summary>
  /// Attempts to detect a partition table in the given stream.
  /// Returns an empty partition list if no valid partition table is found.
  /// </summary>
  /// <param name="diskData">A seekable stream containing raw disk image data.</param>
  /// <returns>Detection result with scheme name and partitions.</returns>
  public static DetectionResult Detect(Stream diskData) {
    if (!diskData.CanSeek || diskData.Length < MinimumSize)
      return None;

    // Enough to see LBA 1 at every supported block size, and an APM map at 2048-byte blocks.
    var header = new byte[(int)Math.Min(GptParser.ProbeBytes, diskData.Length)];
    diskData.Position = 0;
    var bytesRead = diskData.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
    diskData.Position = 0;

    if (bytesRead < MinimumSize)
      return None;
    var head = header.AsSpan(0, bytesRead);

    // GPT first (GPT disks have a protective MBR, so GPT takes priority).
    if (GptParser.IsGpt(head) || HasProtectiveEntry(head)) {
      try {
        var gptPartitions = GptParser.Parse(diskData, out var info);
        if (gptPartitions.Count > 0)
          return new DetectionResult { Scheme = "GPT", Partitions = gptPartitions, SectorSize = info.SectorSize };
      } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException) {
        // GPT parsing failed — fall through to MBR.
      }
    }

    if (SgiDvhParser.IsDvh(head))
      return Bounded("SGI", SgiDvhParser.Parse(head), diskData.Length);
    if (SunVtocParser.IsSunLabel(head))
      return Bounded("Sun", SunVtocParser.Parse(head), diskData.Length);

    // Try MBR.
    if (MbrParser.IsMbr(head)) {
      try {
        // A protective entry describes the GPT, not data; with no readable GPT it holds nothing.
        var mbrPartitions = MbrParser.Parse(diskData).Where(p => p.TypeCode != "0xEE").ToList();
        var result = Bounded("MBR", mbrPartitions, diskData.Length);
        if (result.Partitions.Count > 0)
          return result;
      } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) {
        // MBR parsing failed — fall through to APM.
      }
    }

    // Try APM (Apple Partition Map). APM disks carry neither an MBR boot
    // signature nor a GPT header, so this is reached only when both above fail.
    if (ApmParser.IsApm(head)) {
      try {
        var apm = Bounded("APM", ApmParser.Parse(diskData), diskData.Length);
        if (apm.Partitions.Count > 0)
          return apm;
      } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) {
        // APM parsing failed — try the remaining schemes.
      }
    }

    try {
      if (BsdDisklabelParser.IsStandaloneDisklabel(diskData)) {
        var bsd = Bounded("BSD", BsdDisklabelParser.Parse(diskData, 0), diskData.Length);
        if (bsd.Partitions.Count > 0)
          return bsd;
      }
      if (AmigaRdbParser.IsRdb(diskData)) {
        var rdb = Bounded("RDB", AmigaRdbParser.Parse(diskData), diskData.Length);
        if (rdb.Partitions.Count > 0)
          return rdb;
      }
    } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) {
      // An unreadable label holds nothing.
    } finally {
      diskData.Position = 0;
    }

    return None;
  }

  /// <summary>
  /// Whether the first sector holds a partition table of a scheme that has no MBR-style
  /// boot signature to go on — SGI, Sun, a stand-alone BSD disklabel or an Amiga RDB — each
  /// verified by its own checksum (BSD: by its two magics and geometry). Used by content detection, which has already ruled out
  /// GPT, MBR and APM by then.
  /// </summary>
  public static bool HasSelfValidatingLabel(Stream disk) {
    if (!disk.CanSeek || disk.Length < MinimumSize) return false;
    var saved = disk.Position;
    try {
      var sector0 = new byte[512];
      disk.Position = 0;
      if (disk.ReadAtLeast(sector0, 512, throwOnEndOfStream: false) < 512) return false;
      return SgiDvhParser.IsDvh(sector0) || SunVtocParser.IsSunLabel(sector0)
             || BsdDisklabelParser.IsStandaloneDisklabel(disk) || AmigaRdbParser.IsRdb(disk);
    } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) {
      return false;
    } finally {
      disk.Position = saved;
    }
  }

  private static bool HasProtectiveEntry(ReadOnlySpan<byte> head) {
    if (!MbrParser.IsMbr(head)) return false;
    for (var i = 0; i < 4; ++i)
      if (head[0x1BE + i * 16 + 4] == 0xEE) return true;
    return false;
  }

  /// <summary>Keeps only partitions that start inside the image and have a size.</summary>
  private static DetectionResult Bounded(string scheme, List<PartitionEntry> partitions, long length) => new() {
    Scheme = scheme,
    Partitions = partitions.Where(p => p.StartOffset >= 0 && p.StartOffset < length && p.Size > 0).ToList(),
  };

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
