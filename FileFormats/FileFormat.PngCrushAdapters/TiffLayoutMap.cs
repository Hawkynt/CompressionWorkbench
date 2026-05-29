#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Walks a TIFF file's IFD chain and strip/tile data pointers at the byte level
/// and emits <see cref="DefragBlockInfo"/> tiles. The 8-byte header and each IFD
/// are MetadataReserved; strip/tile data regions are Used per strip.
/// Does not decode pixel data — just walks structural headers and offset tables.
/// </summary>
public static class TiffLayoutMap {

  // TIFF tag IDs
  private const ushort TagStripOffsets = 0x0111;
  private const ushort TagStripByteCounts = 0x0117;
  private const ushort TagTileOffsets = 0x0144;
  private const ushort TagTileByteCounts = 0x0145;

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream file) {
    ArgumentNullException.ThrowIfNull(file);
    if (file.Length < 8)
      yield break;

    file.Position = 0;
    var data = new byte[file.Length];
    var totalRead = 0;
    while (totalRead < data.Length) {
      var n = file.Read(data, totalRead, data.Length - totalRead);
      if (n == 0) break;
      totalRead += n;
    }

    if (totalRead < 8) yield break;

    // Determine byte order
    bool littleEndian;
    if (data[0] == 'I' && data[1] == 'I')
      littleEndian = true;
    else if (data[0] == 'M' && data[1] == 'M')
      littleEndian = false;
    else
      yield break;

    var magic = ReadU16(data, 2, littleEndian);
    if (magic != 0x002A) yield break;

    // 8-byte TIFF header
    yield return new DefragBlockInfo(0, 8,
      DefragBlockKind.MetadataReserved, "TIFF header", DefragBlockClass.Hot);

    var ifdOffset = (int)ReadU32(data, 4, littleEndian);
    var ifdIndex = 0;
    var emittedRanges = new List<(long Start, long End)>();

    // Track header as emitted
    emittedRanges.Add((0, 8));

    while (ifdOffset > 0 && ifdOffset + 2 <= totalRead) {
      var entryCount = ReadU16(data, ifdOffset, littleEndian);
      var ifdSize = 2 + entryCount * 12 + 4; // count + entries + next-IFD pointer
      if (ifdOffset + ifdSize > totalRead) break;

      yield return new DefragBlockInfo(ifdOffset, ifdSize,
        DefragBlockKind.MetadataReserved, $"IFD {ifdIndex}", DefragBlockClass.Hot);
      emittedRanges.Add((ifdOffset, ifdOffset + ifdSize));

      // Parse entries to find strip/tile offsets and byte counts
      var stripOffsets = new List<uint>();
      var stripCounts = new List<uint>();
      var tileOffsets = new List<uint>();
      var tileCounts = new List<uint>();

      for (var i = 0; i < entryCount; i++) {
        var entryPos = ifdOffset + 2 + i * 12;
        var tag = ReadU16(data, entryPos, littleEndian);
        var type = ReadU16(data, entryPos + 2, littleEndian);
        var count = (int)ReadU32(data, entryPos + 4, littleEndian);

        switch (tag) {
          case TagStripOffsets:
            stripOffsets = ReadOffsets(data, entryPos + 8, type, count, littleEndian, totalRead);
            break;
          case TagStripByteCounts:
            stripCounts = ReadOffsets(data, entryPos + 8, type, count, littleEndian, totalRead);
            break;
          case TagTileOffsets:
            tileOffsets = ReadOffsets(data, entryPos + 8, type, count, littleEndian, totalRead);
            break;
          case TagTileByteCounts:
            tileCounts = ReadOffsets(data, entryPos + 8, type, count, littleEndian, totalRead);
            break;
        }
      }

      // Emit strip data blocks
      for (var s = 0; s < stripOffsets.Count && s < stripCounts.Count; s++) {
        var off = (long)stripOffsets[s];
        var len = (long)stripCounts[s];
        if (off + len > totalRead) len = totalRead - off;
        if (len <= 0) continue;
        yield return new DefragBlockInfo(off, len,
          DefragBlockKind.Used, $"IFD{ifdIndex} strip {s}", DefragBlockClass.Normal);
        emittedRanges.Add((off, off + len));
      }

      // Emit tile data blocks
      for (var t = 0; t < tileOffsets.Count && t < tileCounts.Count; t++) {
        var off = (long)tileOffsets[t];
        var len = (long)tileCounts[t];
        if (off + len > totalRead) len = totalRead - off;
        if (len <= 0) continue;
        yield return new DefragBlockInfo(off, len,
          DefragBlockKind.Used, $"IFD{ifdIndex} tile {t}", DefragBlockClass.Normal);
        emittedRanges.Add((off, off + len));
      }

      // Next IFD offset
      var nextIfdPos = ifdOffset + 2 + entryCount * 12;
      ifdOffset = (int)ReadU32(data, nextIfdPos, littleEndian);
      ifdIndex++;
    }
  }

  private static List<uint> ReadOffsets(byte[] data, int valueFieldOffset, ushort type,
                                         int count, bool littleEndian, int dataLen) {
    var result = new List<uint>(count);
    var valueSize = TypeSize(type) * count;

    int dataOffset;
    if (valueSize <= 4) {
      // Values inline in the 4-byte value field
      dataOffset = valueFieldOffset;
    } else {
      // Values at an offset pointed to by the value field
      dataOffset = (int)ReadU32(data, valueFieldOffset, littleEndian);
    }

    for (var i = 0; i < count; i++) {
      var pos = dataOffset + i * TypeSize(type);
      if (pos + TypeSize(type) > dataLen) break;

      uint val = type switch {
        3 => ReadU16(data, pos, littleEndian), // SHORT
        4 => ReadU32(data, pos, littleEndian), // LONG
        _ => ReadU32(data, pos, littleEndian),
      };
      result.Add(val);
    }
    return result;
  }

  private static int TypeSize(ushort type) => type switch {
    1 => 1,  // BYTE
    2 => 1,  // ASCII
    3 => 2,  // SHORT
    4 => 4,  // LONG
    5 => 8,  // RATIONAL
    6 => 1,  // SBYTE
    7 => 1,  // UNDEFINED
    8 => 2,  // SSHORT
    9 => 4,  // SLONG
    10 => 8, // SRATIONAL
    11 => 4, // FLOAT
    12 => 8, // DOUBLE
    _ => 4,
  };

  private static ushort ReadU16(byte[] data, int offset, bool littleEndian) =>
    littleEndian
      ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset))
      : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));

  private static uint ReadU32(byte[] data, int offset, bool littleEndian) =>
    littleEndian
      ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset))
      : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
}
