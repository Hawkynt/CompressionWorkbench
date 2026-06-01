#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.DriveSpace3;

/// <summary>
/// Reads DriveSpace 3 (DOS 6.22 / Windows 95 OSR2 / Win98) CVF headers.
/// DriveSpace 3 is distinguished from DoubleSpace and DriveSpace 2 by the
/// "MS_DSP3" MDBPB signature at offset 3 (vs "MSDSP6.0" for DoubleSpace
/// and "MSDSP6.2" for the original DriveSpace).
/// <para>
/// MDBPB layout (little-endian; first 32 bytes of the host file):
///   0x00 byte[3]   JMP instruction (0xEB or 0xE9)
///   0x03 char[7]   signature "MS_DSP3"
///   0x0A u16       MDFAT entry count
///   0x0C u16       reserved sectors
///   0x0E u32       compressed-volume size in sectors
///   0x12 u16       BitFAT entry count
///   0x14 u16       sectors per cluster
///   0x16 u32       offset (in sectors) of the MDFAT table
///   0x1A u32       offset (in sectors) of the BitFAT table
/// </para>
/// <para>
/// This descriptor parses the MDBPB and surfaces the entry table as one
/// opaque "drivespace3-volume.bin" entry pointing at the compressed DATA
/// region; full LZ77 + Huffman decode is out of scope for the read-only
/// detector wave.
/// </para>
/// </summary>
public sealed class DriveSpace3Reader : IDisposable {
  private readonly byte[] _data;
  private readonly List<DriveSpace3Entry> _entries = [];

  public IReadOnlyList<DriveSpace3Entry> Entries => _entries;
  public bool ValidHeader { get; private set; }
  public int MdfatEntries { get; private set; }
  public int ReservedSectors { get; private set; }
  public long VolumeSectors { get; private set; }
  public int BitfatEntries { get; private set; }
  public int SectorsPerCluster { get; private set; }
  public long MdfatOffsetSectors { get; private set; }
  public long BitfatOffsetSectors { get; private set; }

  public static readonly byte[] Signature = "MS_DSP3"u8.ToArray();
  public const int SignatureOffset = 3;

  public DriveSpace3Reader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 0x20) return;
    if (!_data.AsSpan(SignatureOffset, Signature.Length).SequenceEqual(Signature)) return;
    this.ValidHeader = true;

    this.MdfatEntries     = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(0x0A, 2));
    this.ReservedSectors  = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(0x0C, 2));
    this.VolumeSectors    = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(0x0E, 4));
    this.BitfatEntries    = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(0x12, 2));
    this.SectorsPerCluster = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(0x14, 2));
    this.MdfatOffsetSectors  = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(0x16, 4));
    this.BitfatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(0x1A, 4));

    // Surface a single opaque entry pointing at the BitFAT/data region.
    var dataOffsetSectors = Math.Max(this.MdfatOffsetSectors, this.BitfatOffsetSectors) + 1;
    var dataOffset = checked((int)Math.Min(_data.Length, dataOffsetSectors * 512L));
    if (dataOffset < 0 || dataOffset >= _data.Length) dataOffset = Math.Min(_data.Length, 1024);
    var dataSize = _data.Length - dataOffset;
    if (dataSize > 0) {
      _entries.Add(new DriveSpace3Entry {
        Name = "drivespace3-volume.bin",
        Size = dataSize,
        IsDirectory = false,
        DataOffset = dataOffset,
      });
    }
  }

  public byte[] Extract(DriveSpace3Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.DataOffset < 0 || entry.DataOffset + entry.Size > _data.Length) return [];
    return _data.AsSpan(entry.DataOffset, (int)entry.Size).ToArray();
  }

  public byte[] BuildSurfaceMetadata() {
    var b = new StringBuilder();
    b.Append("parse_status=").Append(this.ValidHeader ? "ok" : "invalid").Append('\n');
    b.Append("format=DriveSpace 3 CVF\n");
    b.Append(CultureInfo.InvariantCulture, $"mdfat_entries={this.MdfatEntries}\n");
    b.Append(CultureInfo.InvariantCulture, $"reserved_sectors={this.ReservedSectors}\n");
    b.Append(CultureInfo.InvariantCulture, $"volume_sectors={this.VolumeSectors}\n");
    b.Append(CultureInfo.InvariantCulture, $"bitfat_entries={this.BitfatEntries}\n");
    b.Append(CultureInfo.InvariantCulture, $"sectors_per_cluster={this.SectorsPerCluster}\n");
    b.Append(CultureInfo.InvariantCulture, $"mdfat_offset_sectors={this.MdfatOffsetSectors}\n");
    b.Append(CultureInfo.InvariantCulture, $"bitfat_offset_sectors={this.BitfatOffsetSectors}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  public void Dispose() { }
}
