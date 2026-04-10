#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.D64;

public sealed class D64Reader : IDisposable {
  private readonly byte[] _data;
  private readonly List<D64Entry> _entries = [];

  public IReadOnlyList<D64Entry> Entries => _entries;

  // Standard D64 sizes
  private const int StandardSize = 174848;
  private const int StandardSizeWithErrors = 175531;
  private const int SectorSize = 256;

  // Track 18 is the directory track
  private const int DirTrack = 18;
  private const int BamSector = 0;
  private const int DirStartSector = 1;

  // Sectors per track for each zone
  private static readonly int[] SectorsPerTrack = [
    0, // track 0 doesn't exist
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, // 1-17
    19, 19, 19, 19, 19, 19, 19, // 18-24
    18, 18, 18, 18, 18, 18, // 25-30
    17, 17, 17, 17, 17 // 31-35
  ];

  public D64Reader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < StandardSize)
      throw new InvalidDataException("D64: image too small.");

    // Read directory chain starting at track 18 sector 1
    ReadDirectory();
  }

  private int GetSectorOffset(int track, int sector) {
    if (track < 1 || track >= SectorsPerTrack.Length) return -1;
    if (sector < 0 || sector >= SectorsPerTrack[track]) return -1;

    var offset = 0;
    for (var t = 1; t < track; t++)
      offset += SectorsPerTrack[t] * SectorSize;
    offset += sector * SectorSize;
    return offset;
  }

  private void ReadDirectory() {
    var track = DirTrack;
    var sector = DirStartSector;
    var visited = new HashSet<(int, int)>();

    while (track != 0 && visited.Add((track, sector))) {
      var offset = GetSectorOffset(track, sector);
      if (offset < 0 || offset + SectorSize > _data.Length) break;

      // Next directory sector link
      var nextTrack = _data[offset];
      var nextSector = _data[offset + 1];

      // 8 directory entries per sector (32 bytes each)
      for (var i = 0; i < 8; i++) {
        var entryOff = offset + i * 32;
        // first entry at offset+0 overlaps the link bytes, but entry 0 starts at offset+0 too
        // Actually: the link is only at the start of the sector. Entries are at offset+0, +32, +64, etc.
        // Entry 0: offset 0-31 (bytes 0-1 are sector link for entry 0, file type at byte 2)

        var fileType = _data[entryOff + 2];
        if ((fileType & 0x07) == 0) continue; // DEL or scratched

        var startTrack = _data[entryOff + 3];
        var startSector = _data[entryOff + 4];

        // Filename: 16 bytes at offset 5, PETSCII, padded with 0xA0
        var nameBytes = _data.AsSpan(entryOff + 5, 16);
        var nameEnd = nameBytes.IndexOf((byte)0xA0);
        if (nameEnd < 0) nameEnd = 16;
        var name = Encoding.ASCII.GetString(_data, entryOff + 5, nameEnd);

        // File size in sectors (LE uint16) at offset 30-31
        var sectorCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(entryOff + 30));

        // Calculate actual file size by following the chain
        var actualSize = CalculateFileSize(startTrack, startSector, sectorCount);

        _entries.Add(new D64Entry {
          Name = name,
          Size = actualSize,
          FileType = fileType,
          StartTrack = startTrack,
          StartSector = startSector,
        });
      }

      track = nextTrack;
      sector = nextSector;
    }
  }

  private int CalculateFileSize(int startTrack, int startSector, int sectorCount) {
    if (sectorCount == 0) return 0;
    // Follow the sector chain to find the last sector's used bytes
    var track = startTrack;
    var sector = startSector;
    var visited = new HashSet<(int, int)>();
    var totalSectors = 0;

    while (track != 0 && visited.Add((track, sector))) {
      var off = GetSectorOffset(track, sector);
      if (off < 0 || off + SectorSize > _data.Length) break;
      totalSectors++;
      var nextTrack = _data[off];
      var nextSector = _data[off + 1];
      if (nextTrack == 0) {
        // Last sector: nextSector = number of bytes used + 1
        return (totalSectors - 1) * 254 + (nextSector - 1);
      }
      track = nextTrack;
      sector = nextSector;
    }
    // Fallback: use sector count
    return sectorCount * 254;
  }

  public byte[] Extract(D64Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0) return [];

    using var ms = new MemoryStream();
    var track = entry.StartTrack;
    var sector = entry.StartSector;
    var visited = new HashSet<(int, int)>();

    while (track != 0 && visited.Add((track, sector))) {
      var off = GetSectorOffset(track, sector);
      if (off < 0 || off + SectorSize > _data.Length) break;

      var nextTrack = _data[off];
      var nextSector = _data[off + 1];

      if (nextTrack == 0) {
        // Last sector: nextSector = bytes used + 1 (includes the link bytes conceptually, minus 1 for data start offset)
        var bytesUsed = nextSector > 1 ? nextSector - 1 : 254;
        ms.Write(_data, off + 2, bytesUsed);
      } else {
        ms.Write(_data, off + 2, 254);
      }

      track = nextTrack;
      sector = nextSector;
    }

    return ms.ToArray();
  }

  public void Dispose() { }
}
