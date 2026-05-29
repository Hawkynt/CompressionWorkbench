#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Walks a PNG file's chunk chain (8-byte signature + sequence of chunks) and
/// emits <see cref="DefragBlockInfo"/> tiles for each structural element.
/// Does not decode pixel data — purely structural.
/// </summary>
public static class PngLayoutMap {

  /// <summary>The canonical 8-byte PNG signature.</summary>
  private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Position = 0;

    if (stream.Length < 8)
      yield break;

    // Read signature
    var sigBuf = new byte[8];
    if (ReadExactly(stream, sigBuf, 0, 8) < 8)
      yield break;

    // Verify PNG signature
    for (var i = 0; i < 8; i++)
      if (sigBuf[i] != PngSignature[i])
        yield break;

    yield return new DefragBlockInfo(0, 8, DefragBlockKind.MetadataReserved, FileName: "PNG signature");

    var header = new byte[8]; // 4 bytes length + 4 bytes type
    var pos = 8L;

    while (pos + 12 <= stream.Length) { // minimum chunk = 4(len) + 4(type) + 0(data) + 4(crc) = 12
      stream.Position = pos;
      if (ReadExactly(stream, header, 0, 8) < 8)
        break;

      var dataLength = (long)BinaryPrimitives.ReadUInt32BigEndian(header);
      var type = Encoding.ASCII.GetString(header, 4, 4);

      // Total chunk size: 4(length) + 4(type) + dataLength + 4(CRC)
      var chunkTotalSize = 4 + 4 + dataLength + 4;

      if (pos + chunkTotalSize > stream.Length)
        break;

      yield return Classify(type, pos, chunkTotalSize);

      pos += chunkTotalSize;

      if (type == "IEND")
        break;
    }
  }

  /// <summary>
  /// Classifies a PNG chunk by its four-character type into the appropriate
  /// <see cref="DefragBlockKind"/> and <see cref="DefragBlockClass"/>.
  /// </summary>
  private static DefragBlockInfo Classify(string type, long offset, long size) => type switch {
    "IHDR" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "IHDR (Image header)"),
    "PLTE" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "PLTE (Palette)"),
    "IDAT" => new DefragBlockInfo(offset, size, DefragBlockKind.Used, FileName: "IDAT (Image data)", Classification: DefragBlockClass.Normal),
    "IEND" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "IEND (End marker)"),
    "tEXt" => new DefragBlockInfo(offset, size, DefragBlockKind.Used, FileName: "tEXt (Text metadata)", Classification: DefragBlockClass.Cold),
    "iTXt" => new DefragBlockInfo(offset, size, DefragBlockKind.Used, FileName: "iTXt (Text metadata)", Classification: DefragBlockClass.Cold),
    "zTXt" => new DefragBlockInfo(offset, size, DefragBlockKind.Used, FileName: "zTXt (Text metadata)", Classification: DefragBlockClass.Cold),
    "eXIf" => new DefragBlockInfo(offset, size, DefragBlockKind.Used, FileName: "eXIf (EXIF)", Classification: DefragBlockClass.Hot),
    "iCCP" => new DefragBlockInfo(offset, size, DefragBlockKind.Used, FileName: "iCCP (ICC Profile)", Classification: DefragBlockClass.Cold),
    "pHYs" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "pHYs (Display hints)"),
    "tIME" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "tIME (Display hints)"),
    "gAMA" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "gAMA (Display hints)"),
    "cHRM" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "cHRM (Display hints)"),
    "sRGB" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "sRGB (Display hints)"),
    "sBIT" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "sBIT (Display hints)"),
    "bKGD" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "bKGD (Display hints)"),
    "tRNS" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "tRNS (Transparency)"),
    "hIST" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "hIST (Histogram)"),
    "sPLT" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "sPLT (Suggested palette)"),
    _ => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: type),
  };

  private static int ReadExactly(Stream stream, byte[] buffer, int offset, int count) {
    var totalRead = 0;
    while (totalRead < count) {
      var n = stream.Read(buffer, offset + totalRead, count - totalRead);
      if (n == 0) break;
      totalRead += n;
    }
    return totalRead;
  }
}
