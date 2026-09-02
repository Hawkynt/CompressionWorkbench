#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileSystem.TrDos;

/// <summary>
/// Walks a ZX Spectrum TR-DOS (.trd) disk image (640 KB DSDD: 160 tracks, 16
/// sectors/track, 256 bytes/sector) and yields the actual on-disk byte layout —
/// the 8-sector directory at track 0 sectors 0..7 plus the disk-info sector at
/// 0x800 as <see cref="DefragBlockKind.MetadataReserved"/>, every per-file
/// contiguous-sector run as a <see cref="DefragBlockKind.Used"/> extent, and
/// the rest as <see cref="DefragBlockKind.Free"/>.
/// </summary>
public static class TrDosExtentMap {

  private const int SectorSize = 256;
  private const int SectorsPerTrack = 16;
  private const int TrackSize = SectorSize * SectorsPerTrack;
  private const int DirEntrySize = 16;
  private const int MaxDirEntries = 128; // 8 sectors × 256 / 16
  private const int DirBytes = 8 * SectorSize; // track 0 sectors 0..7 hold the directory
  private const int DiskInfoOffset = 0x800; // track 0 sector 8

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < DiskInfoOffset + SectorSize) yield break;

    var totalBytes = data.Length;
    // TR-DOS canonical sizes: 640 KB (DSDD) or 320 KB (SS); honour whatever
    // the image actually has.
    var totalSectors = totalBytes / SectorSize;

    // Directory: track 0, sectors 0..7 = bytes [0 .. 0x800).
    yield return new DefragBlockInfo(0, DirBytes, DefragBlockKind.MetadataReserved,
      FileName: "TR-DOS directory (track 0 sectors 0-7)");

    // Disk-info: track 0 sector 8 = bytes [0x800 .. 0x900).
    yield return new DefragBlockInfo(DiskInfoOffset, SectorSize,
      DefragBlockKind.MetadataReserved, FileName: "TR-DOS disk info sector");

    // The remaining 7 sectors of track 0 (sectors 9..15) are unused/reserved
    // by TR-DOS — files start at track 1 sector 0 typically. Mark them as
    // free space so the layout is honest about coverage.
    var owned = new bool[totalSectors];
    for (var s = 0; s < 9; s++) {
      var lin = 0 * SectorsPerTrack + s;
      if (lin < owned.Length) owned[lin] = true; // dir + disk info
    }

    // Walk the directory entries (track 0 sectors 0..7).
    for (var i = 0; i < MaxDirEntries; i++) {
      var entryOff = i * DirEntrySize;
      if (entryOff + DirEntrySize > DirBytes) break;
      var firstByte = data[entryOff];
      if (firstByte == 0x00) break;       // end of directory
      if (firstByte == 0x01) continue;    // deleted

      var nameStr = Encoding.ASCII.GetString(data, entryOff, 8).TrimEnd();
      var ext = (char)data[entryOff + 8];
      var fullName = ext switch {
        'B' => nameStr + ".bas",
        'C' => nameStr + ".cod",
        'D' => nameStr + ".dat",
        '#' => nameStr + ".seq",
        _ => nameStr + "." + ext,
      };

      var lengthSectors = data[entryOff + 13];
      var startSector = data[entryOff + 14];
      var startTrack = data[entryOff + 15];

      if (lengthSectors == 0) continue;
      var fileOff = (long)startTrack * TrackSize + (long)startSector * SectorSize;
      var fileLen = (long)lengthSectors * SectorSize;
      if (fileOff + fileLen > totalBytes) {
        var trimmed = totalBytes - fileOff;
        if (trimmed <= 0) continue;
        fileLen = trimmed;
      }

      // TR-DOS files are stored contiguously — emit one Used extent.
      yield return new DefragBlockInfo(fileOff, fileLen, DefragBlockKind.Used, fullName);

      var firstLin = startTrack * SectorsPerTrack + startSector;
      var lastLin = firstLin + lengthSectors;
      for (var lin = firstLin; lin < lastLin && lin < owned.Length; lin++)
        owned[lin] = true;
    }

    // Emit Free runs for unowned sectors.
    var freeStart = -1;
    for (var lin = 0; lin < owned.Length; lin++) {
      if (!owned[lin]) {
        if (freeStart < 0) freeStart = lin;
      } else if (freeStart >= 0) {
        yield return new DefragBlockInfo((long)freeStart * SectorSize,
          (long)(lin - freeStart) * SectorSize, DefragBlockKind.Free);
        freeStart = -1;
      }
    }
    if (freeStart >= 0) {
      yield return new DefragBlockInfo((long)freeStart * SectorSize,
        (long)(owned.Length - freeStart) * SectorSize, DefragBlockKind.Free);
    }
  }
}
