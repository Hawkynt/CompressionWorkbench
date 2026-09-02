#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.Human68k;

/// <summary>
/// Reads Sharp X68000 Human68k disk images. Human68k uses a FAT12-like
/// filesystem with an extended Shift_JIS-aware directory record format
/// — Japanese file names are stored in Shift_JIS, and the BPB at offset
/// 0x10 carries a Human68k-specific identifier.
/// <para>
/// Boot sector layout (little-endian, sector 0):
///   0x00 byte[3]  jump (0x60 or 0xEB or 0xE9)
///   0x03 char[8]  OEM name — Human68k disks typically carry "X68K"
///                 at offset 0x10, but many images put OEM at offset 0x03
///   0x0B u16      bytes per sector
///   0x0D byte     sectors per cluster
///   0x0E u16      reserved sector count
///   0x10 char[4]  "X68K" tag (Human68k identifier — primary detection magic)
///   0x14 byte     number of FATs
///   0x15 u16      root directory entry count
///   0x17 u16      total sectors (small)
///   0x19 byte     media descriptor
///   0x1A u16      sectors per FAT
///   0x1C u16      sectors per track
///   0x1E u16      heads
///   0x20 u32      hidden sectors
/// </para>
/// <para>
/// Directory entry layout (32 bytes; same as DOS FAT12 with attributes,
/// but filename can use Shift_JIS encoding):
///   0x00 char[8]  filename
///   0x08 char[3]  extension
///   0x0B byte     attributes (0x10=dir, 0x08=volume label, 0x80=killed)
///   0x1A u16      first cluster
///   0x1C u32      file size
/// </para>
/// </summary>
public sealed class Human68kReader : IDisposable {
  /// <summary>
  /// Defines the sector size constant value.
  /// </summary>
  public const int SectorSize = 512;

  private readonly byte[] _data;
  private readonly List<Human68kEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<Human68kEntry> Entries => _entries;
  /// <summary>
  /// Gets a value indicating whether valid volume.
  /// </summary>
  public bool ValidVolume { get; private set; }
  /// <summary>
  /// Gets or sets the sectors per cluster.
  /// </summary>
  public int SectorsPerCluster { get; private set; }
  /// <summary>
  /// Gets or sets the reserved sectors.
  /// </summary>
  public int ReservedSectors { get; private set; }
  /// <summary>
  /// Gets or sets the fat count.
  /// </summary>
  public int FatCount { get; private set; }
  /// <summary>
  /// Gets or sets the root entries.
  /// </summary>
  public int RootEntries { get; private set; }
  /// <summary>
  /// Gets or sets the sectors per fat.
  /// </summary>
  public int SectorsPerFat { get; private set; }

  /// <summary>
  /// Initializes a new instance of <see cref="Human68kReader"/>.
  /// </summary>
  public Human68kReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SectorSize) return;
    // Detection magic: "X68K" at offset 0x10 OR Shift_JIS-ish OEM name patterns.
    if (_data[0x10] != (byte)'X' || _data[0x11] != (byte)'6' ||
        _data[0x12] != (byte)'8' || _data[0x13] != (byte)'K') {
      // Fall back: jump byte 0x60 (Human68k BSR) + plausible BPB.
      if (_data[0] != 0x60) return;
    }
    this.ValidVolume = true;

    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(0x0B, 2));
    if (bytesPerSector is < 256 or > 4096) bytesPerSector = SectorSize;
    this.SectorsPerCluster = Math.Max(1, (int)_data[0x0D]);
    this.ReservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(0x0E, 2));
    this.FatCount = _data[0x14];
    this.RootEntries = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(0x15, 2));
    this.SectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(0x1A, 2));

    if (this.FatCount is < 1 or > 4) return;
    if (this.RootEntries is < 16 or > 1024) return;

    // Root directory immediately after reserved + FATs.
    var rootDirOffset = (this.ReservedSectors + this.FatCount * this.SectorsPerFat) * bytesPerSector;
    if (rootDirOffset >= _data.Length) return;

    for (var i = 0; i < this.RootEntries; i++) {
      var off = rootDirOffset + i * 32;
      if (off + 32 > _data.Length) break;
      var rec = _data.AsSpan(off, 32);
      var first = rec[0];
      if (first == 0x00) break;           // end of directory
      if (first == 0xE5) continue;        // deleted
      var attr = rec[0x0B];
      if (attr == 0x0F) continue;         // VFAT LFN (rare on Human68k)
      if ((attr & 0x08) != 0) continue;   // volume label

      var name = ReadShiftJisName(rec[..8]);
      var ext = ReadShiftJisName(rec.Slice(8, 3));
      if (string.IsNullOrEmpty(name)) continue;
      if (name == "." || name == "..") continue;
      var fullName = string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";

      var firstCluster = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(0x1A, 2));
      var size = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(0x1C, 4));
      var isDir = (attr & 0x10) != 0;

      _entries.Add(new Human68kEntry {
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
        if (len == 0) continue; // strip leading
        break;
      }
      trimmed[len++] = b;
    }
    if (len == 0) return "";
    try {
      var sjis = Encoding.GetEncoding(932); // Shift_JIS
      return sjis.GetString(trimmed[..len]);
    } catch {
      // Fall back to Latin-1-ish.
      var chars = new char[len];
      for (var i = 0; i < len; i++) chars[i] = (char)trimmed[i];
      return new string(chars);
    }
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(Human68kEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    // FAT cluster-chain decoding is out of scope for this descriptor;
    // we surface only the size of the first cluster-aligned run.
    if (this.SectorsPerCluster <= 0) return [];
    var bytesPerCluster = this.SectorsPerCluster * SectorSize;
    var dataStartSector = this.ReservedSectors + this.FatCount * this.SectorsPerFat
      + (this.RootEntries * 32 + SectorSize - 1) / SectorSize;
    var clusterOffset = (entry.FirstCluster - 2) * bytesPerCluster + dataStartSector * SectorSize;
    if (clusterOffset < 0 || clusterOffset >= _data.Length) return [];
    var size = (int)Math.Min(entry.Size, _data.Length - clusterOffset);
    return size <= 0 ? [] : _data.AsSpan(clusterOffset, size).ToArray();
  }

  /// <summary>
  /// Performs the build surface metadata operation.
  /// </summary>
  public byte[] BuildSurfaceMetadata() {
    var b = new StringBuilder();
    b.Append("parse_status=").Append(this.ValidVolume ? "ok" : "invalid").Append('\n');
    b.Append("format=Sharp X68000 Human68k FAT\n");
    b.Append(CultureInfo.InvariantCulture, $"sectors_per_cluster={this.SectorsPerCluster}\n");
    b.Append(CultureInfo.InvariantCulture, $"reserved_sectors={this.ReservedSectors}\n");
    b.Append(CultureInfo.InvariantCulture, $"fat_count={this.FatCount}\n");
    b.Append(CultureInfo.InvariantCulture, $"root_entries={this.RootEntries}\n");
    b.Append(CultureInfo.InvariantCulture, $"sectors_per_fat={this.SectorsPerFat}\n");
    b.Append(CultureInfo.InvariantCulture, $"file_count={this.Entries.Count}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
