#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileSystem.D81;

/// <summary>
/// Walks a Commodore 1581 D81 image (819,200 bytes, 80 tracks × 40 sectors,
/// 256-byte sectors) and yields the actual on-disk byte layout — track 40
/// header + BAM1 + BAM2 + directory chain as metadata, every per-file
/// sector chain as a sequence of contiguous runs, and the remaining
/// sectors as Free.
/// </summary>
public static class D81ExtentMap {

  private const int SectorSize = 256;
  private const int SectorsPerTrack = 40;
  private const int TotalTracks = 80;
  private const int DirTrack = 40;
  private const int HeaderSector = 0;
  private const int Bam1Sector = 1;
  private const int Bam2Sector = 2;
  private const int DirStartSector = 3;
  private const int StandardSize = 819200;

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

    var totalSectors = TotalTracks * SectorsPerTrack;
    var owned = new bool[totalSectors];

    // Track 40 sector 0 — disk header. Sector 1+2 — BAMs. Mark all 3 as one
    // metadata run since they are contiguous on disk.
    yield return new DefragBlockInfo((long)GetSectorOffset(DirTrack, HeaderSector),
      3L * SectorSize, DefragBlockKind.MetadataReserved,
      FileName: "D81 header + BAM1 + BAM2 (track 40 sectors 0-2)");
    owned[LinearIndex(DirTrack, HeaderSector)] = true;
    owned[LinearIndex(DirTrack, Bam1Sector)] = true;
    owned[LinearIndex(DirTrack, Bam2Sector)] = true;

    // Walk directory chain (track 40 from sector 3) to collect file heads
    // and to mark visited dir sectors as metadata.
    var files = new List<(string name, int track, int sector)>();
    var dirRunStart = -1;
    var dirRunEnd = -1;
    {
      var t = DirTrack;
      var s = DirStartSector;
      var visited = new HashSet<(int, int)>();
      while (t != 0 && visited.Add((t, s))) {
        var off = GetSectorOffset(t, s);
        if (off < 0 || off + SectorSize > data.Length) break;
        var lin = LinearIndex(t, s);
        owned[lin] = true;
        if (dirRunStart < 0) { dirRunStart = lin; dirRunEnd = lin; }
        else if (lin == dirRunEnd + 1) dirRunEnd = lin;
        else {
          yield return new DefragBlockInfo(LinearIndexToOffset(dirRunStart),
            (long)(dirRunEnd - dirRunStart + 1) * SectorSize,
            DefragBlockKind.MetadataReserved, FileName: "D81 directory");
          dirRunStart = lin;
          dirRunEnd = lin;
        }

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
    if (dirRunStart >= 0)
      yield return new DefragBlockInfo(LinearIndexToOffset(dirRunStart),
        (long)(dirRunEnd - dirRunStart + 1) * SectorSize,
        DefragBlockKind.MetadataReserved, FileName: "D81 directory");

    foreach (var (name, startT, startS) in files) {
      var t = startT;
      var s = startS;
      var seen = new HashSet<(int, int)>();
      var runStartLin = -1;
      var runEndLin = -1;
      var runByteLen = 0L;

      while (t != 0 && t >= 1 && t <= TotalTracks && s < SectorsPerTrack && seen.Add((t, s))) {
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
    if (track < 1 || track > TotalTracks) return -1;
    if (sector < 0 || sector >= SectorsPerTrack) return -1;
    return ((track - 1) * SectorsPerTrack + sector) * SectorSize;
  }

  private static int LinearIndex(int track, int sector)
    => (track - 1) * SectorsPerTrack + sector;

  private static long LinearIndexToOffset(int lin) => (long)lin * SectorSize;
}
