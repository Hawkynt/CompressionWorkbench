#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Lif;

/// <summary>
/// Reader for HP LIF (Logical Interchange Format) volumes — the disk format used
/// by the HP Series 80, HP-71, HP-75, and HP-85 personal computers as well as
/// the HP-IL/HP-IB peripherals from that era. Volumes contain a flat directory
/// of fixed-length files described by 32-byte directory entries.
/// </summary>
public sealed class LifReader {

  public const ushort LifMagic = 0x8000;
  public const int SectorSize = 256;

  public sealed record FileEntry(
    string Name,
    ushort FileType,
    int StartSector,
    int LengthSectors,
    long ByteLength,
    DateTime? Created
  );

  public sealed record Volume(
    string Label,
    int DirectoryStartSector,
    int DirectorySectors,
    IReadOnlyList<FileEntry> Files,
    byte[] Image  // raw bytes, retained so Extract can read sector ranges
  );

  public static Volume Read(ReadOnlySpan<byte> image) {
    if (image.Length < SectorSize) throw new InvalidDataException("LIF: image shorter than one 256-byte sector.");
    var magic = BinaryPrimitives.ReadUInt16BigEndian(image);
    if (magic != LifMagic) throw new InvalidDataException($"LIF: bad magic 0x{magic:X4}, expected 0x8000.");

    var label = Encoding.ASCII.GetString(image.Slice(2, 6)).TrimEnd(' ', '\0');
    var dirStart = (int)BinaryPrimitives.ReadUInt32BigEndian(image[8..]);
    var dirSectors = (int)BinaryPrimitives.ReadUInt32BigEndian(image[16..]);

    var files = new List<FileEntry>();
    var dirByteOffset = dirStart * SectorSize;
    var entriesPerSector = SectorSize / 32;
    var totalEntries = dirSectors * entriesPerSector;

    for (var i = 0; i < totalEntries; i++) {
      var off = dirByteOffset + i * 32;
      if (off + 32 > image.Length) break;
      var entry = image.Slice(off, 32);

      var first = entry[0];
      if (first == 0xFF) break;            // physical end-of-directory marker
      if (first == 0x00 || first == ' ') continue; // empty / deleted slot

      var name = Encoding.ASCII.GetString(entry[..10]).TrimEnd(' ', '\0');
      var fileType = BinaryPrimitives.ReadUInt16BigEndian(entry[10..]);
      var startSec = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
      var lenSec = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[16..]);
      // bytes 20..26: BCD timestamp (yy mm dd hh mm ss). Surface only when plausible.
      var ts = TryReadBcdTimestamp(entry.Slice(20, 6));

      files.Add(new FileEntry(
        Name: name,
        FileType: fileType,
        StartSector: startSec,
        LengthSectors: lenSec,
        ByteLength: (long)lenSec * SectorSize,
        Created: ts));
    }

    return new Volume(
      Label: label,
      DirectoryStartSector: dirStart,
      DirectorySectors: dirSectors,
      Files: files,
      Image: image.ToArray());
  }

  public static byte[] Extract(Volume v, FileEntry e) {
    var startByte = (long)e.StartSector * SectorSize;
    var len = e.LengthSectors * SectorSize;
    if (startByte + len > v.Image.Length)
      len = (int)Math.Max(0, v.Image.Length - startByte);
    var buf = new byte[len];
    Array.Copy(v.Image, startByte, buf, 0, len);
    return buf;
  }

  private static DateTime? TryReadBcdTimestamp(ReadOnlySpan<byte> bcd) {
    // BCD fields: YY MM DD HH MM SS — each byte holds two BCD digits.
    static int Bcd(byte b) {
      var hi = (b >> 4) & 0x0F;
      var lo = b & 0x0F;
      if (hi > 9 || lo > 9) return -1;
      return hi * 10 + lo;
    }
    var yy = Bcd(bcd[0]); var mm = Bcd(bcd[1]); var dd = Bcd(bcd[2]);
    var hh = Bcd(bcd[3]); var mn = Bcd(bcd[4]); var ss = Bcd(bcd[5]);
    if (yy < 0 || mm is < 1 or > 12 || dd is < 1 or > 31 || hh > 23 || mn > 59 || ss > 59) return null;
    var year = yy >= 70 ? 1900 + yy : 2000 + yy;
    try { return new DateTime(year, mm, dd, hh, mn, ss, DateTimeKind.Unspecified); }
    catch { return null; }
  }
}
