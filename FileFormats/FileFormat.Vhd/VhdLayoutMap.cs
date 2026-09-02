#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.Vhd;

/// <summary>
/// Walks a VHD image and emits the byte-level layout of the container's own
/// structure: footer (copy), dynamic header, BAT, sector bitmaps, data blocks,
/// and trailing footer. For fixed VHDs: raw data + trailing footer.
/// </summary>
public static class VhdLayoutMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Position = 0;

    if (stream.Length < 512)
      yield break;

    // Read the minimum to parse the footer
    var buf = new byte[stream.Length];
    stream.Position = 0;
    stream.ReadExactly(buf);

    // Locate footer: try end of file first, then offset 0
    var footerOff = buf.Length - 512;
    var magic = "conectix"u8;
    if (!buf.AsSpan(footerOff, 8).SequenceEqual(magic)) {
      if (buf.AsSpan(0, 8).SequenceEqual(magic))
        footerOff = 0;
      else
        yield break;
    }

    var diskType = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(footerOff + 60));
    var dataOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(footerOff + 16));

    if (diskType == 2) {
      // Fixed VHD: raw disk data from 0 to Length-512, then footer
      var dataLen = buf.Length - 512;
      if (dataLen > 0) {
        yield return new DefragBlockInfo(0, dataLen, DefragBlockKind.Used,
          FileName: "disk.img", Classification: DefragBlockClass.Normal);
      }
      yield return new DefragBlockInfo(buf.Length - 512, 512, DefragBlockKind.MetadataReserved,
        FileName: "VHD Footer");
    } else if (diskType is 3 or 4) {
      // Dynamic/Differencing VHD
      // Footer copy at offset 0
      yield return new DefragBlockInfo(0, 512, DefragBlockKind.MetadataReserved,
        FileName: "VHD Footer (copy)");

      // Dynamic disk header at dataOffset
      if (dataOffset >= 0 && dataOffset + 1024 <= buf.Length) {
        yield return new DefragBlockInfo(dataOffset, 1024, DefragBlockKind.MetadataReserved,
          FileName: "Dynamic Disk Header");

        var hdr = buf.AsSpan((int)dataOffset);
        var batOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr[16..]);
        var maxBatEntries = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr[28..]);
        var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr[32..]);

        if (blockSize > 0 && (blockSize & (blockSize - 1)) == 0) {
          var sectorsPerBlock = blockSize / 512;
          var bitmapSectors = (sectorsPerBlock + 512 * 8 - 1) / (512 * 8);
          var bitmapBytes = bitmapSectors * 512;

          // BAT
          var batByteLen = (long)maxBatEntries * 4;
          if (batOffset >= 0 && batOffset + batByteLen <= buf.Length) {
            yield return new DefragBlockInfo(batOffset, batByteLen, DefragBlockKind.MetadataReserved,
              FileName: $"BAT ({maxBatEntries} entries)");

            // Data blocks
            for (var i = 0; i < maxBatEntries; i++) {
              var batEntry = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan((int)(batOffset + i * 4L)));
              if (batEntry == 0xFFFFFFFF)
                continue; // sparse

              var physicalOffset = (long)batEntry * 512;

              // Sector bitmap
              yield return new DefragBlockInfo(physicalOffset, bitmapBytes, DefragBlockKind.MetadataReserved,
                FileName: $"Block {i} bitmap");

              // Data block
              yield return new DefragBlockInfo(physicalOffset + bitmapBytes, blockSize, DefragBlockKind.Used,
                FileName: $"Block {i} data", Classification: DefragBlockClass.Normal);
            }
          }
        }
      }

      // Trailing footer
      yield return new DefragBlockInfo(buf.Length - 512, 512, DefragBlockKind.MetadataReserved,
        FileName: "VHD Footer (trailing)");
    }
  }
}
