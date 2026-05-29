#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Atari8;

/// <summary>
/// Walks an Atari 8-bit ATR image (AtariDOS 2.x) and yields the actual
/// on-disk byte layout — 16-byte ATR header + sector 360 (VTOC) +
/// sectors 361-368 (directory) as metadata, every per-file sector chain
/// as one or more contiguous-run extents (chain followed via the 3-byte
/// trailer), and the un-attributed sectors as Free.
/// </summary>
public static class Atari8ExtentMap {

  private const int AtrHeaderSize = 16;
  private const int VtocSector = 360;
  private const int DirectoryStartSector = 361;
  private const int DirectorySectorCount = 8;
  private const int EntriesPerDirectorySector = 8;
  private const int DirectoryEntrySize = 16;
  private const int TotalSectors = 720;

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < AtrHeaderSize + 128 * 3) yield break;
    if (data[0] != 0x96 || data[1] != 0x02) yield break;

    var rawSectorSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
    var sectorSize = rawSectorSize == 0 ? 128 : rawSectorSize;
    if (sectorSize is not (128 or 256)) yield break;

    // Atari sectors are 1-based. Track which logical sector numbers are owned.
    var owned = new bool[TotalSectors + 1]; // index 1..720

    // ATR header is metadata.
    yield return new DefragBlockInfo(0, AtrHeaderSize, DefragBlockKind.MetadataReserved,
      FileName: "ATR header");

    int SectorOffset(int sector1Based) {
      // Per Atari8Reader: in DD images, sectors 1..3 are still 128 bytes.
      if (sectorSize == 256 && sector1Based <= 3)
        return AtrHeaderSize + (sector1Based - 1) * 128;
      var headStart = AtrHeaderSize + (sectorSize == 256 ? 3 * 128 : 0);
      var idx = sector1Based - 1 - (sectorSize == 256 ? 3 : 0);
      return sectorSize == 256
        ? headStart + idx * 256
        : AtrHeaderSize + (sector1Based - 1) * 128;
    }

    // VTOC sector 360 (single sector).
    var vtocOff = SectorOffset(VtocSector);
    if (vtocOff + sectorSize <= data.Length) {
      yield return new DefragBlockInfo(vtocOff, sectorSize,
        DefragBlockKind.MetadataReserved, FileName: "AtariDOS VTOC (sector 360)");
      owned[VtocSector] = true;
    }

    // Directory sectors 361..368 — emit as one contiguous metadata run.
    var dirStartOff = SectorOffset(DirectoryStartSector);
    var dirEndOff = SectorOffset(DirectoryStartSector + DirectorySectorCount - 1) + sectorSize;
    if (dirEndOff <= data.Length) {
      yield return new DefragBlockInfo(dirStartOff, dirEndOff - dirStartOff,
        DefragBlockKind.MetadataReserved, FileName: "AtariDOS directory (sectors 361-368)");
      for (var s = DirectoryStartSector; s < DirectoryStartSector + DirectorySectorCount; s++)
        owned[s] = true;
    }

    // Walk directory sectors to gather files.
    var fileHeads = new List<(string name, int startSector)>();
    var stop = false;
    for (var i = 0; i < DirectorySectorCount && !stop; i++) {
      var sectorNo = DirectoryStartSector + i;
      var off = SectorOffset(sectorNo);
      if (off + sectorSize > data.Length) break;
      for (var j = 0; j < EntriesPerDirectorySector; j++) {
        var eo = off + j * DirectoryEntrySize;
        var flags = data[eo + 0];
        if (flags == 0x00) { stop = true; break; }
        if ((flags & 0x80) != 0) continue;
        if ((flags & 0x40) == 0) continue;
        var startSector = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(eo + 3));
        var nameStr = Encoding.ASCII.GetString(data, eo + 5, 8).TrimEnd();
        var extStr = Encoding.ASCII.GetString(data, eo + 13, 3).TrimEnd();
        var fullName = extStr.Length > 0 ? nameStr + "." + extStr : nameStr;
        if (string.IsNullOrEmpty(fullName)) continue;
        fileHeads.Add((fullName, startSector));
      }
    }

    // Per-file sector chain → contiguous-run extents (sorted by sector number
    // since AtariDOS chains can be non-monotonic).
    foreach (var (name, startSector) in fileHeads) {
      var sectors = new List<int>();
      var visited = new HashSet<int>();
      var cur = startSector;
      while (cur != 0 && cur >= 1 && cur <= TotalSectors && visited.Add(cur)) {
        var off = SectorOffset(cur);
        if (off + sectorSize > data.Length) break;
        sectors.Add(cur);
        var b0 = data[off + sectorSize - 3];
        var b1 = data[off + sectorSize - 2];
        var next = ((b0 & 0x03) << 8) | b1;
        if (next == 0) break;
        cur = next;
      }
      sectors.Sort();
      var runStart = -1;
      var runEnd = -1;
      foreach (var s in sectors) {
        if (s < 1 || s > TotalSectors) continue;
        owned[s] = true;
        if (runStart < 0) { runStart = s; runEnd = s; }
        else if (s == runEnd + 1) runEnd = s;
        else {
          var off = SectorOffset(runStart);
          var endOff = SectorOffset(runEnd) + sectorSize;
          yield return new DefragBlockInfo(off, endOff - off, DefragBlockKind.Used, name);
          runStart = s; runEnd = s;
        }
      }
      if (runStart >= 0) {
        var off = SectorOffset(runStart);
        var endOff = SectorOffset(runEnd) + sectorSize;
        yield return new DefragBlockInfo(off, endOff - off, DefragBlockKind.Used, name);
      }
    }

    // Free runs (1-based sectors 1..720).
    {
      var freeStart = -1;
      for (var s = 1; s <= TotalSectors; s++) {
        var off = SectorOffset(s);
        if (off + sectorSize > data.Length) break;
        if (!owned[s]) {
          if (freeStart < 0) freeStart = s;
        } else if (freeStart >= 0) {
          var startOff = SectorOffset(freeStart);
          var endOff = SectorOffset(s - 1) + sectorSize;
          yield return new DefragBlockInfo(startOff, endOff - startOff, DefragBlockKind.Free);
          freeStart = -1;
        }
      }
      if (freeStart >= 0) {
        var startOff = SectorOffset(freeStart);
        var endOff = SectorOffset(TotalSectors) + sectorSize;
        if (endOff <= data.Length)
          yield return new DefragBlockInfo(startOff, endOff - startOff, DefragBlockKind.Free);
      }
    }
  }
}
