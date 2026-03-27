#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.TrDos;

/// <summary>
/// Reads TR-DOS (.TRD) ZX Spectrum disk images. Enumerates files from the
/// directory at track 0, sectors 0-7. Supports extraction of individual files.
/// </summary>
public sealed class TrDosReader : IDisposable {
  private const int SectorSize = 256;
  private const int SectorsPerTrack = 16;
  private const int TrackSize = SectorSize * SectorsPerTrack;
  private const int DirEntrySize = 16;
  private const int MaxDirEntries = 128; // 8 sectors * 256 / 16
  private const int DiskInfoOffset = 0x800; // track 0, sector 8
  private const byte TrDosIdByte = 0x10;

  private readonly byte[] _data;
  private readonly List<TrDosEntry> _entries = [];

  public IReadOnlyList<TrDosEntry> Entries => _entries;
  public byte DiskType { get; }
  public string DiskLabel { get; }

  public TrDosReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();

    if (_data.Length < DiskInfoOffset + 256)
      throw new InvalidDataException("TR-DOS: file too small for disk info sector.");

    // Validate TR-DOS ID byte at offset 0x8E7
    if (_data[DiskInfoOffset + 0xE7] != TrDosIdByte)
      throw new InvalidDataException("TR-DOS: invalid ID byte.");

    DiskType = _data[DiskInfoOffset + 0xE3];
    DiskLabel = Encoding.ASCII.GetString(_data, DiskInfoOffset + 0xF5, 8).TrimEnd();

    // Read directory entries
    for (var i = 0; i < MaxDirEntries; i++) {
      var offset = i * DirEntrySize;
      if (offset + DirEntrySize > _data.Length) break;

      var firstByte = _data[offset];
      if (firstByte == 0x00) break; // end of directory
      if (firstByte == 0x01) continue; // deleted file

      var name = Encoding.ASCII.GetString(_data, offset, 8).TrimEnd();
      var ext = (char)_data[offset + 8];
      var lengthSectors = _data[offset + 13];
      var startSector = _data[offset + 14];
      var startTrack = _data[offset + 15];
      var param2 = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset + 11));

      var fullName = ext switch {
        'B' => name + ".bas",
        'C' => name + ".cod",
        'D' => name + ".dat",
        '#' => name + ".seq",
        _ => name + "." + ext,
      };

      _entries.Add(new TrDosEntry {
        Name = fullName,
        Size = lengthSectors * SectorSize,
        DataSize = param2,
        StartSector = startSector,
        StartTrack = startTrack,
        LengthSectors = lengthSectors,
        FileType = ext,
      });
    }
  }

  public byte[] Extract(TrDosEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var offset = entry.StartTrack * TrackSize + entry.StartSector * SectorSize;
    var length = entry.LengthSectors * SectorSize;
    if (offset + length > _data.Length)
      length = Math.Max(0, _data.Length - offset);
    return _data.AsSpan(offset, length).ToArray();
  }

  public void Dispose() { }
}
