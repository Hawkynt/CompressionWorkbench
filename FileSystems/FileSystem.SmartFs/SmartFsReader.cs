#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.SmartFs;

/// <summary>
/// Detection / metadata-surface reader for SmartFS — the wear-levelled
/// raw-flash filesystem in Apache NuttX RTOS. SmartFS uses a logical-
/// to-physical sector map: sector 0 is the "format sector" carrying
/// the partition signature, sector size, and number of root sectors.
/// File data is stored in chains of sectors with a 5-byte logical
/// header (logical sector number, sequence, CRC).
///
/// Full chain traversal would require modeling the FAT-like sector
/// mapping table plus directory entry walk. This reader surfaces the
/// parsed format sector and image as metadata.
///
/// Format sector header (selected, little-endian, at file offset 0):
///   0x00 5 bytes  per-sector header (logical sector / status / crc)
///                 — exact layout depends on CONFIG_SMARTFS_NLOGSECS
///   ...
///   0x0A 4 bytes  Format signature = "SMRT" (NuttX CONFIG_SMARTFS_FORMAT_SIG)
///   0x0E 1 byte   format version (typically 1 or 2)
///   0x0F 1 byte   sector size code (0=256, 1=512, 2=1024, 3=2048, 4=4096)
///   0x10 2 bytes  number of root directory sectors
///   0x12 1 byte   reserved
///   0x13+ ...
/// </summary>
public sealed class SmartFsReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<SmartFsEntry> _entries = [];

  public IReadOnlyList<SmartFsEntry> Entries => _entries;

  public byte FormatVersion { get; private set; }
  public uint SectorSize { get; private set; }
  public ushort RootSectorCount { get; private set; }
  public bool ValidFormatSector { get; private set; }

  public static readonly byte[] FormatSignature = "SMRT"u8.ToArray();
  // Scan a small window around the documented offset (10) for the signature —
  // some NuttX builds pad the per-sector header differently depending on
  // CONFIG_MTD_SMART_SECTOR_SIZE / wear-level config, but the signature is
  // always within the first 32 bytes of the format sector.
  private const int SignatureScanWindow = 32;

  public SmartFsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SignatureScanWindow + 8)
      throw new InvalidDataException("SmartFs: image too small for format sector.");

    var sigOffset = -1;
    var limit = Math.Min(SignatureScanWindow, _data.Length - 4);
    for (var i = 0; i <= limit; i++) {
      if (_data.AsSpan(i, 4).SequenceEqual(FormatSignature)) {
        sigOffset = i;
        break;
      }
    }
    if (sigOffset < 0)
      throw new InvalidDataException("SmartFs: missing SMRT format signature in first 32 bytes.");

    this.ValidFormatSector = true;
    if (sigOffset + 4 < _data.Length) this.FormatVersion = _data[sigOffset + 4];
    if (sigOffset + 5 < _data.Length) this.SectorSize = SectorSizeFromCode(_data[sigOffset + 5]);
    if (sigOffset + 8 <= _data.Length) this.RootSectorCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(sigOffset + 6));

    var meta = BuildMetadata(sigOffset);
    _entries.Add(new SmartFsEntry { Name = "FULL.smartfs", Size = _data.Length, Data = _data });
    _entries.Add(new SmartFsEntry { Name = "metadata.ini", Size = meta.Length, Data = meta });
  }

  private static uint SectorSizeFromCode(byte code) => code switch {
    0 => 256u,
    1 => 512u,
    2 => 1024u,
    3 => 2048u,
    4 => 4096u,
    _ => 0u,
  };

  private byte[] BuildMetadata(int sigOffset) {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=ok\n");
    bldr.Append("format=SmartFS (Apache NuttX)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"signature_offset={sigOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"format_version={this.FormatVersion}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sector_size={this.SectorSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"root_sector_count={this.RootSectorCount}\n");
    bldr.Append("note=Sector-chain walk + directory enumeration not implemented (research read-only).\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(SmartFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}
