#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.Vmdk;

/// <summary>
/// Walks a sparse VMDK image and emits the byte-level layout: sparse header,
/// embedded descriptor, grain directory, grain tables, and data grains.
/// </summary>
public static class VmdkLayoutMap {

  private static readonly byte[] SparseMagic = [0x4B, 0x44, 0x4D, 0x56];

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Position = 0;

    if (stream.Length < 512)
      yield break;

    var buf = new byte[stream.Length];
    stream.Position = 0;
    stream.ReadExactly(buf);

    // Must be sparse VMDK
    if (!buf.AsSpan(0, 4).SequenceEqual(SparseMagic))
      yield break;

    const int sectorSize = 512;

    // Sparse header (sector 0)
    yield return new DefragBlockInfo(0, sectorSize, DefragBlockKind.MetadataReserved,
      FileName: "Sparse Header");

    // SparseExtentHeader is byte-packed; fields sit at 12/20/28/36/44/56.
    var capacity = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(12));
    var grainSizeSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(20));
    var descriptorOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(28));
    var descriptorSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(36));
    var numGTEsPerGT = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(44));
    var gdOffsetSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(56));

    if (numGTEsPerGT <= 0) numGTEsPerGT = 512;
    var grainSizeBytes = grainSizeSectors * sectorSize;

    // Embedded descriptor
    if (descriptorOffset > 0 && descriptorSize > 0) {
      yield return new DefragBlockInfo(
        descriptorOffset * sectorSize,
        descriptorSize * sectorSize,
        DefragBlockKind.MetadataReserved,
        FileName: "Embedded Descriptor");
    }

    // Grain directory
    var sectorsPerGt = (long)numGTEsPerGT * grainSizeSectors;
    var numGdEntries = sectorsPerGt > 0 ? (int)((capacity + sectorsPerGt - 1) / sectorsPerGt) : 0;

    if (gdOffsetSectors > 0 && numGdEntries > 0) {
      var gdByteOffset = gdOffsetSectors * sectorSize;
      var gdByteSize = (long)numGdEntries * 4;
      var gdSectorAligned = ((gdByteSize + sectorSize - 1) / sectorSize) * sectorSize;

      yield return new DefragBlockInfo(gdByteOffset, gdSectorAligned, DefragBlockKind.MetadataReserved,
        FileName: $"Grain Directory ({numGdEntries} entries)");

      // Grain tables
      for (var gd = 0; gd < numGdEntries; gd++) {
        var gtOff = gdByteOffset + gd * 4L;
        if (gtOff + 4 > buf.Length) break;

        var gtSectorOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan((int)gtOff));
        if (gtSectorOffset == 0) continue;

        var gtByteOffset = (long)gtSectorOffset * sectorSize;
        var gtByteSize = (long)numGTEsPerGT * 4;
        var gtSectorAligned = ((gtByteSize + sectorSize - 1) / sectorSize) * sectorSize;

        yield return new DefragBlockInfo(gtByteOffset, gtSectorAligned, DefragBlockKind.MetadataReserved,
          FileName: $"Grain Table {gd}");

        // Data grains within this GT
        var grainBase = (long)gd * numGTEsPerGT;
        var totalGrains = grainSizeBytes > 0 ? (capacity * sectorSize + grainSizeBytes - 1) / grainSizeBytes : 0;

        for (var gte = 0; gte < numGTEsPerGT; gte++) {
          var grainIdx = grainBase + gte;
          if (grainIdx >= totalGrains) break;

          var gteByteOff = gtByteOffset + gte * 4L;
          if (gteByteOff + 4 > buf.Length) break;

          var grainSectorOff = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan((int)gteByteOff));
          if (grainSectorOff == 0) continue;

          yield return new DefragBlockInfo(
            (long)grainSectorOff * sectorSize,
            grainSizeBytes,
            DefragBlockKind.Used,
            FileName: $"Grain {grainIdx}",
            Classification: DefragBlockClass.Normal);
        }
      }
    }
  }
}
