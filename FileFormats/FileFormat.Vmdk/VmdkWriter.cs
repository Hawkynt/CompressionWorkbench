#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Vmdk;

public sealed class VmdkWriter {
  private byte[]? _diskData;

  public void SetDiskData(byte[] data) => _diskData = data;

  /// <summary>
  /// Builds a monolithic sparse VMDK with a proper two-level grain directory/table structure.
  /// </summary>
  public byte[] Build() {
    var data = _diskData ?? [];
    const int sectorSize = 512;
    const int grainSizeSectors = 128; // 128 sectors = 64 KB grains
    const int numGTEsPerGT = 512;    // standard: 512 grain table entries per GT
    var grainSizeBytes = grainSizeSectors * sectorSize; // 65536

    var capacitySectors = (data.Length + sectorSize - 1) / sectorSize;
    if (capacitySectors == 0) capacitySectors = 1;

    var totalGrains = (capacitySectors + grainSizeSectors - 1) / grainSizeSectors;
    var numGdEntries = (totalGrains + numGTEsPerGT - 1) / numGTEsPerGT;

    // Build descriptor
    var descriptorText = BuildDescriptor(capacitySectors);
    var descriptorBytes = Encoding.ASCII.GetBytes(descriptorText);
    var descriptorSectors = (descriptorBytes.Length + sectorSize - 1) / sectorSize;

    // Layout (all sector-aligned):
    //   Sector 0          : sparse header
    //   Sector 1..        : embedded descriptor
    //   next aligned      : grain directory (numGdEntries * 4 bytes)
    //   next aligned      : grain tables (numGdEntries * numGTEsPerGT * 4 bytes each)
    //   next grain-aligned: data grains

    var gdOffsetSectors = 1 + descriptorSectors;
    var gdByteSize = numGdEntries * 4;
    var gdSectors = (gdByteSize + sectorSize - 1) / sectorSize;

    var gtStartSectors = gdOffsetSectors + gdSectors;
    var gtByteSizeEach = numGTEsPerGT * 4;
    var gtSectorsEach = (gtByteSizeEach + sectorSize - 1) / sectorSize;
    var gtTotalSectors = gtSectorsEach * numGdEntries;

    var dataStartSectors = gtStartSectors + gtTotalSectors;
    // Align to grain boundary
    dataStartSectors = ((dataStartSectors + grainSizeSectors - 1) / grainSizeSectors) * grainSizeSectors;

    // Determine which grains are non-zero
    var grainOffsets = new long[totalGrains]; // sector offset for each grain, or 0 for sparse
    var nextDataSector = (long)dataStartSectors;

    for (var g = 0; g < totalGrains; g++) {
      var srcOff = (long)g * grainSizeBytes;
      var srcLen = (int)Math.Min(grainSizeBytes, data.Length - srcOff);
      if (srcLen <= 0 || IsAllZero(data.AsSpan((int)srcOff, srcLen))) {
        grainOffsets[g] = 0; // sparse
      } else {
        grainOffsets[g] = nextDataSector;
        nextDataSector += grainSizeSectors;
      }
    }

    var overHeadSectors = dataStartSectors;
    var totalSize = (int)(nextDataSector * sectorSize);
    var result = new byte[totalSize];

    // Sparse header (sector 0)
    SparseMagic.CopyTo(result, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 1);                          // version
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), 0);                          // flags
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), (ulong)capacitySectors);    // capacity
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(24), (ulong)grainSizeSectors);   // grainSize
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(32), 1);                         // descriptorOffset
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(40), (ulong)descriptorSectors);  // descriptorSize
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(48), (uint)numGTEsPerGT);        // numGTEsPerGT
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(56), 0);                         // rgdOffset (0 = none)
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(64), (ulong)gdOffsetSectors);    // gdOffset
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(72), (ulong)overHeadSectors);    // overHead
    result[77] = (byte)'\n';
    result[78] = (byte)' ';
    result[79] = (byte)'\r';
    result[80] = (byte)'\n';

    // Descriptor
    descriptorBytes.CopyTo(result, sectorSize);

    // Grain directory entries: each points to a grain table (sector offset)
    for (var gd = 0; gd < numGdEntries; gd++) {
      var gtSectorOffset = (uint)(gtStartSectors + gd * gtSectorsEach);
      BinaryPrimitives.WriteUInt32LittleEndian(
        result.AsSpan((int)((long)gdOffsetSectors * sectorSize + gd * 4L)), gtSectorOffset);
    }

    // Grain table entries: each points to a data grain (sector offset), or 0
    for (var g = 0; g < totalGrains; g++) {
      var gdIndex = g / numGTEsPerGT;
      var gtIndex = g % numGTEsPerGT;
      var gtByteOffset = (long)(gtStartSectors + gdIndex * gtSectorsEach) * sectorSize + gtIndex * 4L;
      BinaryPrimitives.WriteUInt32LittleEndian(
        result.AsSpan((int)gtByteOffset), (uint)grainOffsets[g]);
    }

    // Data grains
    for (var g = 0; g < totalGrains; g++) {
      if (grainOffsets[g] == 0) continue;
      var destOff = (int)(grainOffsets[g] * sectorSize);
      var srcOff = (long)g * grainSizeBytes;
      var srcLen = (int)Math.Min(grainSizeBytes, data.Length - srcOff);
      if (srcLen > 0)
        data.AsSpan((int)srcOff, srcLen).CopyTo(result.AsSpan(destOff));
    }

    return result;
  }

  private static readonly byte[] SparseMagic = [0x4B, 0x44, 0x4D, 0x56];

  private static bool IsAllZero(ReadOnlySpan<byte> data) {
    foreach (var b in data)
      if (b != 0) return false;
    return true;
  }

  private static string BuildDescriptor(int capacitySectors) {
    var sb = new StringBuilder();
    sb.AppendLine("# Disk DescriptorFile");
    sb.AppendLine("version=1");
    sb.AppendLine("CID=fffffffe");
    sb.AppendLine("parentCID=ffffffff");
    sb.AppendLine("createType=\"monolithicSparse\"");
    sb.AppendLine();
    sb.AppendLine($"RW {capacitySectors} SPARSE \"disk.vmdk\"");
    sb.AppendLine();
    sb.AppendLine("# The Disk Data Base");
    sb.AppendLine("#DDB");
    sb.AppendLine("ddb.virtualHWVersion = \"4\"");
    sb.AppendLine($"ddb.geometry.sectors = \"63\"");
    sb.AppendLine($"ddb.geometry.heads = \"16\"");
    sb.AppendLine($"ddb.geometry.cylinders = \"{Math.Max(1, capacitySectors / (63 * 16))}\"");
    sb.AppendLine("ddb.adapterType = \"ide\"");
    return sb.ToString();
  }
}
