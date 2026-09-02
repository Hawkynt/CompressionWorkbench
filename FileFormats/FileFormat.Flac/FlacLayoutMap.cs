#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Flac;

/// <summary>
/// Walks a FLAC file and emits the byte-level layout: fLaC magic, STREAMINFO,
/// other metadata blocks (PADDING, VORBIS_COMMENT, PICTURE, SEEKTABLE, etc.),
/// and audio frames as <see cref="DefragBlockInfo"/> tiles.
/// </summary>
public static class FlacLayoutMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;

    if (archive.Length < 8)
      yield break;

    // Read fLaC magic (4 bytes)
    var magic = new byte[4];
    if (archive.Read(magic, 0, 4) < 4) yield break;
    if (magic[0] != 0x66 || magic[1] != 0x4C || magic[2] != 0x61 || magic[3] != 0x43)
      yield break;

    yield return new DefragBlockInfo(0, 4, DefragBlockKind.MetadataReserved,
      FileName: "fLaC Magic");

    // Walk metadata blocks
    var pos = 4L;
    var isLast = false;
    while (!isLast && pos + 4 <= archive.Length) {
      archive.Position = pos;
      var headerBuf = new byte[4];
      if (archive.Read(headerBuf, 0, 4) < 4) break;

      isLast = (headerBuf[0] & 0x80) != 0;
      var blockType = headerBuf[0] & 0x7F;
      var blockLength = (headerBuf[1] << 16) | (headerBuf[2] << 8) | headerBuf[3];

      var totalBlockSize = 4L + blockLength; // 4-byte header + body
      var blockName = blockType switch {
        0 => "STREAMINFO",
        1 => "PADDING",
        2 => "APPLICATION",
        3 => "SEEKTABLE",
        4 => "VORBIS_COMMENT",
        5 => "CUESHEET",
        6 => "PICTURE",
        _ => $"Metadata Block (type {blockType})",
      };

      // PADDING blocks are effectively free space
      if (blockType == 1) {
        yield return new DefragBlockInfo(pos, totalBlockSize, DefragBlockKind.Free,
          FileName: "PADDING");
      } else {
        yield return new DefragBlockInfo(pos, totalBlockSize, DefragBlockKind.MetadataReserved,
          FileName: blockName);
      }

      pos += totalBlockSize;
    }

    // Everything after metadata blocks = audio frames
    if (pos < archive.Length) {
      var framesStart = pos;

      // Try to identify individual frame boundaries by scanning for sync codes
      // FLAC frame sync: 0xFF 0xF8 or 0xFF 0xF9 (14 bits of 1s + reserved + blocking strategy)
      var data = new byte[archive.Length - pos];
      archive.Position = pos;
      var totalRead = 0;
      while (totalRead < data.Length) {
        var n = archive.Read(data, totalRead, data.Length - totalRead);
        if (n == 0) break;
        totalRead += n;
      }

      var frameStarts = new List<long>();
      for (var i = 0; i + 1 < totalRead; ++i) {
        if (data[i] == 0xFF && (data[i + 1] & 0xFE) == 0xF8)
          frameStarts.Add(framesStart + i);
      }

      if (frameStarts.Count > 1) {
        // Emit individual frames
        for (var i = 0; i < frameStarts.Count; ++i) {
          var start = frameStarts[i];
          var end = i + 1 < frameStarts.Count ? frameStarts[i + 1] : framesStart + totalRead;
          yield return new DefragBlockInfo(start, end - start,
            DefragBlockKind.Used, FileName: $"Audio Frame {i}",
            Classification: DefragBlockClass.Normal);
        }
      } else {
        // Couldn't parse frame boundaries; emit as single Used block
        yield return new DefragBlockInfo(framesStart, totalRead,
          DefragBlockKind.Used, FileName: "Audio Frames",
          Classification: DefragBlockClass.Normal);
      }
    }
  }
}
