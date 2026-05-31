#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Vmdk;

public sealed class VmdkWriter {
  private byte[]? _diskData;

  public void SetDiskData(byte[] data) => _diskData = data;

  // SparseExtentHeader is a byte-packed structure (no natural alignment).
  // Field byte offsets within the 512-byte header sector:
  //   0  magic[4]            "KDMV"
  //   4  version (u32)
  //   8  flags (u32)
  //   12 capacity (u64, sectors)
  //   20 grainSize (u64, sectors)
  //   28 descriptorOffset (u64, sectors)
  //   36 descriptorSize (u64, sectors)
  //   44 numGTEsPerGT (u32)
  //   48 rgdOffset (u64, sectors) — redundant grain directory
  //   56 gdOffset (u64, sectors)  — primary grain directory
  //   64 overHead (u64, sectors)
  //   72 uncleanShutdown (u8)
  //   73 singleEndLineChar, nonEndLineChar, doubleEndLineChar1/2 (newline-detection test)
  private const int OffVersion = 4;
  private const int OffFlags = 8;
  private const int OffCapacity = 12;
  private const int OffGrainSize = 20;
  private const int OffDescriptorOffset = 28;
  private const int OffDescriptorSize = 36;
  private const int OffNumGTEsPerGT = 44;
  private const int OffRgdOffset = 48;
  private const int OffGdOffset = 56;
  private const int OffOverHead = 64;
  private const int OffUncleanShutdown = 72;

  // Flags: bit0 = newline-detection test valid, bit1 = redundant grain table used.
  private const uint FlagNewLineTest = 0x1;
  private const uint FlagRedundantGrainTable = 0x2;

  /// <summary>
  /// Builds a monolithic sparse VMDK with a proper two-level grain directory/table structure,
  /// including the redundant grain directory that VMware/qemu emit by default.
  /// </summary>
  public byte[] Build() {
    var data = _diskData ?? [];
    const int sectorSize = 512;
    const int grainSizeSectors = 128; // 128 sectors = 64 KB grains
    const int numGTEsPerGT = 512;    // standard: 512 grain table entries per GT
    var grainSizeBytes = grainSizeSectors * sectorSize; // 65536

    var capacitySectors = (data.Length + sectorSize - 1) / sectorSize;
    if (capacitySectors == 0) capacitySectors = 1;
    // Capacity must cover a whole number of grains so the grain tables map the
    // entire virtual disk; round up to the grain boundary.
    capacitySectors = ((capacitySectors + grainSizeSectors - 1) / grainSizeSectors) * grainSizeSectors;

    var totalGrains = (capacitySectors + grainSizeSectors - 1) / grainSizeSectors;
    var numGdEntries = (totalGrains + numGTEsPerGT - 1) / numGTEsPerGT;
    if (numGdEntries == 0) numGdEntries = 1;

    // Build descriptor
    var descriptorText = BuildDescriptor(capacitySectors);
    var descriptorBytes = Encoding.ASCII.GetBytes(descriptorText);
    var descriptorSectors = (descriptorBytes.Length + sectorSize - 1) / sectorSize;

    // Layout (all sector-aligned), matching the VMware monolithicSparse on-disk order:
    //   Sector 0                : sparse header
    //   Sector 1..              : embedded descriptor
    //   next aligned            : redundant grain directory (numGdEntries * 4 bytes)
    //   next aligned            : redundant grain tables (numGdEntries * numGTEsPerGT * 4)
    //   next aligned            : primary grain directory
    //   next aligned            : primary grain tables
    //   next grain-aligned      : data grains

    var gtByteSizeEach = numGTEsPerGT * 4;
    var gtSectorsEach = (gtByteSizeEach + sectorSize - 1) / sectorSize;
    var gtTotalSectors = gtSectorsEach * numGdEntries;

    var gdByteSize = numGdEntries * 4;
    var gdSectors = (gdByteSize + sectorSize - 1) / sectorSize;

    var rgdOffsetSectors = 1 + descriptorSectors;
    var rgtStartSectors = rgdOffsetSectors + gdSectors;
    var gdOffsetSectors = rgtStartSectors + gtTotalSectors;
    var gtStartSectors = gdOffsetSectors + gdSectors;

    var dataStartSectors = gtStartSectors + gtTotalSectors;
    // Align data start to a grain boundary.
    dataStartSectors = ((dataStartSectors + grainSizeSectors - 1) / grainSizeSectors) * grainSizeSectors;

    // Determine which grains are non-zero and assign their file sector offsets.
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

    // Sparse header (sector 0) — byte-packed layout.
    SparseMagic.CopyTo(result, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(OffVersion), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(OffFlags), FlagNewLineTest | FlagRedundantGrainTable);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(OffCapacity), (ulong)capacitySectors);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(OffGrainSize), (ulong)grainSizeSectors);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(OffDescriptorOffset), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(OffDescriptorSize), (ulong)descriptorSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(OffNumGTEsPerGT), (uint)numGTEsPerGT);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(OffRgdOffset), (ulong)rgdOffsetSectors);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(OffGdOffset), (ulong)gdOffsetSectors);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(OffOverHead), (ulong)overHeadSectors);
    result[OffUncleanShutdown] = 0;
    // Newline-detection test bytes: '\n', ' ', '\r', '\n'.
    result[73] = (byte)'\n';
    result[74] = (byte)' ';
    result[75] = (byte)'\r';
    result[76] = (byte)'\n';

    // Descriptor
    descriptorBytes.CopyTo(result, sectorSize);

    // Grain directory entries (primary + redundant): each points to a grain table.
    for (var gd = 0; gd < numGdEntries; gd++) {
      var rgtSectorOffset = (uint)(rgtStartSectors + gd * gtSectorsEach);
      var gtSectorOffset = (uint)(gtStartSectors + gd * gtSectorsEach);
      BinaryPrimitives.WriteUInt32LittleEndian(
        result.AsSpan((int)((long)rgdOffsetSectors * sectorSize + gd * 4L)), rgtSectorOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(
        result.AsSpan((int)((long)gdOffsetSectors * sectorSize + gd * 4L)), gtSectorOffset);
    }

    // Grain table entries (primary + redundant): each points to a data grain, or 0.
    for (var g = 0; g < totalGrains; g++) {
      var gdIndex = g / numGTEsPerGT;
      var gtIndex = g % numGTEsPerGT;
      var entry = (uint)grainOffsets[g];
      var rgtByteOffset = (long)(rgtStartSectors + gdIndex * gtSectorsEach) * sectorSize + gtIndex * 4L;
      var gtByteOffset = (long)(gtStartSectors + gdIndex * gtSectorsEach) * sectorSize + gtIndex * 4L;
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan((int)rgtByteOffset), entry);
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan((int)gtByteOffset), entry);
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
    sb.Append("# Disk DescriptorFile\n");
    sb.Append("version=1\n");
    sb.Append("CID=fffffffe\n");
    sb.Append("parentCID=ffffffff\n");
    sb.Append("createType=\"monolithicSparse\"\n");
    sb.Append('\n');
    sb.Append("# Extent description\n");
    sb.Append($"RW {capacitySectors} SPARSE \"disk.vmdk\"\n");
    sb.Append('\n');
    sb.Append("# The Disk Data Base\n");
    sb.Append("#DDB\n");
    sb.Append("ddb.virtualHWVersion = \"4\"\n");
    sb.Append("ddb.geometry.sectors = \"63\"\n");
    sb.Append("ddb.geometry.heads = \"16\"\n");
    sb.Append($"ddb.geometry.cylinders = \"{Math.Max(1, capacitySectors / (63 * 16))}\"\n");
    sb.Append("ddb.adapterType = \"ide\"\n");
    return sb.ToString();
  }
}
