#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.AppleDos;

/// <summary>
/// Reader for Apple DOS 3.3 <c>.dsk</c>/<c>.do</c> disk images.
/// </summary>
/// <remarks>
/// Layout: 35 tracks x 16 sectors x 256 bytes = 143 360 bytes. Catalog track is 17.
/// VTOC (track 17, sector 0) points to the first catalog sector. Each catalog sector
/// holds 7 x 35-byte directory entries. Each entry has a track/sector pointer to a
/// "T/S list" sector whose body is an array of (track, sector) pairs pointing at the
/// file's data sectors. A file may span multiple chained T/S list sectors.
/// </remarks>
public sealed class AppleDosReader : IDisposable {

  public const int StandardSize = 143360;
  public const int TracksPerDisk = 35;
  public const int SectorsPerTrack = 16;
  public const int SectorSize = 256;
  public const int CatalogTrack = 17;
  public const int VtocSector = 0;

  private readonly byte[] _data;
  private readonly List<AppleDosEntry> _entries = [];

  public IReadOnlyList<AppleDosEntry> Entries => _entries;

  public AppleDosReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  public AppleDosReader(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    _data = data;
    Parse();
  }

  private static int SectorOffset(int track, int sector) =>
    track * SectorsPerTrack * SectorSize + sector * SectorSize;

  private void Parse() {
    if (_data.Length < StandardSize)
      throw new InvalidDataException("AppleDOS: image too small (expected 143360 bytes).");

    // Read VTOC. Only sanity-check: byte 3 = DOS version (typically 3), track 17 bytes
    // 1-2 are track/sector of first catalog sector. In a valid DOS 3.3 VTOC,
    // sectors-per-track = 16 (byte 0x35).
    var vtocOff = SectorOffset(CatalogTrack, VtocSector);
    var firstCatTrack = _data[vtocOff + 0x01];
    var firstCatSector = _data[vtocOff + 0x02];
    var sectorsPerTrackInVtoc = _data[vtocOff + 0x35];

    if (sectorsPerTrackInVtoc != SectorsPerTrack)
      throw new InvalidDataException("AppleDOS: VTOC sectors-per-track byte is not 16; not a DOS 3.3 disk.");

    if (firstCatTrack != CatalogTrack)
      throw new InvalidDataException("AppleDOS: VTOC first-catalog track does not match catalog track 17.");

    ReadCatalog(firstCatTrack, firstCatSector);
  }

  private void ReadCatalog(int track, int sector) {
    var visited = new HashSet<(int, int)>();
    while (track != 0 && visited.Add((track, sector))) {
      if (track < 0 || track >= TracksPerDisk || sector < 0 || sector >= SectorsPerTrack) break;
      var off = SectorOffset(track, sector);
      var nextTrack = _data[off + 0x01];
      var nextSector = _data[off + 0x02];

      // 7 entries of 35 bytes starting at offset 0x0B
      for (var i = 0; i < 7; i++) {
        var eo = off + 0x0B + i * 35;
        var tsTrack = _data[eo + 0];
        var tsSector = _data[eo + 1];

        // 0x00 = never used; 0xFF = deleted; anything else is a real entry.
        if (tsTrack == 0x00 || tsTrack == 0xFF) continue;

        var fileType = _data[eo + 2];

        // Filename: 30 bytes, high-bit ASCII, padded with 0xA0.
        var nameBuf = new byte[30];
        for (var j = 0; j < 30; j++) nameBuf[j] = (byte)(_data[eo + 3 + j] & 0x7F);
        var nameLen = 30;
        while (nameLen > 0 && nameBuf[nameLen - 1] == (0xA0 & 0x7F)) nameLen--;
        var name = Encoding.ASCII.GetString(nameBuf, 0, nameLen).TrimEnd();

        var sectorCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(eo + 33));

        var size = ComputeFileSize(tsTrack, tsSector, fileType);

        _entries.Add(new AppleDosEntry {
          Name = name,
          Size = size,
          FileType = fileType,
          SectorCount = sectorCount,
          TrackSectorListTrack = tsTrack,
          TrackSectorListSector = tsSector,
        });
      }

      track = nextTrack;
      sector = nextSector;
    }
  }

  /// <summary>
  /// Computes the logical file size. DOS 3.3 stores file length inside the data for
  /// Applesoft / Integer BASIC (2-byte LE length at start) and Binary (2-byte load
  /// address at start + 2-byte LE length). For text files and unknown types we fall
  /// back to <c>dataSectorCount * 256</c>.
  /// </summary>
  private int ComputeFileSize(int tsTrack, int tsSector, byte fileType) {
    var dataSectors = CollectDataSectors(tsTrack, tsSector);
    if (dataSectors.Count == 0) return 0;

    var typeNibble = fileType & 0x7F;
    var firstOff = SectorOffset(dataSectors[0].Track, dataSectors[0].Sector);

    switch (typeNibble) {
      case 0x01: // Integer BASIC — 2-byte LE length prefix
      case 0x02: // Applesoft BASIC — 2-byte LE length prefix
      {
        var len = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(firstOff));
        return Math.Min(len + 2, dataSectors.Count * SectorSize);
      }
      case 0x04: // Binary — 2-byte load addr, 2-byte LE length, then data
      {
        if (dataSectors.Count * SectorSize < 4) return dataSectors.Count * SectorSize;
        var len = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(firstOff + 2));
        return Math.Min(len + 4, dataSectors.Count * SectorSize);
      }
      default:
        return dataSectors.Count * SectorSize;
    }
  }

  private List<(int Track, int Sector)> CollectDataSectors(int tsTrack, int tsSector) {
    var result = new List<(int, int)>();
    var visited = new HashSet<(int, int)>();
    var track = tsTrack;
    var sector = tsSector;

    while (track != 0 && visited.Add((track, sector))) {
      if (track < 0 || track >= TracksPerDisk || sector < 0 || sector >= SectorsPerTrack) break;
      var off = SectorOffset(track, sector);
      var nextTrack = _data[off + 0x01];
      var nextSector = _data[off + 0x02];

      // T/S list body: 122 pairs starting at offset 0x0C. A (0,0) pair terminates.
      for (var i = 0; i < 122; i++) {
        var pairOff = off + 0x0C + i * 2;
        var dTrack = _data[pairOff + 0];
        var dSector = _data[pairOff + 1];
        if (dTrack == 0 && dSector == 0) {
          // "Sparse" DOS allows (0,0) placeholders; conservative readers treat it as EOF.
          return result;
        }
        result.Add((dTrack, dSector));
      }

      track = nextTrack;
      sector = nextSector;
    }

    return result;
  }

  public byte[] Extract(AppleDosEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var sectors = CollectDataSectors(entry.TrackSectorListTrack, entry.TrackSectorListSector);
    if (sectors.Count == 0) return [];

    var buf = new byte[sectors.Count * SectorSize];
    for (var i = 0; i < sectors.Count; i++) {
      var so = SectorOffset(sectors[i].Track, sectors[i].Sector);
      Buffer.BlockCopy(_data, so, buf, i * SectorSize, SectorSize);
    }

    // Trim to logical size.
    var logical = entry.Size;
    if (logical <= 0 || logical > buf.Length) return buf;
    var trimmed = new byte[logical];
    Buffer.BlockCopy(buf, 0, trimmed, 0, (int)logical);
    return trimmed;
  }

  public void Dispose() { }
}
