#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Avi;

/// <summary>
/// Walks an AVI (RIFF) file's top-level chunk structure and emits
/// <see cref="DefragBlockInfo"/> tiles. The RIFF header is MetadataReserved,
/// hdrl is MetadataReserved, each chunk in movi is Used (named by stream type),
/// and idx1 is MetadataReserved.
/// </summary>
public static class AviLayoutMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream file) {
    ArgumentNullException.ThrowIfNull(file);
    if (file.Length < 12)
      yield break;

    file.Position = 0;
    var riffHeader = new byte[12];
    if (file.Read(riffHeader, 0, 12) < 12)
      yield break;

    if (riffHeader[0] != 'R' || riffHeader[1] != 'I' || riffHeader[2] != 'F' || riffHeader[3] != 'F')
      yield break;
    if (riffHeader[8] != 'A' || riffHeader[9] != 'V' || riffHeader[10] != 'I' || riffHeader[11] != ' ')
      yield break;

    // RIFF header itself (12 bytes: "RIFF" + size + "AVI ")
    yield return new DefragBlockInfo(0, 12,
      DefragBlockKind.MetadataReserved, "RIFF header", DefragBlockClass.Hot);

    // Walk top-level chunks
    var pos = 12L;
    var chunkHeader = new byte[12]; // enough for LIST type too

    while (pos + 8 <= file.Length) {
      file.Position = pos;
      if (file.Read(chunkHeader, 0, 8) < 8)
        break;

      var id = Encoding.ASCII.GetString(chunkHeader, 0, 4);
      var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4));
      var bodyStart = pos + 8;

      if (bodyStart + size > file.Length)
        size = file.Length - bodyStart; // truncated file — use remaining

      if (id == "LIST" && size >= 4) {
        file.Position = bodyStart;
        if (file.Read(chunkHeader, 8, 4) < 4) break;
        var listType = Encoding.ASCII.GetString(chunkHeader, 8, 4);

        if (listType == "hdrl") {
          // Header list = MetadataReserved
          yield return new DefragBlockInfo(pos, 8 + size,
            DefragBlockKind.MetadataReserved, "hdrl (header list)", DefragBlockClass.Hot);
        } else if (listType == "movi") {
          // Walk movi children: each sub-chunk is a data chunk
          foreach (var block in EnumerateMoviChildren(file, bodyStart + 4, size - 4))
            yield return block;

          // Also emit the LIST/movi wrapper overhead (the 12-byte header)
          // We've already emitted children, so just note the wrapper
        } else {
          // Other LIST (INFO, etc.)
          yield return new DefragBlockInfo(pos, 8 + size,
            DefragBlockKind.MetadataReserved, $"LIST/{listType}", DefragBlockClass.Cold);
        }
      } else if (id == "idx1") {
        yield return new DefragBlockInfo(pos, 8 + size,
          DefragBlockKind.MetadataReserved, "idx1 (index)", DefragBlockClass.Normal);
      } else if (id == "JUNK" || id == "RIFF") {
        yield return new DefragBlockInfo(pos, 8 + size,
          DefragBlockKind.Free, $"{id} (padding)", DefragBlockClass.Normal);
      } else {
        yield return new DefragBlockInfo(pos, 8 + size,
          DefragBlockKind.Used, id, DefragBlockClass.Normal);
      }

      pos = bodyStart + size + (size & 1); // word-align
    }
  }

  private static IEnumerable<DefragBlockInfo> EnumerateMoviChildren(Stream file, long start, long length) {
    var pos = start;
    var end = start + length;
    var header = new byte[8];

    while (pos + 8 <= end) {
      file.Position = pos;
      if (file.Read(header, 0, 8) < 8)
        break;

      var id = Encoding.ASCII.GetString(header, 0, 4);
      var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
      var bodyStart = pos + 8;

      if (bodyStart + size > end)
        size = end - bodyStart;

      if (id == "LIST" && size >= 4) {
        // rec list — recurse
        foreach (var block in EnumerateMoviChildren(file, bodyStart + 4, size - 4))
          yield return block;
      } else {
        // Data chunk — classify by stream type suffix
        var streamType = id.Length >= 4 ? id[2..] : id;
        var name = streamType switch {
          "dc" or "DC" => $"{id} (video)",
          "wb" or "WB" => $"{id} (audio)",
          "tx" or "TX" => $"{id} (subtitle)",
          _ => $"{id} (data)",
        };
        yield return new DefragBlockInfo(pos, 8 + size,
          DefragBlockKind.Used, name, DefragBlockClass.Normal);
      }

      pos = bodyStart + size + (size & 1);
    }
  }
}
