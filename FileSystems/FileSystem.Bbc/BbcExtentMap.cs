#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileSystem.Bbc;

/// <summary>
/// Walks a BBC Micro Acorn DFS image (.ssd / .dsd, 256-byte sectors,
/// 10 sectors/track) and yields its actual on-disk byte layout — sectors
/// 0+1 of each side as the catalog (metadata), every per-file
/// (start_sector, length) extent as a single contiguous run, and
/// the unallocated sectors as Free.
/// </summary>
public static class BbcExtentMap {

  private const int SectorSize = 256;
  private const int SectorsPerTrack = 10;

    /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < SectorSize * 2) yield break;

    // Detect single vs double sided by file size.
    var doubleSided = data.Length is 400_000;
    var sideSize = doubleSided ? data.Length / 2 : data.Length;
    var sides = doubleSided ? 2 : 1;
    var totalSectors = data.Length / SectorSize;
    var owned = new bool[totalSectors];

    for (var side = 0; side < sides; side++) {
      var sideBase = side * sideSize;
      var sector0 = sideBase;
      var sector1 = sideBase + SectorSize;
      if (sector1 + SectorSize > data.Length) break;

      // Catalog = 2 sectors at start of each side.
      yield return new DefragBlockInfo(sector0, 2L * SectorSize,
        DefragBlockKind.MetadataReserved,
        FileName: doubleSided ? $"BBC DFS catalog (side {side})" : "BBC DFS catalog");
      owned[sector0 / SectorSize] = true;
      owned[sector1 / SectorSize] = true;

      var entriesTimesEight = data[sector1 + 5];
      var entryCount = entriesTimesEight / 8;
      if (entryCount > 31) entryCount = 31;

      for (var i = 0; i < entryCount; i++) {
        var nameOff = sector0 + 8 + i * 8;
        var metaOff = sector1 + 8 + i * 8;
        if (nameOff + 8 > data.Length || metaOff + 8 > data.Length) break;

        var nameBuf = new byte[7];
        Array.Copy(data, nameOff, nameBuf, 0, 7);
        var name = Encoding.ASCII.GetString(nameBuf).TrimEnd();
        var dirByte = data[nameOff + 7];
        var dirChar = (char)(dirByte & 0x7F);
        if (dirChar < 0x20 || dirChar > 0x7E) dirChar = '$';
        var fullName = $"{dirChar}.{name}";

        var lengthLo = (uint)(data[metaOff + 4] | (data[metaOff + 5] << 8));
        var packed = data[metaOff + 6];
        var startSectorLo = data[metaOff + 7];
        var startSectorHi = packed & 0x03;
        var lengthHi = (packed >> 4) & 0x03;
        var startSector = (startSectorHi << 8) | startSectorLo;
        var length = ((uint)lengthHi << 16) | lengthLo;

        if (length == 0) continue;

        var byteStart = sideBase + (long)startSector * SectorSize;
        var lenBytes = (long)length;
        var sectorsUsed = (lenBytes + SectorSize - 1) / SectorSize;

        if (byteStart < sideBase || byteStart + lenBytes > sideBase + sideSize) continue;

        for (var s = 0; s < sectorsUsed; s++) {
          var idx = (int)((byteStart + s * SectorSize) / SectorSize);
          if (idx >= 0 && idx < totalSectors) owned[idx] = true;
        }

        // Emit the contiguous run as one extent (length in bytes is the file's
        // logical size, but the on-disk footprint is the rounded-up sector count).
        yield return new DefragBlockInfo(byteStart, sectorsUsed * SectorSize,
          DefragBlockKind.Used, fullName);
      }
    }

    // Free runs.
    {
      var freeStart = -1;
      for (var s = 0; s < totalSectors; s++) {
        if (!owned[s]) {
          if (freeStart < 0) freeStart = s;
        } else if (freeStart >= 0) {
          yield return new DefragBlockInfo((long)freeStart * SectorSize,
            (long)(s - freeStart) * SectorSize, DefragBlockKind.Free);
          freeStart = -1;
        }
      }
      if (freeStart >= 0)
        yield return new DefragBlockInfo((long)freeStart * SectorSize,
          (long)(totalSectors - freeStart) * SectorSize, DefragBlockKind.Free);
    }
  }
}
