#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Lzh;

/// <summary>
/// Walks an LHA/LZH archive and emits the byte-level layout: each entry's
/// variable-length header as MetadataReserved and compressed data as Used.
/// </summary>
public static class LzhLayoutMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;

    while (archive.Position < archive.Length) {
      var entryStart = archive.Position;

      // Peek at first two bytes
      var firstByte = archive.ReadByte();
      if (firstByte <= 0) yield break;
      var secondByte = archive.ReadByte();
      if (secondByte < 0) yield break;

      // Read method string (5 bytes at offset 2)
      var methodBytes = new byte[5];
      if (archive.Read(methodBytes, 0, 5) < 5) yield break;
      var method = Encoding.ASCII.GetString(methodBytes);

      // Validate method
      if (!method.StartsWith('-') || !method.EndsWith('-'))
        yield break;

      // Read common fields
      var reader = new BinaryReader(archive, Encoding.ASCII, leaveOpen: true);
      var compressedSize = reader.ReadUInt32();
      var originalSize = reader.ReadUInt32();
      var timestamp = reader.ReadUInt32();
      var reserved = reader.ReadByte();
      var level = reader.ReadByte();

      string fileName;
      long dataOffset;

      switch (level) {
        case 0: {
          var nameLength = reader.ReadByte();
          var nameBytes = reader.ReadBytes(nameLength);
          fileName = Encoding.ASCII.GetString(nameBytes);
          var crc16 = reader.ReadUInt16();
          // Level 0: headerSize (firstByte) includes everything from offset 2 to end of header
          // Total header = 2 + firstByte bytes
          dataOffset = entryStart + 2 + firstByte;
          break;
        }
        case 1: {
          var nameLength = reader.ReadByte();
          var nameBytes = reader.ReadBytes(nameLength);
          fileName = Encoding.ASCII.GetString(nameBytes);
          var crc16 = reader.ReadUInt16();
          var osId = reader.ReadByte();
          // Read extended headers
          while (true) {
            var extSize = reader.ReadUInt16();
            if (extSize == 0) break;
            var extType = reader.ReadByte();
            var extData = reader.ReadBytes(extSize - 3);
            if (extType == 0x01 && extData.Length > 0)
              fileName = Encoding.ASCII.GetString(extData);
          }
          dataOffset = archive.Position;
          break;
        }
        case 2: {
          var totalHeaderSize = firstByte | (secondByte << 8);
          var crc16 = reader.ReadUInt16();
          var osId = reader.ReadByte();
          fileName = ""; // will be set by extended headers

          var headerEnd = entryStart + totalHeaderSize;
          while (archive.Position < headerEnd) {
            var extSize = reader.ReadUInt16();
            if (extSize == 0) break;
            var extType = reader.ReadByte();
            var extData = reader.ReadBytes(extSize - 3);
            if (extType == 0x01 && extData.Length > 0)
              fileName = Encoding.ASCII.GetString(extData);
          }
          archive.Position = headerEnd;
          dataOffset = headerEnd;
          break;
        }
        default:
          yield break;
      }

      var headerSize = dataOffset - entryStart;

      // MetadataReserved tile for the header
      yield return new DefragBlockInfo(
        entryStart,
        headerSize,
        DefragBlockKind.MetadataReserved,
        FileName: $"Header: {fileName}");

      // Used tile for compressed data
      if (compressedSize > 0) {
        var classification = method switch {
          LhaConstants.MethodLh0 or LhaConstants.MethodLz4 or LhaConstants.MethodPm0
            => DefragBlockClass.Frozen,
          LhaConstants.MethodLh5 or LhaConstants.MethodLh6 or LhaConstants.MethodLh7
            => DefragBlockClass.Hot,
          _ => DefragBlockClass.Normal,
        };

        yield return new DefragBlockInfo(
          dataOffset,
          compressedSize,
          DefragBlockKind.Used,
          FileName: fileName,
          Classification: classification);
      }

      // Move to next entry
      archive.Position = dataOffset + compressedSize;
    }
  }
}
