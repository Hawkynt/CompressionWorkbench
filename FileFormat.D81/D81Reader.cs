#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.D81;

public sealed class D81Reader : IDisposable {
  private readonly byte[] _data;
  private readonly List<D81Entry> _entries = [];

  public IReadOnlyList<D81Entry> Entries => _entries;

  // Standard D81 size: 80 tracks x 40 sectors
  private const int StandardSize = 819200;
  private const int SectorSize = 256;
  private const int SectorsPerTrackConst = 40;
  private const int TotalTracks = 80;

  // Track 40 is the directory/header track
  private const int DirTrack = 40;
  private const int HeaderSector = 0;  // disk header
  private const int Bam1Sector = 1;    // BAM for tracks 1-40
  private const int Bam2Sector = 2;    // BAM for tracks 41-80
  private const int DirStartSector = 3; // first directory sector

  public D81Reader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < StandardSize)
      throw new InvalidDataException("D81: image too small.");

    ReadDirectory();
  }

  private static int GetSectorOffset(int track, int sector) {
    if (track < 1 || track > TotalTracks) return -1;
    if (sector < 0 || sector >= SectorsPerTrackConst) return -1;

    return ((track - 1) * SectorsPerTrackConst + sector) * SectorSize;
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

        _entries.Add(new D81Entry {
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

  public byte[] Extract(D81Entry entry) {
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
