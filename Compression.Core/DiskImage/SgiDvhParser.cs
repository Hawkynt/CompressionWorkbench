using System.Buffers.Binary;

namespace Compression.Core.DiskImage;

/// <summary>
/// Parses the SGI disk volume header (DVH) in sector 0 — the IRIX partition scheme. Big-endian.
/// </summary>
/// <remarks>
/// <para>
/// The header opens with the magic <c>0x0BE5A941</c> and carries a checksum chosen so that the
/// 32-bit sum of all 128 big-endian longwords of the 512-byte sector is zero. Sixteen partition
/// records at byte 312 each hold a length and a first block (512-byte blocks) and a type. The
/// volume partition (type 6, conventionally slot 10) covers the whole disk and is not listed;
/// the volume-header partition (type 0) is, since it holds the boot files.
/// </para>
/// References:
/// <list type="bullet">
///   <item><description>Silicon Graphics, IRIX <c>sys/dvh.h</c> — <c>struct volume_header</c>, <c>VHMAGIC</c>, partition types, checksum rule.</description></item>
/// </list>
/// </remarks>
public static class SgiDvhParser {

  private const int LabelSize = 512;
  private const uint Magic = 0x0BE5A941;
  private const int PartitionsOffset = 312;
  private const int PartitionCount = 16;
  private const int PartitionRecordSize = 12;
  private const uint VolumeType = 6;

  /// <summary>Reports whether <paramref name="sector0"/> is an SGI volume header with a valid checksum.</summary>
  public static bool IsDvh(ReadOnlySpan<byte> sector0) {
    if (sector0.Length < LabelSize || BinaryPrimitives.ReadUInt32BigEndian(sector0) != Magic) return false;
    uint sum = 0;
    for (var i = 0; i < LabelSize; i += 4)
      sum += BinaryPrimitives.ReadUInt32BigEndian(sector0[i..]);
    return sum == 0;
  }

  /// <summary>Parses the header's partitions, excluding the whole-volume partition and empty slots.</summary>
  /// <exception cref="InvalidDataException">Sector 0 is not an SGI volume header.</exception>
  public static List<PartitionEntry> Parse(ReadOnlySpan<byte> sector0) {
    if (!IsDvh(sector0)) throw new InvalidDataException("Sector 0 is not a checksummed SGI volume header.");

    var result = new List<PartitionEntry>();
    for (var i = 0; i < PartitionCount; ++i) {
      var record = sector0[(PartitionsOffset + i * PartitionRecordSize)..];
      var blocks = BinaryPrimitives.ReadUInt32BigEndian(record);
      var first = BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
      var type = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
      if (blocks == 0 || type == VolumeType) continue;

      result.Add(new PartitionEntry {
        Index = result.Count,
        StartOffset = (long)first * LabelSize,
        Size = (long)blocks * LabelSize,
        TypeName = TypeName(type),
        TypeCode = $"0x{type:X2}",
        Name = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Source = "SGI"
      });
    }
    return result;
  }

  private static string TypeName(uint type) => type switch {
    0x00 => "SGI volhdr",
    0x01 => "SGI trkrepl",
    0x02 => "SGI secrepl",
    0x03 => "SGI raw",
    0x04 => "SGI bsd",
    0x05 => "SGI sysv",
    0x06 => "SGI volume",
    0x07 => "SGI efs",
    0x08 => "SGI lvol",
    0x09 => "SGI rlvol",
    0x0A => "SGI xfs",
    0x0B => "SGI xfslog",
    0x0C => "SGI xlv",
    0x0D => "SGI xvm",
    0x82 => "Linux swap",
    0x83 => "Linux native",
    0x8E => "Linux LVM",
    0xFD => "Linux RAID",
    _ => $"SGI type 0x{type:X2}"
  };
}
