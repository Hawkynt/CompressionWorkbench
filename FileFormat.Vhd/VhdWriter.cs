using System.Buffers.Binary;

namespace FileFormat.Vhd;

/// <summary>Creates a fixed VHD: raw data + 512-byte footer.</summary>
public sealed class VhdWriter {
  private byte[]? _diskData;

  /// <summary>Sets the raw disk data to embed in the VHD.</summary>
  public void SetDiskData(byte[] data) => _diskData = data;

  /// <summary>Builds the VHD image.</summary>
  public byte[] Build() {
    var data = _diskData ?? [];
    var result = new byte[data.Length + 512];
    data.CopyTo(result, 0);

    // Write footer at end
    var footer = result.AsSpan(data.Length);
    "conectix"u8.CopyTo(footer);
    BinaryPrimitives.WriteUInt32BigEndian(footer[8..], 2); // features: reserved
    BinaryPrimitives.WriteUInt32BigEndian(footer[12..], 0x00010000); // file format version
    BinaryPrimitives.WriteUInt64BigEndian(footer[16..], 0xFFFFFFFFFFFFFFFF); // data offset (fixed = none)
    // Timestamp: 0 for simplicity
    "cwb "u8.CopyTo(footer[28..]); // creator application
    BinaryPrimitives.WriteUInt32BigEndian(footer[32..], 0x00010000); // creator version
    "Wi2k"u8.CopyTo(footer[36..]); // creator host OS
    BinaryPrimitives.WriteUInt64BigEndian(footer[40..], (ulong)data.Length); // original size
    BinaryPrimitives.WriteUInt64BigEndian(footer[48..], (ulong)data.Length); // current size

    // CHS geometry at offset 56
    ComputeChs(data.Length, out var c, out var h, out var s);
    BinaryPrimitives.WriteUInt16BigEndian(footer[56..], c);
    footer[58] = h;
    footer[59] = s;

    // Disk type = 2 (fixed)
    BinaryPrimitives.WriteUInt32BigEndian(footer[60..], 2);

    // Compute checksum at offset 64: one's complement of sum of all bytes except bytes 64-67
    uint sum = 0;
    for (int i = 0; i < 512; i++) {
      if (i >= 64 && i < 68) continue;
      sum += result[data.Length + i];
    }
    BinaryPrimitives.WriteUInt32BigEndian(footer[64..], ~sum);

    return result;
  }

  private static void ComputeChs(long diskSize, out ushort cylinders, out byte heads, out byte sectors) {
    var totalSectors = diskSize / 512;
    if (totalSectors > 65535 * 16 * 255)
      totalSectors = 65535 * 16 * 255;
    if (totalSectors >= 65535 * 16 * 63) {
      sectors = 255;
      heads = 16;
      cylinders = (ushort)(totalSectors / (16 * 255));
    } else {
      sectors = 17;
      var cylsTimesHeads = totalSectors / 17;
      heads = (byte)Math.Max(4, (cylsTimesHeads + 1023) / 1024);
      cylinders = (ushort)(cylsTimesHeads / heads);
    }
    if (cylinders > 65535) cylinders = 65535;
    if (cylinders == 0) cylinders = 1;
  }
}
