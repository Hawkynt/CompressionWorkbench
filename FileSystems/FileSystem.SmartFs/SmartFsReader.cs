#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.SmartFs;

/// <summary>
/// Reader for SmartFS — the wear-levelled raw-flash filesystem in Apache NuttX
/// RTOS. SmartFS uses a logical-to-physical sector map: sector 0 is the format
/// sector carrying the partition signature, sector size, and number of root
/// sectors. File data is stored in chains of logical sectors.
///
/// Format sector header (selected, little-endian, at file offset 0):
///   0x00 5 bytes  per-sector header (logical sector / sequence / CRC / status)
///   0x0A 4 bytes  format signature = "SMRT"
///   0x0E 1 byte   format version
///   0x0F 1 byte   sector size code (0..7 = 256..32768 bytes)
///   0x10 2 bytes  number of root directory sectors
/// </summary>
public sealed class SmartFsReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<SmartFsEntry> _entries = [];

  /// <summary>Gets the entries.</summary>
  public IReadOnlyList<SmartFsEntry> Entries => _entries;

  /// <summary>Gets the format version.</summary>
  public byte FormatVersion { get; private set; }

  /// <summary>Gets the logical sector size.</summary>
  public uint SectorSize { get; private set; }

  /// <summary>Gets the root-directory sector count.</summary>
  public ushort RootSectorCount { get; private set; }

  /// <summary>Gets whether a valid format sector was found.</summary>
  public bool ValidFormatSector { get; private set; }

  /// <summary>Provides the format signature value.</summary>
  public static readonly byte[] FormatSignature = "SMRT"u8.ToArray();

  // Some NuttX configurations pad the per-sector header differently; the
  // signature stays within the beginning of the format sector.
  private const int SignatureScanWindow = 32;

  /// <summary>Initializes a SmartFS reader.</summary>
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
    if (sigOffset + 5 < _data.Length) this.SectorSize = (uint)SmartFsLayout.SizeFromCode(_data[sigOffset + 5]);
    if (sigOffset + 8 <= _data.Length)
      this.RootSectorCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(sigOffset + 6));

    var meta = BuildMetadata(sigOffset);
    _entries.Add(new SmartFsEntry { Name = "FULL.smartfs", Size = _data.Length, Data = _data });
    _entries.Add(new SmartFsEntry { Name = "metadata.ini", Size = meta.Length, Data = meta });

    try {
      WalkRootDirectory();
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException
                                    or IndexOutOfRangeException) {
      // A volume laid out by a configuration this reader does not model still
      // yields the format sector and the raw image above.
    }
  }

  /// <summary>
  /// Reads every entry the root-directory chain holds, and each file's bytes
  /// from the sector chain the entry points at.
  /// </summary>
  private void WalkRootDirectory() {
    if (this.SectorSize == 0) return;

    var sectorSize = checked((int)this.SectorSize);
    var payloadStart = SmartFsLayout.SectorHeaderSize + SmartFsLayout.ChainHeaderSize;
    if (sectorSize <= payloadStart) return;

    var visited = new HashSet<ushort>();
    var sector = (ushort)SmartFsLayout.RootDirSector;
    while (sector != SmartFsLayout.EndOfChain && visited.Add(sector)) {
      var at = (long)sector * sectorSize;
      if (at + sectorSize > _data.Length) return;

      var chain = _data.AsSpan((int)at + SmartFsLayout.SectorHeaderSize);
      var next = BinaryPrimitives.ReadUInt16LittleEndian(chain);
      var used = BinaryPrimitives.ReadUInt16LittleEndian(chain[2..]);
      var type = chain[4];
      if (type != SmartFsLayout.ChainTypeDirectory) return;
      if (used > sectorSize - payloadStart) return;

      for (var offset = 0; offset + SmartFsLayout.EntrySize <= used; offset += SmartFsLayout.EntrySize) {
        var entry = _data.AsSpan((int)at + payloadStart + offset, SmartFsLayout.EntrySize);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(entry);
        if ((flags & SmartFsLayout.EntryActive) == 0) continue;

        var firstSector = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
        var name = ReadName(entry[SmartFsLayout.EntryHeaderSize..]);
        if (name.Length == 0) continue;

        if ((flags & SmartFsLayout.EntryDirectory) != 0) {
          _entries.Add(new SmartFsEntry { Name = name, Size = 0, IsDirectory = true });
          continue;
        }

        var content = ReadChain(firstSector, sectorSize, payloadStart);
        _entries.Add(new SmartFsEntry { Name = name, Size = content.Length, Data = content });
      }

      sector = next;
    }
  }

  /// <summary>The bytes a file's sector chain holds, in chain order.</summary>
  private byte[] ReadChain(ushort firstSector, int sectorSize, int payloadStart) {
    using var content = new MemoryStream();
    var visited = new HashSet<ushort>();
    var sector = firstSector;
    while (sector != SmartFsLayout.EndOfChain && visited.Add(sector)) {
      var at = (long)sector * sectorSize;
      if (at + sectorSize > _data.Length) break;

      var chain = _data.AsSpan((int)at + SmartFsLayout.SectorHeaderSize);
      var next = BinaryPrimitives.ReadUInt16LittleEndian(chain);
      var used = BinaryPrimitives.ReadUInt16LittleEndian(chain[2..]);
      if (chain[4] != SmartFsLayout.ChainTypeFile) break;
      if (used > sectorSize - payloadStart) break;

      content.Write(_data, (int)at + payloadStart, used);
      sector = next;
    }
    return content.ToArray();
  }

  /// <summary>The name in a directory entry, trimmed at its first padding byte.</summary>
  private static string ReadName(ReadOnlySpan<byte> field) {
    var length = 0;
    while (length < SmartFsLayout.MaxNameLength && length < field.Length
           && field[length] is not (0 or 0xFF))
      ++length;
    return Encoding.ASCII.GetString(field[..length]);
  }

  private byte[] BuildMetadata(int sigOffset) {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=ok\n");
    bldr.Append("format=SmartFS (Apache NuttX)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"signature_offset={sigOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"format_version={this.FormatVersion}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sector_size={this.SectorSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"root_sector_count={this.RootSectorCount}\n");
    bldr.Append("note=Root-directory and file sector chains are enumerated when their geometry is supported.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>Decodes the supplied input.</summary>
  public byte[] Extract(SmartFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>Releases resources held by this instance.</summary>
  public void Dispose() { }
}
