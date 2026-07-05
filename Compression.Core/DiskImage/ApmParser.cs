using System.Buffers.Binary;
using System.Text;

namespace Compression.Core.DiskImage;

/// <summary>
/// Parses the Apple Partition Map (APM) as used on classic Mac OS 68k/PowerPC
/// media and early Intel-Mac install images. The scheme is big-endian throughout.
///
/// <para>Block 0 optionally holds a Driver Descriptor Record (DDR) whose signature
/// is <c>ER</c> (<c>0x4552</c>); its <c>sbBlkSize</c> field names the media block
/// size (512 or 2048). The partition map itself begins at block 1: every entry is
/// one block, tagged with the <c>PM</c> (<c>0x504D</c>) signature, and its
/// <c>pmMapBlkCnt</c> field states how many entries the map contains.</para>
///
/// <para>Only real partitions are enumerated; the map's self-descriptor
/// (<c>Apple_partition_map</c>) and free-space runs (<c>Apple_Free</c>) are
/// skipped. Partition types are surfaced verbatim (e.g. <c>Apple_HFS</c>,
/// <c>Apple_HFSX</c>, <c>Apple_UFS</c>).</para>
///
/// References:
/// <list type="bullet">
///   <item><description>Apple, "Inside Macintosh: Devices" — chapter 3, "SCSI Manager", partition-map layout (DDR + <c>Partition</c> structure).</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Apple_Partition_Map</c> — field-level overview.</description></item>
/// </list>
/// </summary>
public static class ApmParser {

  /// <summary>DDR signature at block 0: ASCII "ER".</summary>
  private const ushort DdrSignature = 0x4552;

  /// <summary>Partition-map entry signature: ASCII "PM".</summary>
  private const ushort PartitionSignature = 0x504D;

  /// <summary>Block sizes the parser understands.</summary>
  private const int BlockSize512 = 512;
  private const int BlockSize2048 = 2048;

  /// <summary>Upper bound on partition-map entries to guard against corrupt headers.</summary>
  private const int MaxEntries = 4096;

  /// <summary>
  /// Checks whether the given header carries an Apple Partition Map. The check
  /// looks for the <c>PM</c> entry signature at block 1 for both supported block
  /// sizes, honouring a DDR block size where one is present.
  /// </summary>
  /// <param name="data">Disk header bytes starting at block 0 (at least one block).</param>
  /// <returns><c>true</c> if a partition-map entry signature is present at block 1.</returns>
  public static bool IsApm(ReadOnlySpan<byte> data)
    => DetectBlockSize(data) > 0;

  /// <summary>
  /// Parses every real partition from an APM disk image.
  /// </summary>
  /// <param name="diskData">The full disk image as a seekable, readable stream.</param>
  /// <returns>Enumerated partitions, excluding <c>Apple_Free</c> and the map self-descriptor.</returns>
  public static List<PartitionEntry> Parse(Stream diskData) {
    var probe = new byte[Math.Min(BlockSize2048 * 2, (int)Math.Max(0, diskData.Length))];
    diskData.Position = 0;
    _ = diskData.Read(probe, 0, probe.Length);

    var blockSize = DetectBlockSize(probe);
    if (blockSize <= 0)
      throw new InvalidDataException("No Apple Partition Map signature found at block 1.");

    var result = new List<PartitionEntry>();
    var entryBuf = new byte[blockSize];

    // The first entry's pmMapBlkCnt states the total number of map entries.
    long entryCount = -1;
    var index = 0;

    for (var i = 0; entryCount < 0 || i < entryCount; ++i) {
      if (i > MaxEntries) break;
      var entryOffset = (long)(1 + i) * blockSize;
      if (entryOffset + blockSize > diskData.Length) break;

      diskData.Position = entryOffset;
      if (!ReadExact(diskData, entryBuf)) break;

      var span = entryBuf.AsSpan();
      if (BinaryPrimitives.ReadUInt16BigEndian(span) != PartitionSignature)
        break; // end of a shorter-than-declared map

      var mapBlkCnt = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
      if (entryCount < 0)
        entryCount = mapBlkCnt == 0 ? 0 : Math.Min(mapBlkCnt, MaxEntries);

      var pyPartStart = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
      var partBlkCnt = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
      var name = ReadFixedString(span.Slice(16, 32));
      var type = ReadFixedString(span.Slice(48, 32));

      // Skip the map's own descriptor and free-space runs — not real partitions.
      if (type.Equals("Apple_partition_map", StringComparison.OrdinalIgnoreCase)
          || type.Equals("Apple_Free", StringComparison.OrdinalIgnoreCase)
          || partBlkCnt == 0)
        continue;

      result.Add(new PartitionEntry {
        Index = index++,
        StartOffset = (long)pyPartStart * blockSize,
        Size = (long)partBlkCnt * blockSize,
        TypeName = string.IsNullOrEmpty(type) ? "Apple_Unknown" : type,
        TypeCode = type,
        Name = name,
        Source = "APM"
      });
    }

    return result;
  }

  /// <summary>
  /// Determines the APM block size from a disk header, or 0 if the header does
  /// not describe an Apple Partition Map. A DDR block size is honoured when
  /// valid; otherwise the block-1 <c>PM</c> signature is probed for 512 then 2048.
  /// </summary>
  private static int DetectBlockSize(ReadOnlySpan<byte> data) {
    // Honour an explicit DDR block size when the descriptor is present and sane.
    if (data.Length >= 4 && BinaryPrimitives.ReadUInt16BigEndian(data) == DdrSignature) {
      var ddrBlkSize = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
      if (ddrBlkSize is BlockSize512 or BlockSize2048 && HasPartitionSignature(data, ddrBlkSize))
        return ddrBlkSize;
    }

    if (HasPartitionSignature(data, BlockSize512)) return BlockSize512;
    if (HasPartitionSignature(data, BlockSize2048)) return BlockSize2048;
    return 0;
  }

  private static bool HasPartitionSignature(ReadOnlySpan<byte> data, int blockSize)
    => data.Length >= blockSize + 2
       && BinaryPrimitives.ReadUInt16BigEndian(data[blockSize..]) == PartitionSignature;

  private static string ReadFixedString(ReadOnlySpan<byte> field) {
    var end = field.IndexOf((byte)0);
    if (end < 0) end = field.Length;
    return Encoding.ASCII.GetString(field[..end]).TrimEnd();
  }

  private static bool ReadExact(Stream stream, Span<byte> buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var n = stream.Read(buffer[read..]);
      if (n <= 0) return false;
      read += n;
    }
    return true;
  }
}
