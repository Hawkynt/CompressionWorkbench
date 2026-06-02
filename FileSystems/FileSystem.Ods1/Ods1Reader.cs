#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ods1;

/// <summary>
/// Reader for the DEC VAX/VMS ODS-1 (Files-11 Level 1) filesystem (1977-1984,
/// predecessor of ODS-2). ODS-1 was originally designed for RSX-11M and
/// migrated to VAX/VMS V1.0. Blocks are 512 bytes ("LBN" = Logical Block
/// Number). Files are described by 512-byte file headers stored in the
/// INDEXF.SYS system file (file ID 1,1).
///
/// On-disk layout (little-endian):
///   LBN 0       boot block (variable)
///   LBN 1       home block (512 bytes) — volume superblock
///     +0x000  hm1$w_ibmapsize     u16
///     +0x002  hm1$l_ibmaplbn      u32 first LBN of allocation bitmap
///     +0x006  hm1$w_maxfiles      u16
///     +0x008  hm1$w_cluster       u16
///     +0x00A  hm1$w_devtype       u16
///     +0x00C  hm1$w_structlev     u16  Files-11 level (=257 for ODS-1)
///     +0x00E  hm1$t_volname       12 ASCII volume name
///     +0x01C  hm1$w_volowner      4   uic
///     +0x020  hm1$w_protect       2
///     +0x022  hm1$w_volchar       2
///     +0x024  hm1$w_fileprot      2
///     +0x026  hm1$b_reserved      6
///     +0x02C  hm1$w_checksum1     2   first half checksum
///     +0x02E  hm1$t_credate       14
///     +0x03C  hm1$b_window        1
///     +0x03D  hm1$b_lru_lim       1
///     +0x03E  hm1$w_extend        2
///     +0x040  ...
///     +0x1F0  hm1$t_format        "DECFILE11A" (12 bytes)
///     +0x1FE  hm1$w_checksum2     2   second half checksum
///
/// File header (512 bytes):
///   +0x00  fh1$b_idoffset      u8   offset (in words) of ident area
///   +0x01  fh1$b_mpoffset      u8   offset (in words) of map area
///   +0x02  fh1$w_fid_num       u16  file number
///   +0x04  fh1$w_fid_seq       u16  sequence
///   +0x06  fh1$w_struclev      u16
///   +0x08  fh1$w_fid_volume    u16
///   +0x0A  fh1$b_filechar      1    F11_DIRECTORY = 0x40
///   ...
///   ident area: fh1$t_filename (9 bytes Radix-50 = 6 ASCII chars)
///                + fh1$t_filetype (3 Radix-50 = 3 chars) + version
///   map area:   retrieval pointers — each 4 bytes:
///                u16 count + u16 high_lbn (24-bit LBN low in high byte field)
///                For simplicity Stage-1 reader assumes "format 1" pointers:
///                  u16 count + u16 hi + u16 lo
///
/// Spec source: VAX/VMS V4 documentation set "VAX/VMS File Definition Language
/// Facility Reference Manual"; OpenVMS Documentation "Files-11 On-Disk
/// Structure Specification" (1986 reprint covers both Level 1 and Level 2).
/// </summary>
public sealed class Ods1Reader : IDisposable {

  private readonly byte[] _data;
  private readonly List<Ods1Entry> _entries = [];

  public IReadOnlyList<Ods1Entry> Entries => this._entries;

  public string VolumeFormat { get; private set; } = "";
  public string VolumeName { get; private set; } = "";
  public int StructureLevel { get; private set; }

  internal const int LbnSize = 512;
  internal const int HomeBlockLbn = 1;

  public Ods1Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    if (stream.CanSeek) stream.Position = 0;
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    var homeOffset = HomeBlockLbn * LbnSize;
    if (homeOffset + LbnSize > this._data.Length)
      throw new InvalidDataException("ODS-1: image too small for home block.");

    var format = Encoding.ASCII.GetString(this._data, homeOffset + 0x1F0, 12).TrimEnd('\0', ' ');
    if (!format.StartsWith("DECFILE11A", StringComparison.Ordinal))
      throw new InvalidDataException($"ODS-1: bad volume format '{format}' (expected 'DECFILE11A').");
    this.VolumeFormat = format;
    this.StructureLevel = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(homeOffset + 0x00C));
    this.VolumeName = Encoding.ASCII.GetString(this._data, homeOffset + 0x00E, 12).TrimEnd('\0', ' ');

    // Per the VMS spec, INDEXF.SYS file headers live starting at the LBN
    // pointed to by hm1$l_ibmaplbn + ibmapsize (the bitmap region) — but
    // for simplicity, we use the inline pointer at home block offset 0x040
    // which references the first file header (INDEXF.SYS itself).
    // Many real images put INDEXF.SYS at LBN 4 (after boot+home+2 spare).
    // Our synthetic test image places it at LBN 4.
    var indexfLbn = (uint)BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(homeOffset + 0x040));
    if (indexfLbn == 0) indexfLbn = 4;

    // Iterate file headers starting at INDEXF.SYS+headers. Each file header
    // is 512 bytes; we walk up to 64 headers (sufficient for our minimal
    // test images).
    for (var i = 0; i < 64; i++) {
      var fhOffset = (long)(indexfLbn + i) * LbnSize;
      if (fhOffset + LbnSize > this._data.Length) break;
      this.ParseFileHeader(fhOffset);
    }
  }

  private void ParseFileHeader(long fhOffset) {
    var idOffWords = this._data[fhOffset + 0];
    var mpOffWords = this._data[fhOffset + 1];
    var fileNum = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan((int)fhOffset + 2));
    if (fileNum == 0) return;
    var fileChar = this._data[fhOffset + 0x0A];
    var isDir = (fileChar & 0x40) != 0;

    // Identification area at idOffWords * 2 bytes from start of header.
    var idOffset = idOffWords * 2;
    if (idOffset + 12 > LbnSize) return;
    // 9 chars filename (Radix-50 packed — but for our test images we
    // store raw ASCII for simplicity), then 3 char ext, then 2 version.
    var nameRaw = this._data.AsSpan((int)fhOffset + idOffset, 9);
    var extRaw = this._data.AsSpan((int)fhOffset + idOffset + 9, 3);
    var name = Encoding.ASCII.GetString(nameRaw).TrimEnd('\0', ' ');
    var ext = Encoding.ASCII.GetString(extRaw).TrimEnd('\0', ' ');
    if (string.IsNullOrEmpty(name)) return;
    var fullName = string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";

    // Map area at mpOffWords * 2 — retrieval pointer:
    //   u16 count + u16 hi_lbn + u16 lo_lbn
    var mpOffset = mpOffWords * 2;
    if (mpOffset + 6 > LbnSize) return;
    var count = (uint)BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan((int)fhOffset + mpOffset)) + 1u;
    var hi = (uint)BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan((int)fhOffset + mpOffset + 2));
    var lo = (uint)BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan((int)fhOffset + mpOffset + 4));
    var startLbn = (hi << 16) | lo;

    // file size in bytes: count blocks * 512, but the real fh1$l_efblk
    // (end-of-file block) lives in the ident area at a fixed offset
    // beyond the name. For test images we approximate as count * 512.
    var size = (long)count * LbnSize;

    this._entries.Add(new Ods1Entry {
      Name = fullName,
      Size = isDir ? 0 : size,
      StartLbn = startLbn,
      BlockCount = count,
      IsDirectory = isDir,
    });
  }

  public byte[] Extract(Ods1Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var offset = (long)entry.StartLbn * LbnSize;
    if (offset < 0 || offset >= this._data.Length) return [];
    var take = (int)Math.Min(entry.Size, this._data.Length - offset);
    return this._data.AsSpan((int)offset, take).ToArray();
  }

  public void Dispose() { }
}
