#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.Pc98;

/// <summary>
/// Reads NEC PC-98 DOS disk images. The PC-98 is a Japanese personal
/// computer family with a unique BIOS and Initial Program Loader
/// signature ("NECIPL" = 0x4E 0x45 0x43 0x49 0x50 0x4C) at file offset 0
/// that distinguishes its FAT layout from IBM PC FAT despite using
/// 512-byte sectors and a FAT12/16-like directory.
/// <para>
/// PC-98 IPL layout (little-endian, sector 0; 512 bytes):
///   0x00 char[6]  "NECIPL" — IPL signature (this descriptor's primary magic)
///   0x06 ...      IPL boot code (NEC vendor-specific)
///   0x20 ...      PC-98 BPB extensions (vendor area)
///   0x80 char[3]  jump instruction (FAT BPB at boot-block 1, sector 1)
///   0x83 char[8]  OEM name
///   0x8B u16      bytes per sector
///   0x8D byte     sectors per cluster
///   0x8E u16      reserved sectors
///   0x90 byte     number of FATs
///   0x91 u16      root entries
///   0x97 u16      sectors per FAT
/// </para>
/// <para>
/// On PC-98 systems the FAT BPB and root directory follow the same
/// FAT12/16 layout as IBM PC, but at an offset of one IPL block
/// (=512 bytes) from file start.
/// </para>
/// </summary>
public sealed class Pc98Reader : IDisposable {
  public const int SectorSize = 512;
  public const int IplOffset = 0;
  public const int BpbOffset = 0x80;

  private readonly byte[] _data;
  private readonly List<Pc98Entry> _entries = [];

  public IReadOnlyList<Pc98Entry> Entries => _entries;
  public bool ValidVolume { get; private set; }
  public int SectorsPerCluster { get; private set; }
  public int ReservedSectors { get; private set; }
  public int FatCount { get; private set; }
  public int RootEntries { get; private set; }
  public int SectorsPerFat { get; private set; }

  public static readonly byte[] Signature = "NECIPL"u8.ToArray();

  public Pc98Reader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SectorSize) return;
    if (!_data.AsSpan(0, Signature.Length).SequenceEqual(Signature)) return;
    this.ValidVolume = true;

    // Parse the FAT BPB at the second 256-byte half of the IPL block
    // (offset 0x80) — typical PC-98 IPL convention.
    if (_data.Length < BpbOffset + 32) return;
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(BpbOffset + 0x0B, 2));
    if (bytesPerSector is < 256 or > 4096) bytesPerSector = SectorSize;
    this.SectorsPerCluster = Math.Max(1, (int)_data[BpbOffset + 0x0D]);
    this.ReservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(BpbOffset + 0x0E, 2));
    this.FatCount = _data[BpbOffset + 0x10];
    this.RootEntries = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(BpbOffset + 0x11, 2));
    this.SectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(BpbOffset + 0x16, 2));

    if (this.FatCount is < 1 or > 4) return;
    if (this.RootEntries is < 16 or > 1024) return;

    var rootDirSector = this.ReservedSectors + this.FatCount * this.SectorsPerFat;
    var rootDirOffset = rootDirSector * bytesPerSector + bytesPerSector; // +1 for IPL block
    if (rootDirOffset >= _data.Length) return;

    for (var i = 0; i < this.RootEntries; i++) {
      var off = rootDirOffset + i * 32;
      if (off + 32 > _data.Length) break;
      var rec = _data.AsSpan(off, 32);
      var first = rec[0];
      if (first == 0x00) break;           // end of directory
      if (first == 0xE5) continue;        // deleted
      var attr = rec[0x0B];
      if (attr == 0x0F) continue;         // VFAT LFN
      if ((attr & 0x08) != 0) continue;   // volume label

      var name = ReadShiftJisName(rec[..8]);
      var ext = ReadShiftJisName(rec.Slice(8, 3));
      if (string.IsNullOrEmpty(name)) continue;
      if (name == "." || name == "..") continue;
      var fullName = string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";

      var firstCluster = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(0x1A, 2));
      var size = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(0x1C, 4));
      var isDir = (attr & 0x10) != 0;

      _entries.Add(new Pc98Entry {
        Name = fullName,
        Size = isDir ? 0 : size,
        IsDirectory = isDir,
        FirstCluster = firstCluster,
        Attributes = attr,
      });
    }
  }

  private static string ReadShiftJisName(ReadOnlySpan<byte> span) {
    Span<byte> trimmed = stackalloc byte[span.Length];
    var len = 0;
    foreach (var b in span) {
      if (b is 0 or 0x20) {
        if (len == 0) continue;
        break;
      }
      trimmed[len++] = b;
    }
    if (len == 0) return "";
    try {
      var sjis = Encoding.GetEncoding(932);
      return sjis.GetString(trimmed[..len]);
    } catch {
      var chars = new char[len];
      for (var i = 0; i < len; i++) chars[i] = (char)trimmed[i];
      return new string(chars);
    }
  }

  public byte[] Extract(Pc98Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (this.SectorsPerCluster <= 0) return [];
    var bytesPerCluster = this.SectorsPerCluster * SectorSize;
    var rootDirBytes = (this.RootEntries * 32 + SectorSize - 1) / SectorSize * SectorSize;
    var dataStartSector = this.ReservedSectors + this.FatCount * this.SectorsPerFat;
    var dataStartOffset = dataStartSector * SectorSize + rootDirBytes + SectorSize; // +1 for IPL
    var clusterOffset = (entry.FirstCluster - 2) * bytesPerCluster + dataStartOffset;
    if (clusterOffset < 0 || clusterOffset >= _data.Length) return [];
    var size = (int)Math.Min(entry.Size, _data.Length - clusterOffset);
    return size <= 0 ? [] : _data.AsSpan(clusterOffset, size).ToArray();
  }

  public byte[] BuildSurfaceMetadata() {
    var b = new StringBuilder();
    b.Append("parse_status=").Append(this.ValidVolume ? "ok" : "invalid").Append('\n');
    b.Append("format=NEC PC-98 DOS FAT\n");
    b.Append(CultureInfo.InvariantCulture, $"sectors_per_cluster={this.SectorsPerCluster}\n");
    b.Append(CultureInfo.InvariantCulture, $"reserved_sectors={this.ReservedSectors}\n");
    b.Append(CultureInfo.InvariantCulture, $"fat_count={this.FatCount}\n");
    b.Append(CultureInfo.InvariantCulture, $"root_entries={this.RootEntries}\n");
    b.Append(CultureInfo.InvariantCulture, $"sectors_per_fat={this.SectorsPerFat}\n");
    b.Append(CultureInfo.InvariantCulture, $"file_count={this.Entries.Count}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  public void Dispose() { }
}
