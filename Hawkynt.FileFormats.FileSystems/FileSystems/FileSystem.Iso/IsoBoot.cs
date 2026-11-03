#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Iso;

/// <summary>One El Torito boot entry as found in an image's boot catalog.</summary>
public sealed record IsoBootEntry(byte Platform, byte MediaType, ushort LoadSegment, byte SystemType, ushort SectorCount, uint LoadRba);

/// <summary>
/// Boot information of an existing ISO image, so an edit can keep it booting: its El Torito
/// entries (El Torito Bootable CD-ROM Format Specification 1.0), the catalog location, and
/// whether the image is "hybrid" (bootable from a USB stick through a partition table in the
/// system area).
/// </summary>
public static class IsoBoot {
  private const int SectorSize = 2048;

  /// <summary>The boot entries and the catalog sector, or null for an image that does not boot.</summary>
  public static (List<IsoBootEntry> Entries, uint CatalogLba)? ReadElTorito(Stream s) {
    var buf = new byte[SectorSize];
    for (var sector = 16; sector < 64; sector++) {
      if (!ReadSector(s, sector, buf)) return null;
      if (buf[0] == 0xFF) return null;
      if (buf[0] != 0 || Encoding.ASCII.GetString(buf, 1, 5) != "CD001") continue;
      if (!Encoding.ASCII.GetString(buf, 7, 23).StartsWith("EL TORITO SPECIFICATION", StringComparison.Ordinal)) continue;
      var catalogLba = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0x47));
      if (!ReadSector(s, (long)catalogLba, buf)) return null;
      if (buf[0] != 1 || buf[30] != 0x55 || buf[31] != 0xAA) return null;
      var entries = new List<IsoBootEntry>();
      var platform = buf[1];
      if (buf[32] == 0x88) entries.Add(Entry(buf, 32, platform));
      // Section headers (0x90, last 0x91) with their entries.
      var off = 64;
      while (off + 32 <= SectorSize && buf[off] is 0x90 or 0x91) {
        var last = buf[off] == 0x91;
        var sectionPlatform = buf[off + 1];
        var count = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(off + 2));
        off += 32;
        for (var i = 0; i < count && off + 32 <= SectorSize; i++, off += 32) {
          if (buf[off] == 0x88) entries.Add(Entry(buf, off, sectionPlatform));
        }
        if (last) break;
      }
      return (entries, catalogLba);
    }
    return null;
  }

  private static IsoBootEntry Entry(byte[] b, int off, byte platform) => new(
    platform,
    b[off + 1],
    BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(off + 2)),
    b[off + 4],
    BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(off + 6)),
    BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off + 8)));

  /// <summary>
  /// An MBR partition table, a GPT header or an Apple partition map in the system area:
  /// the image boots from USB through absolute sector numbers that a rebuild would move.
  /// </summary>
  public static bool IsHybrid(Stream s) {
    var buf = new byte[SectorSize];
    if (!ReadSector(s, 0, buf)) return false;
    if (Encoding.ASCII.GetString(buf, 512, 8) == "EFI PART") return true;
    if (buf[0] == (byte)'E' && buf[1] == (byte)'R' && buf[512] == (byte)'P' && buf[513] == (byte)'M') return true;
    if (buf[510] == 0x55 && buf[511] == 0xAA) {
      for (var p = 0; p < 4; p++) {
        var e = 446 + p * 16;
        if (buf[e + 4] != 0 && BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(e + 12)) != 0) return true;
      }
    }
    return false;
  }

  /// <summary>The Volume Identifier of the Primary Volume Descriptor (the disc label).</summary>
  public static string ReadVolumeId(Stream s) {
    var buf = new byte[SectorSize];
    return ReadSector(s, 16, buf) ? Encoding.ASCII.GetString(buf, 40, 32).TrimEnd(' ', '\0') : "";
  }

  /// <summary>True when an image carries a valid isolinux-style boot info table for this address.</summary>
  public static bool HasBootInfoTable(byte[] image, uint loadRba)
    => image.Length >= 64
       && BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(8)) == 16
       && BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(12)) == loadRba;

  /// <summary>Reads <paramref name="length"/> bytes starting at sector <paramref name="lba"/>.</summary>
  public static byte[] ReadAt(Stream s, long lba, int length) {
    var data = new byte[length];
    s.Position = lba * SectorSize;
    s.ReadExactly(data);
    return data;
  }

  private static bool ReadSector(Stream s, long sector, byte[] buf) {
    if ((sector + 1) * SectorSize > s.Length) return false;
    s.Position = sector * SectorSize;
    s.ReadExactly(buf);
    return true;
  }
}
