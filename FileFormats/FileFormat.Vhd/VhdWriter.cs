using System.Buffers.Binary;

namespace FileFormat.Vhd;

/// <summary>Creates fixed or dynamic VHD images.</summary>
public sealed class VhdWriter {
  private byte[]? _diskData;

  /// <summary>Sets the raw disk data to embed in the VHD.</summary>
  public void SetDiskData(byte[] data) => _diskData = data;

  /// <summary>Builds a fixed VHD image: raw data followed by a 512-byte footer.</summary>
  public byte[] Build() {
    var data = _diskData ?? [];
    var result = new byte[data.Length + 512];
    data.CopyTo(result, 0);

    WriteFooter(result.AsSpan(data.Length), data.Length, diskType: 2, dataOffset: 0xFFFFFFFFFFFFFFFF);
    return result;
  }

  /// <summary>
  /// Builds a dynamic (sparse) VHD image with a BAT. Non-zero blocks are written;
  /// all-zero blocks are stored as sparse (BAT entry = 0xFFFFFFFF).
  /// </summary>
  /// <param name="blockSize">Block size in bytes. Must be a power of 2, typically 2 MB (0x00200000).</param>
  public byte[] BuildDynamic(int blockSize = 0x00200000) {
    var data = _diskData ?? [];
    var virtualSize = data.Length;
    if (virtualSize == 0) virtualSize = blockSize; // at least one block worth

    var totalBlocks = (virtualSize + blockSize - 1) / blockSize;
    var sectorsPerBlock = blockSize / 512;
    // Bitmap: one bit per sector, rounded up to whole sectors
    var bitmapSectors = (sectorsPerBlock + 512 * 8 - 1) / (512 * 8);
    var bitmapBytes = bitmapSectors * 512;

    // Layout:
    //   [0..511]     = footer copy
    //   [512..1535]  = dynamic disk header (1024 bytes)
    //   [1536..]     = BAT (totalBlocks * 4 bytes, padded to sector boundary)
    //   after BAT    = data blocks (non-sparse only, each = bitmap + blockSize)

    var footerCopyOff = 0;
    var dynHeaderOff = 512;
    var batOff = 1536;
    var batByteSize = totalBlocks * 4;
    var batPadded = ((batByteSize + 511) / 512) * 512; // pad to sector boundary
    var dataStartOff = batOff + batPadded;

    // Determine which blocks are non-zero (need storage)
    var blockOffsets = new long[totalBlocks]; // physical offset in result, or -1 for sparse
    var nextPhysical = (long)dataStartOff;

    for (var b = 0; b < totalBlocks; b++) {
      var srcOff = (long)b * blockSize;
      var srcLen = (int)Math.Min(blockSize, data.Length - srcOff);
      if (srcLen <= 0 || IsAllZero(data.AsSpan((int)srcOff, srcLen))) {
        blockOffsets[b] = -1; // sparse
      } else {
        blockOffsets[b] = nextPhysical;
        nextPhysical += bitmapBytes + blockSize;
      }
    }

    // Trailing footer
    var totalSize = nextPhysical + 512;
    var result = new byte[totalSize];

    // Footer copy at offset 0 (for dynamic VHDs)
    WriteFooter(result.AsSpan(footerCopyOff), virtualSize, diskType: 3, dataOffset: (ulong)dynHeaderOff);

    // Dynamic disk header at offset 512
    var hdr = result.AsSpan(dynHeaderOff);
    "cxsparse"u8.CopyTo(hdr);
    BinaryPrimitives.WriteUInt64BigEndian(hdr[8..], 0xFFFFFFFFFFFFFFFF); // data offset (unused)
    BinaryPrimitives.WriteUInt64BigEndian(hdr[16..], (ulong)batOff);     // table offset
    BinaryPrimitives.WriteUInt32BigEndian(hdr[24..], 0x00010000);        // header version
    BinaryPrimitives.WriteUInt32BigEndian(hdr[28..], (uint)totalBlocks); // max table entries
    BinaryPrimitives.WriteUInt32BigEndian(hdr[32..], (uint)blockSize);   // block size

    // Dynamic header checksum at offset 36
    uint dynSum = 0;
    for (var i = 0; i < 1024; i++) {
      if (i >= 36 && i < 40) continue;
      dynSum += result[dynHeaderOff + i];
    }
    BinaryPrimitives.WriteUInt32BigEndian(hdr[36..], ~dynSum);

    // BAT
    for (var b = 0; b < totalBlocks; b++) {
      if (blockOffsets[b] < 0) {
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(batOff + b * 4), 0xFFFFFFFF);
      } else {
        // BAT entry is the sector offset of the block (including its bitmap prefix)
        var sectorOffset = (uint)(blockOffsets[b] / 512);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(batOff + b * 4), sectorOffset);
      }
    }

    // Data blocks
    for (var b = 0; b < totalBlocks; b++) {
      if (blockOffsets[b] < 0) continue;

      var physOff = (int)blockOffsets[b];

      // Write sector bitmap (all 0xFF = all sectors present)
      for (var i = 0; i < bitmapBytes; i++)
        result[physOff + i] = 0xFF;

      // Write block data
      var srcOff = (long)b * blockSize;
      var srcLen = (int)Math.Min(blockSize, data.Length - srcOff);
      if (srcLen > 0)
        data.AsSpan((int)srcOff, srcLen).CopyTo(result.AsSpan(physOff + bitmapBytes));
    }

    // Trailing footer
    WriteFooter(result.AsSpan((int)nextPhysical), virtualSize, diskType: 3, dataOffset: (ulong)dynHeaderOff);

    return result;
  }

  private void WriteFooter(Span<byte> footer, long diskSize, uint diskType, ulong dataOffset) {
    "conectix"u8.CopyTo(footer);
    BinaryPrimitives.WriteUInt32BigEndian(footer[8..], 2);              // features: reserved
    BinaryPrimitives.WriteUInt32BigEndian(footer[12..], 0x00010000);    // file format version
    BinaryPrimitives.WriteUInt64BigEndian(footer[16..], dataOffset);    // data offset
    "cwb "u8.CopyTo(footer[28..]);                                      // creator application
    BinaryPrimitives.WriteUInt32BigEndian(footer[32..], 0x00010000);    // creator version
    "Wi2k"u8.CopyTo(footer[36..]);                                      // creator host OS
    BinaryPrimitives.WriteUInt64BigEndian(footer[40..], (ulong)diskSize); // original size
    BinaryPrimitives.WriteUInt64BigEndian(footer[48..], (ulong)diskSize); // current size

    ComputeChs(diskSize, out var c, out var h, out var s);
    BinaryPrimitives.WriteUInt16BigEndian(footer[56..], c);
    footer[58] = h;
    footer[59] = s;

    BinaryPrimitives.WriteUInt32BigEndian(footer[60..], diskType);

    // Checksum: one's complement of sum of all bytes except bytes 64-67
    uint sum = 0;
    for (var i = 0; i < 512; i++) {
      if (i >= 64 && i < 68) continue;
      sum += footer[i];
    }
    BinaryPrimitives.WriteUInt32BigEndian(footer[64..], ~sum);
  }

  private static bool IsAllZero(ReadOnlySpan<byte> data) {
    foreach (var b in data)
      if (b != 0) return false;
    return true;
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
