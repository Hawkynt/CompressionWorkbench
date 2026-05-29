#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Gif;

/// <summary>
/// Walks a GIF87a/GIF89a file at the block level and emits <see cref="DefragBlockInfo"/>
/// tiles for block-chart visualization. The header, logical screen descriptor, and
/// global color table are MetadataReserved; each image frame is Used; extensions are
/// MetadataReserved; the trailer byte is MetadataReserved.
/// </summary>
public static class GifLayoutMap {

  private const byte BlockExtension = 0x21;
  private const byte BlockImageDescriptor = 0x2C;
  private const byte BlockTrailer = 0x3B;

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream file) {
    ArgumentNullException.ThrowIfNull(file);
    if (file.Length < 13)
      yield break;

    file.Position = 0;
    var data = new byte[file.Length];
    var totalRead = 0;
    while (totalRead < data.Length) {
      var n = file.Read(data, totalRead, data.Length - totalRead);
      if (n == 0) break;
      totalRead += n;
    }

    if (totalRead < 13) yield break;
    if (data[0] != 'G' || data[1] != 'I' || data[2] != 'F')
      yield break;

    // Header (6 bytes) + Logical Screen Descriptor (7 bytes) + optional GCT
    var packed = data[10];
    var globalCtSize = (packed & 0x80) != 0 ? 3 * (1 << ((packed & 0x07) + 1)) : 0;
    var headerEnd = 13 + globalCtSize;
    if (headerEnd > totalRead)
      yield break;

    yield return new DefragBlockInfo(0, headerEnd,
      DefragBlockKind.MetadataReserved, "GIF header + LSD + GCT", DefragBlockClass.Hot);

    var pos = headerEnd;
    var frameIndex = 0;

    while (pos < totalRead) {
      var marker = data[pos];

      if (marker == BlockTrailer) {
        yield return new DefragBlockInfo(pos, 1,
          DefragBlockKind.MetadataReserved, "Trailer", DefragBlockClass.Normal);
        break;
      }

      if (marker == BlockExtension) {
        var extStart = pos;
        if (pos + 2 > totalRead) break;
        pos += 2; // skip introducer + label
        SkipSubBlocks(data, ref pos, totalRead);
        yield return new DefragBlockInfo(extStart, pos - extStart,
          DefragBlockKind.MetadataReserved, "Extension", DefragBlockClass.Normal);
        continue;
      }

      if (marker == BlockImageDescriptor) {
        var idStart = pos;
        if (pos + 10 > totalRead) break;
        var idPacked = data[pos + 9];
        var hasLocalCt = (idPacked & 0x80) != 0;
        var localCtSize = hasLocalCt ? 3 * (1 << ((idPacked & 0x07) + 1)) : 0;
        pos += 10 + localCtSize;
        if (pos >= totalRead) break;
        ++pos; // LZW minimum code size byte
        SkipSubBlocks(data, ref pos, totalRead);

        yield return new DefragBlockInfo(idStart, pos - idStart,
          DefragBlockKind.Used, $"Frame {frameIndex}", DefragBlockClass.Normal);
        frameIndex++;
        continue;
      }

      // Unknown marker — skip
      pos++;
    }
  }

  private static void SkipSubBlocks(byte[] data, ref int pos, int length) {
    while (pos < length) {
      var len = data[pos++];
      if (len == 0) return;
      pos += len;
      if (pos > length) return;
    }
  }
}
