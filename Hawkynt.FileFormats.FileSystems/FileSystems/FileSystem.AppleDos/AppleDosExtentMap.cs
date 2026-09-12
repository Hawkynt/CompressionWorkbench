#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileSystem.AppleDos;

/// <summary>
/// Walks an Apple DOS 3.3 image (143,360 bytes, 35 tracks × 16 sectors,
/// 256-byte sectors) and yields its actual on-disk byte layout — track 17
/// VTOC + catalog as metadata, every per-file (T/S list + data) sector
/// chain as contiguous-run extents, and unallocated sectors as Free.
/// </summary>
public static class AppleDosExtentMap {

  private const int TracksPerDisk = 35;
  private const int SectorsPerTrack = 16;
  private const int SectorSize = 256;
  private const int CatalogTrack = 17;
  private const int VtocSector = 0;
  private const int StandardSize = 143360;

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

    var totalSectors = TracksPerDisk * SectorsPerTrack;
    var owned = new bool[totalSectors];

    // VTOC = track 17 sector 0. Catalog = track 17 sectors 1..15 (linked
    // chain — we walk it). Mark the entire track 17 as metadata in one run
    // since the catalog typically occupies the whole track.
    var vtocLin = LinearIndex(CatalogTrack, VtocSector);
    yield return new DefragBlockInfo(LinearIndexToOffset(vtocLin),
      (long)SectorsPerTrack * SectorSize, DefragBlockKind.MetadataReserved,
      FileName: "AppleDOS VTOC + catalog (track 17)");
    for (var s = 0; s < SectorsPerTrack; s++)
      owned[LinearIndex(CatalogTrack, s)] = true;

    // Read VTOC and walk catalog chain.
    var vtocOff = SectorOffset(CatalogTrack, VtocSector);
    var firstCatTrack = (int)data[vtocOff + 0x01];
    var firstCatSector = (int)data[vtocOff + 0x02];
    var sectorsPerTrackInVtoc = data[vtocOff + 0x35];
    if (sectorsPerTrackInVtoc != SectorsPerTrack) yield break;

    var fileHeads = new List<(string name, int tsTrack, int tsSector)>();
    {
      var t = firstCatTrack;
      var s = firstCatSector;
      var seen = new HashSet<(int, int)>();
      while (t != 0 && seen.Add((t, s))) {
        if (t < 0 || t >= TracksPerDisk || s < 0 || s >= SectorsPerTrack) break;
        var off = SectorOffset(t, s);
        var nextT = data[off + 0x01];
        var nextS = data[off + 0x02];
        for (var i = 0; i < 7; i++) {
          var eo = off + 0x0B + i * 35;
          var tsTrack = data[eo + 0];
          var tsSector = data[eo + 1];
          if (tsTrack == 0x00 || tsTrack == 0xFF) continue;
          var nameBuf = new byte[30];
          for (var j = 0; j < 30; j++) nameBuf[j] = (byte)(data[eo + 3 + j] & 0x7F);
          var nameLen = 30;
          while (nameLen > 0 && nameBuf[nameLen - 1] == (0xA0 & 0x7F)) nameLen--;
          var name = Encoding.ASCII.GetString(nameBuf, 0, nameLen).TrimEnd();
          fileHeads.Add((name, tsTrack, tsSector));
        }
        t = nextT;
        s = nextS;
      }
    }

    // For each file: walk T/S list chain. Both T/S list sectors and the
    // data sectors they reference belong to the file. Yield contiguous
    // sector runs across both kinds (linear sector index ordering).
    foreach (var (name, tsT, tsS) in fileHeads) {
      var sectorList = new List<(int t, int s)>();
      var t = tsT;
      var s = tsS;
      var visited = new HashSet<(int, int)>();
      while (t != 0 && visited.Add((t, s))) {
        if (t < 0 || t >= TracksPerDisk || s < 0 || s >= SectorsPerTrack) break;
        sectorList.Add((t, s)); // T/S list sector itself
        var off = SectorOffset(t, s);
        var nextT = data[off + 0x01];
        var nextS = data[off + 0x02];
        for (var i = 0; i < 122; i++) {
          var pairOff = off + 0x0C + i * 2;
          var dT = data[pairOff + 0];
          var dS = data[pairOff + 1];
          if (dT == 0 && dS == 0) break;
          if (dT >= TracksPerDisk || dS >= SectorsPerTrack) continue;
          sectorList.Add((dT, dS));
        }
        t = nextT;
        s = nextS;
      }

      // Coalesce by linear sector index.
      var runStartLin = -1;
      var runEndLin = -1;
      var runByteLen = 0L;
      foreach (var (dT, dS) in sectorList) {
        var lin = LinearIndex(dT, dS);
        if (lin < 0 || lin >= totalSectors) continue;
        owned[lin] = true;
        if (runStartLin < 0) {
          runStartLin = lin; runEndLin = lin; runByteLen = SectorSize;
        } else if (lin == runEndLin + 1) {
          runEndLin = lin; runByteLen += SectorSize;
        } else {
          yield return new DefragBlockInfo(LinearIndexToOffset(runStartLin), runByteLen,
            DefragBlockKind.Used, name);
          runStartLin = lin; runEndLin = lin; runByteLen = SectorSize;
        }
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

  private static int SectorOffset(int track, int sector)
    => track * SectorsPerTrack * SectorSize + sector * SectorSize;

  private static int LinearIndex(int track, int sector)
    => track * SectorsPerTrack + sector;

  private static long LinearIndexToOffset(int lin) => (long)lin * SectorSize;
}
