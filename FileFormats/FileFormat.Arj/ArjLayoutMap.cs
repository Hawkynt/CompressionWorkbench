#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Arj;

/// <summary>
/// Walks an ARJ archive and emits the byte-level layout: main archive header,
/// each entry header, compressed data, and the end-of-archive marker.
/// </summary>
public static class ArjLayoutMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;

    // Read the main archive header
    var mainResult = ReadOneHeaderLayout(archive);
    if (mainResult == null)
      yield break;

    var (mainHeaderStart, mainHeaderEnd, mainFileName, _) = mainResult.Value;
    yield return new DefragBlockInfo(
      mainHeaderStart,
      mainHeaderEnd - mainHeaderStart,
      DefragBlockKind.MetadataReserved,
      FileName: "ARJ Archive Header");

    // Read file entry headers until end-of-archive
    while (true) {
      var entryStart = archive.Position;
      var result = ReadOneHeaderLayout(archive);
      if (result == null) {
        // End-of-archive marker: header ID (2 bytes) + basic header size 0 (2 bytes) = 4 bytes
        if (entryStart + 4 <= archive.Length) {
          yield return new DefragBlockInfo(
            entryStart,
            4,
            DefragBlockKind.MetadataReserved,
            FileName: "End-of-archive marker");
        }
        yield break;
      }

      var (headerStart, headerEnd, fileName, compressedSize) = result.Value;

      // MetadataReserved tile for the entry header
      yield return new DefragBlockInfo(
        headerStart,
        headerEnd - headerStart,
        DefragBlockKind.MetadataReserved,
        FileName: $"Header: {fileName}");

      // Used tile for compressed data
      if (compressedSize > 0) {
        yield return new DefragBlockInfo(
          headerEnd,
          compressedSize,
          DefragBlockKind.Used,
          FileName: fileName,
          Classification: DefragBlockClass.Normal);
      }

      // Advance past compressed data
      archive.Position = headerEnd + compressedSize;
    }
  }

  /// <summary>
  /// Reads one ARJ header from the current stream position and returns its
  /// layout information without fully parsing the entry contents.
  /// Returns null on end-of-archive marker or end of stream.
  /// </summary>
  private static (long HeaderStart, long HeaderEnd, string FileName, long CompressedSize)? ReadOneHeaderLayout(Stream stream) {
    var headerStart = stream.Position;

    if (stream.Position >= stream.Length - 1)
      return null;

    // Header ID (2 bytes, LE)
    var lo = stream.ReadByte();
    var hi = stream.ReadByte();
    if (lo < 0 || hi < 0) return null;

    var headerId = (ushort)(lo | (hi << 8));
    if (headerId != ArjConstants.HeaderId) return null;

    // Basic header size (2 bytes, LE)
    var szLo = stream.ReadByte();
    var szHi = stream.ReadByte();
    if (szLo < 0 || szHi < 0) return null;

    var basicHeaderSize = (ushort)(szLo | (szHi << 8));

    // Size of 0 = end-of-archive marker
    if (basicHeaderSize == 0) return null;

    if (basicHeaderSize > 2600) return null;

    // Read header body
    var headerBytes = new byte[basicHeaderSize];
    var total = 0;
    while (total < basicHeaderSize) {
      var read = stream.Read(headerBytes, total, basicHeaderSize - total);
      if (read == 0) return null;
      total += read;
    }

    // Skip CRC-32 (4 bytes)
    stream.Position += 4;

    // Skip extended headers
    while (true) {
      if (stream.Position + 2 > stream.Length) break;
      var b1 = stream.ReadByte();
      var b2 = stream.ReadByte();
      if (b1 < 0 || b2 < 0) break;
      var extSize = (ushort)(b1 | (b2 << 8));
      if (extSize == 0) break;
      stream.Position += extSize + 4; // ext data + CRC
    }

    var headerEnd = stream.Position;

    // Parse file name and compressed size from the header body
    var fileName = "";
    long compressedSize = 0;
    if (basicHeaderSize >= ArjConstants.FirstHeaderMinSize) {
      var firstHeaderSize = headerBytes[0];
      compressedSize = headerBytes[12]
        | ((long)headerBytes[13] << 8)
        | ((long)headerBytes[14] << 16)
        | ((long)headerBytes[15] << 24);

      if (firstHeaderSize < basicHeaderSize) {
        var pos = firstHeaderSize;
        var end = pos;
        while (end < basicHeaderSize && headerBytes[end] != 0) end++;
        if (end > pos)
          fileName = Encoding.ASCII.GetString(headerBytes, pos, end - pos);
      }
    }

    return (headerStart, headerEnd, fileName, compressedSize);
  }
}
