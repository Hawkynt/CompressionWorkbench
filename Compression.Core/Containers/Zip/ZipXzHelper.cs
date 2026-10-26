using Compression.Core.Dictionary.Lzma;
using Compression.Core.Streams;
using FileFormat.Xz;

namespace FileFormat.Zip;

/// <summary>
/// Compresses and decompresses ZIP method 95 entry data. APPNOTE 4.4.5 defines the payload
/// as a complete .xz stream (stream header, blocks, index, footer), so the entry bytes are
/// exactly what a stand-alone <c>xz</c> would write.
/// </summary>
internal static class ZipXzHelper {
  /// <summary>Decompresses an XZ-compressed ZIP entry.</summary>
  public static byte[] Decompress(byte[] compressedData) {
    using var input = new MemoryStream(compressedData, writable: false);
    using var xz = new XzStream(input, CompressionStreamMode.Decompress, leaveOpen: true);
    using var output = new MemoryStream();
    xz.CopyTo(output);
    return output.ToArray();
  }

  /// <summary>Compresses data into a single .xz stream (LZMA2, CRC-64 check).</summary>
  public static byte[] Compress(byte[] data, int dictionarySize = 1 << 23,
      LzmaCompressionLevel level = LzmaCompressionLevel.Normal) {
    using var output = new MemoryStream();
    using (var xz = new XzStream(output, CompressionStreamMode.Compress, dictionarySize,
             XzConstants.CheckCrc64, null, level, leaveOpen: true))
      xz.Write(data, 0, data.Length);
    return output.ToArray();
  }
}
