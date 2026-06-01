#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.Stacker;

/// <summary>
/// Reads Stacker CVF (Compressed Volume File) headers — Stacker (Stac
/// Electronics, MS-DOS 1990–1993) is the historical predecessor of
/// Microsoft's DoubleSpace/DriveSpace. A Stacker volume is an MS-DOS
/// host file (typically STACVOL.DSK or *.STA/*.STK) that wraps a
/// compressed inner FAT volume. The on-disk container is the Stacker
/// Control Block (SCB).
/// <para>
/// SCB header layout (little-endian, first 16 bytes):
///   0x00 char[2]  signature "ST"  (0x53 0x54)
///   0x02 byte     'K'  (0x4B)  -- magic completion byte
///   0x03 byte     version (3, 4 — Stacker 3.x / 4.x)
///   0x04 u16      reserved sector count
///   0x06 u16      logical sectors per cluster
///   0x08 u32      compressed-volume size in 512-byte sectors
///   0x0C u32      offset (in sectors) of the inner FAT bootblock
/// </para>
/// <para>
/// Decompression of the Stac LZS payload is out of scope for this
/// read-only descriptor; the reader surfaces a single opaque entry
/// "stacker-volume.bin" pointing at the wrapped inner volume so callers
/// can route it through a future FAT extractor.
/// </para>
/// </summary>
public sealed class StackerReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<StackerEntry> _entries = [];

  public IReadOnlyList<StackerEntry> Entries => _entries;

  public bool ValidHeader { get; private set; }
  public int Version { get; private set; }
  public int ReservedSectors { get; private set; }
  public int SectorsPerCluster { get; private set; }
  public long VolumeSectors { get; private set; }
  public long InnerBootSectorOffset { get; private set; }

  public StackerReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 16) return;
    // Magic "STK" + version byte at offset 0..3.
    if (_data[0] != 0x53 || _data[1] != 0x54 || _data[2] != 0x4B) return;
    var version = _data[3];
    if (version is not (3 or 4)) {
      // Accept only known Stacker container versions; otherwise treat as not-Stacker.
      return;
    }
    this.ValidHeader = true;
    this.Version = version;

    this.ReservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(4, 2));
    this.SectorsPerCluster = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(6, 2));
    this.VolumeSectors = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(8, 4));
    this.InnerBootSectorOffset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(12, 4));

    // Surface the wrapped inner FAT image as a single opaque entry. The
    // inner bootblock starts at InnerBootSectorOffset*512; size is the
    // remaining file length from there.
    var innerOffset = checked((int)Math.Min(_data.Length, this.InnerBootSectorOffset * 512L));
    if (innerOffset < 0 || innerOffset >= _data.Length) innerOffset = Math.Min(_data.Length, 512);
    var innerSize = _data.Length - innerOffset;
    if (innerSize > 0) {
      _entries.Add(new StackerEntry {
        Name = "stacker-volume.bin",
        Size = innerSize,
        IsDirectory = false,
        DataOffset = innerOffset,
      });
    }
  }

  public byte[] Extract(StackerEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.DataOffset < 0 || entry.DataOffset + entry.Size > _data.Length) return [];
    return _data.AsSpan(entry.DataOffset, (int)entry.Size).ToArray();
  }

  public byte[] BuildSurfaceMetadata() {
    var b = new StringBuilder();
    b.Append("parse_status=").Append(this.ValidHeader ? "ok" : "invalid").Append('\n');
    b.Append("format=Stacker CVF\n");
    b.Append(CultureInfo.InvariantCulture, $"version={this.Version}\n");
    b.Append(CultureInfo.InvariantCulture, $"reserved_sectors={this.ReservedSectors}\n");
    b.Append(CultureInfo.InvariantCulture, $"sectors_per_cluster={this.SectorsPerCluster}\n");
    b.Append(CultureInfo.InvariantCulture, $"volume_sectors={this.VolumeSectors}\n");
    b.Append(CultureInfo.InvariantCulture, $"inner_boot_sector={this.InnerBootSectorOffset}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  public void Dispose() { }
}
