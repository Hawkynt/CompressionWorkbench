#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.D64;

/// <summary>
/// Walks a Commodore 1541 D64 image (174,848 bytes, 35 tracks, 256-byte
/// sectors, zoned 21/19/18/17 sectors per track) and yields the actual
/// on-disk byte layout — track 18 (BAM + directory) as metadata, every
/// per-file sector chain as a sequence of contiguous-run extents, and the
/// remaining sectors as Free. Used by the defragment window's block-map
/// preview.
/// </summary>
public static class D64ExtentMap {

  private const int SectorSize = 256;
  private const int DirTrack = 18;
  private const int DirStartSector = 1;

  // Sectors per track for each zone — same table as D64Reader.
  private static readonly int[] SectorsPerTrack = [
    0,
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
    if (data.Length < 174848) yield break;

    // Track 18 — BAM (sector 0) + directory (sectors 1..18) — covers the
    // metadata region as one contiguous run. 19 sectors × 256 = 4864 bytes.
    var dirOff = (long)GetSectorOffset(DirTrack, 0);
    yield return new DefragBlockInfo(dirOff, (long)SectorsPerTrack[DirTrack] * SectorSize,
      DefragBlockKind.MetadataReserved, FileName: "D64 BAM + directory (track 18)");

    // Walk the directory chain to collect all per-file T/S head + size info.
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

    // Track every (track, sector) we attribute to a file, so the un-attributed
    // set becomes the Free extent space after this loop. Pre-mark track 18 as
    // owned (we already emitted it as metadata).
    var totalSectors = 0;
    for (var t = 1; t < SectorsPerTrack.Length; t++) totalSectors += SectorsPerTrack[t];
    var owned = new bool[totalSectors]; // linear sector index across all tracks
    for (var sec = 0; sec < SectorsPerTrack[DirTrack]; sec++)
      owned[LinearIndex(DirTrack, sec)] = true;

    foreach (var (name, startT, startS) in files) {
      var t = (int)startT;
      var s = (int)startS;
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
        // Each sector of a chain contributes one full 256-byte sector to the
        // on-disk footprint (whether or not the last sector's data fills it).
        if (runStartLin < 0) {
          runStartLin = lin;
          runEndLin = lin;
          runByteLen = SectorSize;
        } else if (lin == runEndLin + 1) {
          runEndLin = lin;
          runByteLen += SectorSize;
        } else {
          // Flush previous run.
          var off2 = LinearIndexToOffset(runStartLin);
          yield return new DefragBlockInfo(off2, runByteLen, DefragBlockKind.Used, name);
          runStartLin = lin;
          runEndLin = lin;
          runByteLen = SectorSize;
        }
        t = nextT;
        s = nextS;
      }
      if (runStartLin >= 0) {
        var off2 = LinearIndexToOffset(runStartLin);
        yield return new DefragBlockInfo(off2, runByteLen, DefragBlockKind.Used, name);
      }
    }

    // Free sectors — collapse runs of unowned sectors.
    {
      var freeStart = -1;
      for (var lin = 0; lin < totalSectors; lin++) {
        if (!owned[lin]) {
          if (freeStart < 0) freeStart = lin;
        } else if (freeStart >= 0) {
          var off = LinearIndexToOffset(freeStart);
          var len = (long)(lin - freeStart) * SectorSize;
          yield return new DefragBlockInfo(off, len, DefragBlockKind.Free);
          freeStart = -1;
        }
      }
      if (freeStart >= 0) {
        var off = LinearIndexToOffset(freeStart);
        var len = (long)(totalSectors - freeStart) * SectorSize;
        yield return new DefragBlockInfo(off, len, DefragBlockKind.Free);
      }
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
