#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Vmdk;

public sealed class VmdkWriter {
  private byte[]? _diskData;

  public void SetDiskData(byte[] data) => _diskData = data;

  public byte[] Build() {
    var data = _diskData ?? [];
    var sectorSize = 512;
    var grainSize = 128; // 128 sectors = 64KB
    var capacitySectors = (data.Length + sectorSize - 1) / sectorSize;
    if (capacitySectors == 0) capacitySectors = 1;

    // Simplified: write sparse header + descriptor + raw data
    // Descriptor at sector 1, data starts after descriptor
    var descriptorText = BuildDescriptor(capacitySectors);
    var descriptorBytes = Encoding.ASCII.GetBytes(descriptorText);
    var descriptorSectors = (descriptorBytes.Length + sectorSize - 1) / sectorSize;

    var overHeadSectors = 1 + descriptorSectors; // header + descriptor
    // Align to grain boundary
    overHeadSectors = ((overHeadSectors + grainSize - 1) / grainSize) * grainSize;

    var totalSize = overHeadSectors * sectorSize + data.Length;
    var result = new byte[totalSize];

    // Sparse header (sector 0)
    SparseMagic.CopyTo(result, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 1); // version
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), 0); // flags
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), (ulong)capacitySectors);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(24), (ulong)grainSize);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(32), 1); // descriptorOffset (sector 1)
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(40), (ulong)descriptorSectors);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(64), (ulong)overHeadSectors);
    result[73] = (byte)'\n';
    result[74] = (byte)' ';
    result[75] = (byte)'\r';
    result[76] = (byte)'\n';

    // Descriptor
    descriptorBytes.CopyTo(result, sectorSize);

    // Raw data
    if (data.Length > 0)
      data.CopyTo(result, overHeadSectors * sectorSize);

    return result;
  }

  private static readonly byte[] SparseMagic = [0x4B, 0x44, 0x4D, 0x56];

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
