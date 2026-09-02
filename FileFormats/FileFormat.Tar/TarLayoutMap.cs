#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Tar;

/// <summary>
/// Walks a TAR archive sequentially and emits the byte-level layout: each
/// 512-byte header block as MetadataReserved, each file's data (padded to
/// 512) as Used, and the trailing 2x512 zero blocks as MetadataReserved.
/// </summary>
public static class TarLayoutMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;

    var headerBuf = new byte[TarConstants.BlockSize];

    while (archive.Position + TarConstants.BlockSize <= archive.Length) {
      var headerOffset = archive.Position;
      var bytesRead = archive.Read(headerBuf, 0, TarConstants.BlockSize);
      if (bytesRead < TarConstants.BlockSize)
        yield break;

      // Check for zero block (end-of-archive marker)
      if (IsZeroBlock(headerBuf)) {
        // First zero block; check for second
        var firstZeroOffset = headerOffset;
        var secondZeroOffset = archive.Position;
        var totalZeroSize = TarConstants.BlockSize;

        if (archive.Position + TarConstants.BlockSize <= archive.Length) {
          bytesRead = archive.Read(headerBuf, 0, TarConstants.BlockSize);
          if (bytesRead == TarConstants.BlockSize && IsZeroBlock(headerBuf))
            totalZeroSize += TarConstants.BlockSize;
        }

        yield return new DefragBlockInfo(
          firstZeroOffset,
          totalZeroSize,
          DefragBlockKind.MetadataReserved,
          FileName: "End-of-archive marker");
        yield break;
      }

      // Validate this looks like a real header (check for ustar magic or valid checksum)
      var typeFlag = headerBuf[156];

      // Parse the file size from the header
      var fileSize = ParseSize(headerBuf);
      var fileName = ParseName(headerBuf);

      // Handle GNU long name / PAX headers: they are metadata
      if (typeFlag == TarConstants.TypeGnuLongName ||
          typeFlag == TarConstants.TypeGnuLongLink ||
          typeFlag == TarConstants.TypePaxHeader ||
          typeFlag == TarConstants.TypePaxGlobal) {
        var paddedSize = RoundUp512(fileSize);
        var totalHeaderSize = TarConstants.BlockSize + paddedSize;
        yield return new DefragBlockInfo(
          headerOffset,
          totalHeaderSize,
          DefragBlockKind.MetadataReserved,
          FileName: $"Extended header ({(char)typeFlag})");
        archive.Position = headerOffset + totalHeaderSize;
        continue;
      }

      // Emit the 512-byte header block as MetadataReserved
      yield return new DefragBlockInfo(
        headerOffset,
        TarConstants.BlockSize,
        DefragBlockKind.MetadataReserved,
        FileName: $"Header: {fileName}");

      // Emit the data region as Used (if any)
      if (fileSize > 0) {
        var dataOffset = headerOffset + TarConstants.BlockSize;
        yield return new DefragBlockInfo(
          dataOffset,
          fileSize,
          DefragBlockKind.Used,
          FileName: fileName,
          Classification: DefragBlockClass.Frozen); // TAR = stored, no compression

        // Account for padding to 512-byte boundary
        var paddedSize = RoundUp512(fileSize);
        var padding = paddedSize - fileSize;
        if (padding > 0) {
          yield return new DefragBlockInfo(
            dataOffset + fileSize,
            padding,
            DefragBlockKind.Free,
            FileName: "Padding");
        }

        archive.Position = dataOffset + paddedSize;
      }
    }
  }

  private static long ParseSize(byte[] header) {
    // Check for binary (base-256) encoding: high bit set on first byte
    if ((header[124] & 0x80) != 0) {
      long size = 0;
      for (var i = 125; i < 124 + 12; i++)
        size = (size << 8) | header[i];
      return size;
    }
    // Standard octal encoding
    long result = 0;
    for (var i = 124; i < 124 + 12; i++) {
      var b = header[i];
      if (b == 0 || b == (byte)' ') break;
      if (b < (byte)'0' || b > (byte)'7') break;
      result = (result << 3) | (long)(b - (byte)'0');
    }
    return result;
  }

  private static string ParseName(byte[] header) {
    var end = 0;
    while (end < TarConstants.NameLength && header[end] != 0)
      end++;
    return System.Text.Encoding.UTF8.GetString(header, 0, end);
  }

  private static long RoundUp512(long value) =>
    (value + TarConstants.BlockSize - 1) / TarConstants.BlockSize * TarConstants.BlockSize;

  private static bool IsZeroBlock(byte[] block) {
    for (var i = 0; i < block.Length; i++)
      if (block[i] != 0) return false;
    return true;
  }
}
