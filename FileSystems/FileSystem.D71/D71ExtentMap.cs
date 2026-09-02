#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileSystem.D71;

/// <summary>
/// Walks a Commodore 1571 D71 image (349,696 bytes, 70 tracks, 256-byte
/// sectors, double-sided 1541 layout) and yields the actual on-disk byte
/// layout — track 18 (BAM side 1 + directory) and track 53 (BAM side 2)
/// as metadata, every per-file sector chain as a sequence of contiguous
/// runs, and the remaining sectors as Free.
/// </summary>
public static class D71ExtentMap {

  private const int SectorSize = 256;
  private const int DirTrack = 18;
  private const int Bam2Track = 53; // mirror BAM on side 2
  private const int DirStartSector = 1;
  private const int StandardSize = 349696;

  // Sectors per track for all 70 tracks — same table as D71Reader.
  private static readonly int[] SectorsPerTrack = [
    0,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17,
  ];

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < StandardSize) yield break;

    // Track 18 — BAM (sector 0) + directory chain (sectors 1..18). 19 sectors.
    var dir1Off = (long)GetSectorOffset(DirTrack, 0);
    yield return new DefragBlockInfo(dir1Off, (long)SectorsPerTrack[DirTrack] * SectorSize,
      DefragBlockKind.MetadataReserved, FileName: "D71 BAM + directory (track 18)");

    // Track 53 — BAM mirror for side 2 (sector 0); the rest of track 53 is
    // ordinary data, so emit only the single sector as metadata.
    var dir2Off = (long)GetSectorOffset(Bam2Track, 0);
    yield return new DefragBlockInfo(dir2Off, SectorSize,
      DefragBlockKind.MetadataReserved, FileName: "D71 BAM mirror (track 53 sector 0)");

    var totalSectors = 0;
    for (var t = 1; t < SectorsPerTrack.Length; t++) totalSectors += SectorsPerTrack[t];
    var owned = new bool[totalSectors];
    for (var sec = 0; sec < SectorsPerTrack[DirTrack]; sec++)
      owned[LinearIndex(DirTrack, sec)] = true;
    owned[LinearIndex(Bam2Track, 0)] = true;

    // Walk directory chain to collect file head T/S.
    var files = new List<(string name, int track, int sector)>();
    {
      var t = DirTrack;
      var s = DirStartSector;
      var visited = new HashSet<(int, int)>();
      while (t != 0 && visited.Add((t, s))) {
        var off = GetSectorOffset(t, s);
        if (off < 0 || off + SectorSize > data.Length) break;
        var nextTrack = data[off];
        var nextSector = data[off + 1];
        for (var i = 0; i < 8; i++) {
          var entryOff = off + i * 32;
          var fileType = data[entryOff + 2];
          if ((fileType & 0x07) == 0) continue;
          var startTrack = data[entryOff + 3];
          var startSector = data[entryOff + 4];
          var nameBytes = data.AsSpan(entryOff + 5, 16);
          var nameEnd = nameBytes.IndexOf((byte)0xA0);
          if (nameEnd < 0) nameEnd = 16;
          var name = Encoding.ASCII.GetString(data, entryOff + 5, nameEnd);
          files.Add((name, startTrack, startSector));
        }
        t = nextTrack;
        s = nextSector;
      }
    }

    foreach (var (name, startT, startS) in files) {
      var t = startT;
      var s = startS;
      var seen = new HashSet<(int, int)>();
      var runStartLin = -1;
      var runEndLin = -1;
      var runByteLen = 0L;

      while (t != 0 && t < SectorsPerTrack.Length && s < SectorsPerTrack[t] && seen.Add((t, s))) {
        var lin = LinearIndex(t, s);
        owned[lin] = true;
        var off = GetSectorOffset(t, s);
        if (off < 0 || off + SectorSize > data.Length) break;
        var nextT = data[off];
        var nextS = data[off + 1];
        if (runStartLin < 0) {
          runStartLin = lin;
          runEndLin = lin;
          runByteLen = SectorSize;
        } else if (lin == runEndLin + 1) {
          runEndLin = lin;
          runByteLen += SectorSize;
        } else {
          yield return new DefragBlockInfo(LinearIndexToOffset(runStartLin), runByteLen,
            DefragBlockKind.Used, name);
          runStartLin = lin;
          runEndLin = lin;
          runByteLen = SectorSize;
        }
        t = nextT;
        s = nextS;
      }
      if (runStartLin >= 0)
        yield return new DefragBlockInfo(LinearIndexToOffset(runStartLin), runByteLen,
          DefragBlockKind.Used, name);
    }

    // Free runs.
    {
      var freeStart = -1;
      for (var lin = 0; lin < totalSectors; lin++) {
        if (!owned[lin]) {
          if (freeStart < 0) freeStart = lin;
        } else if (freeStart >= 0) {
          yield return new DefragBlockInfo(LinearIndexToOffset(freeStart),
            (long)(lin - freeStart) * SectorSize, DefragBlockKind.Free);
          freeStart = -1;
        }
      }
      if (freeStart >= 0)
        yield return new DefragBlockInfo(LinearIndexToOffset(freeStart),
          (long)(totalSectors - freeStart) * SectorSize, DefragBlockKind.Free);
    }
  }

  private static int GetSectorOffset(int track, int sector) {
    if (track < 1 || track >= SectorsPerTrack.Length) return -1;
    if (sector < 0 || sector >= SectorsPerTrack[track]) return -1;
    var offset = 0;
    for (var t = 1; t < track; t++)
      offset += SectorsPerTrack[t] * SectorSize;
    offset += sector * SectorSize;
    return offset;
  }

  private static int LinearIndex(int track, int sector) {
    var idx = 0;
    for (var t = 1; t < track; t++) idx += SectorsPerTrack[t];
    return idx + sector;
  }

  private static long LinearIndexToOffset(int lin) => (long)lin * SectorSize;
}
