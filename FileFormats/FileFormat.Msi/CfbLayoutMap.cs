#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.Msi;

/// <summary>
/// Walks the OLE Compound File Binary (CFB) structure and emits the byte-level
/// layout: header, FAT sectors, DIFAT sectors, directory sectors, mini-FAT
/// sectors, mini-stream container sectors, and each stream's sector chain as
/// <see cref="DefragBlockInfo"/> tiles.
/// </summary>
public static class CfbLayoutMap {

  private const uint EndOfChain = 0xFFFFFFFE;
  private const uint FreeSect = 0xFFFFFFFF;

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (archive.Length < 512)
      yield break;

    archive.Position = 0;
    var header = new byte[512];
    if (archive.Read(header, 0, 512) < 512)
      yield break;

    // Validate magic
    ReadOnlySpan<byte> magic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    if (!header.AsSpan(0, 8).SequenceEqual(magic))
      yield break;

    var sectorExp = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1E));
    var sectorSize = 1 << sectorExp;

    // Header tile (always one sector for the file header)
    yield return new DefragBlockInfo(0, sectorSize, DefragBlockKind.MetadataReserved, FileName: "CFB Header");

    var fatSectorCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x2C));
    var firstDirSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x30));
    var firstMiniFatSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x3C));
    var numMiniFatSectors = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x40));
    var firstDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x44));
    var numDifatSectors = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x48));

    // Collect FAT sector IDs from header DIFAT + DIFAT chain
    var fatSectorIds = new List<uint>();
    var maxHeaderDifat = Math.Min(fatSectorCount, 109);
    for (var i = 0; i < maxHeaderDifat; i++) {
      var sid = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x4C + i * 4));
      if (sid != FreeSect && sid != EndOfChain)
        fatSectorIds.Add(sid);
    }

    // DIFAT chain sectors
    var difatSectors = new List<uint>();
    if (numDifatSectors > 0 && firstDifatSector != EndOfChain) {
      var current = firstDifatSector;
      for (var d = 0; d < numDifatSectors && current != EndOfChain && current != FreeSect; d++) {
        difatSectors.Add(current);
        var off = SectorOffset(current, sectorSize);
        if (off + sectorSize > archive.Length) break;
        var buf = new byte[sectorSize];
        archive.Position = off;
        if (archive.Read(buf, 0, sectorSize) < sectorSize) break;
        var entriesPerDifat = (sectorSize / 4) - 1;
        for (var i = 0; i < entriesPerDifat && fatSectorIds.Count < fatSectorCount; i++) {
          var sid = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4));
          if (sid != FreeSect && sid != EndOfChain)
            fatSectorIds.Add(sid);
        }
        current = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(entriesPerDifat * 4));
      }
    }

    // Emit DIFAT sector tiles
    foreach (var sid in difatSectors)
      yield return new DefragBlockInfo(SectorOffset(sid, sectorSize), sectorSize, DefragBlockKind.MetadataReserved, FileName: "DIFAT Sector");

    // Emit FAT sector tiles
    foreach (var sid in fatSectorIds)
      yield return new DefragBlockInfo(SectorOffset(sid, sectorSize), sectorSize, DefragBlockKind.MetadataReserved, FileName: "FAT Sector");

    // Build FAT
    var fatEntryCount = fatSectorIds.Count * (sectorSize / 4);
    var fat = new uint[fatEntryCount];
    for (var i = 0; i < fatSectorIds.Count; i++) {
      var off = SectorOffset(fatSectorIds[i], sectorSize);
      if (off + sectorSize > archive.Length) continue;
      var buf = new byte[sectorSize];
      archive.Position = off;
      if (archive.Read(buf, 0, sectorSize) < sectorSize) continue;
      var entriesThisSector = sectorSize / 4;
      for (var j = 0; j < entriesThisSector; j++) {
        var idx = i * entriesThisSector + j;
        if (idx < fat.Length)
          fat[idx] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(j * 4));
      }
    }

    // Emit directory sector chain tiles
    foreach (var sid in WalkChain(firstDirSector, fat))
      yield return new DefragBlockInfo(SectorOffset(sid, sectorSize), sectorSize, DefragBlockKind.MetadataReserved, FileName: "Directory Sector");

    // Emit mini-FAT sector chain tiles
    if (numMiniFatSectors > 0 && firstMiniFatSector != EndOfChain) {
      foreach (var sid in WalkChain(firstMiniFatSector, fat))
        yield return new DefragBlockInfo(SectorOffset(sid, sectorSize), sectorSize, DefragBlockKind.MetadataReserved, FileName: "Mini-FAT Sector");
    }

    // Read directory entries to find streams
    var dirData = ReadChainData(archive, firstDirSector, fat, sectorSize);
    var miniStreamCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x38));
    var entryCount = dirData.Length / 128;

    for (var i = 0; i < entryCount; i++) {
      var entryOff = i * 128;
      if (entryOff + 128 > dirData.Length) break;
      var entryType = dirData[entryOff + 0x42];
      if (entryType == 0) continue;

      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(entryOff + 0x40));
      var nameByteCount = Math.Max(0, nameLen - 2);
      var name = nameByteCount > 0
        ? System.Text.Encoding.Unicode.GetString(dirData, entryOff, nameByteCount)
        : $"entry_{i}";

      var startSector = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(entryOff + 0x74));
      var streamSize = BinaryPrimitives.ReadInt64LittleEndian(dirData.AsSpan(entryOff + 0x78));
      if (sectorSize == 512 && entryType == 2)
        streamSize &= 0xFFFFFFFF;

      if (entryType == 5) {
        // Root storage: its sector chain is the mini-stream container
        if (startSector != EndOfChain && startSector != FreeSect) {
          foreach (var sid in WalkChain(startSector, fat))
            yield return new DefragBlockInfo(SectorOffset(sid, sectorSize), sectorSize, DefragBlockKind.MetadataReserved, FileName: "Mini-Stream Container");
        }
      } else if (entryType == 2 && streamSize > 0) {
        // Stream entry: emit sector chain as Used tiles
        if (streamSize >= miniStreamCutoff && startSector != EndOfChain && startSector != FreeSect) {
          foreach (var sid in WalkChain(startSector, fat))
            yield return new DefragBlockInfo(SectorOffset(sid, sectorSize), sectorSize, DefragBlockKind.Used, FileName: name);
        }
        // Mini-stream entries are inside the root's container; we already tiled that as MetadataReserved.
      }
    }
  }

  private static long SectorOffset(uint sectorId, int sectorSize) => sectorSize + (long)sectorId * sectorSize;

  private static IEnumerable<uint> WalkChain(uint start, uint[] fat) {
    var current = start;
    var safety = 0;
    while (current != EndOfChain && current != FreeSect && safety++ < 1_000_000) {
      yield return current;
      current = current < fat.Length ? fat[current] : EndOfChain;
    }
  }

  private static byte[] ReadChainData(Stream archive, uint start, uint[] fat, int sectorSize) {
    using var ms = new MemoryStream();
    var buf = new byte[sectorSize];
    foreach (var sid in WalkChain(start, fat)) {
      var off = SectorOffset(sid, sectorSize);
      if (off + sectorSize > archive.Length) break;
      archive.Position = off;
      if (archive.Read(buf, 0, sectorSize) < sectorSize) break;
      ms.Write(buf, 0, sectorSize);
    }
    return ms.ToArray();
  }
}
