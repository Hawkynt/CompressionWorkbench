#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.D71;

public sealed class D71Reader : IDisposable {
  private readonly byte[] _data;
  private readonly List<D71Entry> _entries = [];

  public IReadOnlyList<D71Entry> Entries => _entries;

  // Standard D71 size: 70 tracks (double-sided 1571)
  private const int StandardSize = 349696;
  private const int SectorSize = 256;

  // Track 18 is the directory track
  private const int DirTrack = 18;
  private const int DirStartSector = 1;

  // Sectors per track for all 70 tracks
  private static readonly int[] SectorsPerTrack = [
    0, // track 0 doesn't exist
    // Side 1: tracks 1-35 (same as D64)
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, // 1-17
    19, 19, 19, 19, 19, 19, 19, // 18-24
    18, 18, 18, 18, 18, 18, // 25-30
    17, 17, 17, 17, 17, // 31-35
    // Side 2: tracks 36-70 (mirrors side 1)
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, // 36-52
    19, 19, 19, 19, 19, 19, 19, // 53-59
    18, 18, 18, 18, 18, 18, // 60-65
    17, 17, 17, 17, 17 // 66-70
  ];

  public D71Reader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < StandardSize)
      throw new InvalidDataException("D71: image too small.");

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

      var nextTrack = _data[offset];
      var nextSector = _data[offset + 1];

      for (var i = 0; i < 8; i++) {
        var entryOff = offset + i * 32;

        var fileType = _data[entryOff + 2];
        if ((fileType & 0x07) == 0) continue;

        var startTrack = _data[entryOff + 3];
        var startSector = _data[entryOff + 4];

        var nameBytes = _data.AsSpan(entryOff + 5, 16);
        var nameEnd = nameBytes.IndexOf((byte)0xA0);
        if (nameEnd < 0) nameEnd = 16;
        var name = Encoding.ASCII.GetString(_data, entryOff + 5, nameEnd);

        var sectorCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(entryOff + 30));

        var actualSize = CalculateFileSize(startTrack, startSector, sectorCount);

        _entries.Add(new D71Entry {
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
        return (totalSectors - 1) * 254 + (nextSector - 1);
      }
      track = nextTrack;
      sector = nextSector;
    }
    return sectorCount * 254;
  }

  public byte[] Extract(D71Entry entry) {
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
