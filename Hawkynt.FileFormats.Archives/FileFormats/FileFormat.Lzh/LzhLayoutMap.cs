#pragma warning disable CS1591
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
      LhaHeader? header;
      try {
        header = LhaHeaderParser.Read(archive);
      } catch (InvalidDataException) {
        yield break;
      }
      if (header == null) yield break;

      var entryStart = header.HeaderOffset;
      var dataOffset = header.DataOffset;
      var compressedSize = header.CompressedSize;
      var method = header.Method;
      var fileName = header.Path;

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
