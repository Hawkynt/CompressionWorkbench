using System.Buffers.Binary;

namespace Compression.Core.DiskImage;

/// <summary>
/// Parses the Sun disk label (SunOS / Solaris SPARC "VTOC" label) in sector 0. Big-endian.
/// </summary>
/// <remarks>
/// <para>
/// The 512-byte label ends in the magic <c>0xDABE</c> at byte 508 and a checksum at 510 chosen
/// so that the XOR of all 256 big-endian 16-bit words is zero. Eight partition records at byte
/// 444 hold a start cylinder and a length in sectors; a cylinder is <c>ntrks × nsect</c>
/// sectors, from the geometry fields at bytes 436 and 438. The VTOC at byte 128 tags each
/// slice (root, swap, usr, whole disk, Linux native, …) and is recognised by its sanity word
/// <c>0x600DDEEE</c>; a label without one still describes its partitions.
/// </para>
/// <para>
/// The whole-disk slice (tag 5, conventionally slice 2) overlaps every other slice and is not
/// listed.
/// </para>
/// References:
/// <list type="bullet">
///   <item><description>Sun Microsystems, <c>sys/dklabel.h</c> — <c>struct dk_label</c> layout, <c>DKL_MAGIC</c>, checksum rule.</description></item>
///   <item><description>Sun Microsystems, <c>sys/vtoc.h</c> — slice tag values (<c>V_BOOT</c>, <c>V_ROOT</c>, <c>V_BACKUP</c>, …) and <c>VTOC_SANE</c>.</description></item>
/// </list>
/// </remarks>
public static class SunVtocParser {

  private const int LabelSize = 512;
  private const ushort Magic = 0xDABE;
  private const int MagicOffset = 508;
  private const uint VtocSane = 0x600DDEEE;
  private const int VtocSanityOffset = 188;
  private const int VtocInfosOffset = 142;
  private const int NtrksOffset = 436;
  private const int NsectOffset = 438;
  private const int PartitionsOffset = 444;
  private const int PartitionCount = 8;
  private const ushort WholeDiskTag = 5;

  /// <summary>Reports whether <paramref name="sector0"/> is a Sun label with a valid checksum.</summary>
  public static bool IsSunLabel(ReadOnlySpan<byte> sector0) {
    if (sector0.Length < LabelSize) return false;
    if (BinaryPrimitives.ReadUInt16BigEndian(sector0[MagicOffset..]) != Magic) return false;
    ushort xor = 0;
    for (var i = 0; i < LabelSize; i += 2)
      xor ^= BinaryPrimitives.ReadUInt16BigEndian(sector0[i..]);
    return xor == 0;
  }

  /// <summary>Parses the label's slices, excluding the whole-disk slice and empty slots.</summary>
  /// <exception cref="InvalidDataException">Sector 0 is not a Sun label.</exception>
  public static List<PartitionEntry> Parse(ReadOnlySpan<byte> sector0) {
    if (!IsSunLabel(sector0)) throw new InvalidDataException("Sector 0 is not a checksummed Sun disk label.");

    var ntrks = BinaryPrimitives.ReadUInt16BigEndian(sector0[NtrksOffset..]);
    var nsect = BinaryPrimitives.ReadUInt16BigEndian(sector0[NsectOffset..]);
    var cylinderSectors = (long)ntrks * nsect;
    var hasVtoc = BinaryPrimitives.ReadUInt32BigEndian(sector0[VtocSanityOffset..]) == VtocSane;

    var result = new List<PartitionEntry>();
    for (var i = 0; i < PartitionCount; ++i) {
      var record = sector0[(PartitionsOffset + i * 8)..];
      var startCylinder = BinaryPrimitives.ReadUInt32BigEndian(record);
      var sectors = BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
      if (sectors == 0) continue;

      var tag = hasVtoc ? BinaryPrimitives.ReadUInt16BigEndian(sector0[(VtocInfosOffset + i * 4)..]) : (ushort)0;
      if (tag == WholeDiskTag || (!hasVtoc && i == 2)) continue;

      result.Add(new PartitionEntry {
        Index = result.Count,
        StartOffset = startCylinder * cylinderSectors * LabelSize,
        Size = (long)sectors * LabelSize,
        TypeName = TagName(tag),
        TypeCode = $"0x{tag:X2}",
        Name = ((char)('a' + i)).ToString(),
        Source = "Sun"
      });
    }
    return result;
  }

  private static string TagName(ushort tag) => tag switch {
    0x00 => "Unassigned",
    0x01 => "Boot",
    0x02 => "SunOS root",
    0x03 => "SunOS swap",
    0x04 => "SunOS usr",
    0x05 => "Whole disk",
    0x06 => "SunOS stand",
    0x07 => "SunOS var",
    0x08 => "SunOS home",
    0x82 => "Linux swap",
    0x83 => "Linux native",
    0x8E => "Linux LVM",
    0xFD => "Linux raid autodetect",
    _ => $"Sun tag 0x{tag:X2}"
  };
}
