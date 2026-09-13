using FileFormat.Zstd;
using Compression.Core.Streams;

namespace FileFormat.Zip;

/// <summary>
/// Helper to compress and decompress Zstd-compressed ZIP entry data.
/// </summary>
internal static class ZipZstdHelper {
  /// <summary>
  /// Decompresses Zstd-compressed data from a ZIP entry.
  /// </summary>
  public static byte[] Decompress(byte[] compressedData) {
    using var input = new MemoryStream(compressedData);
    using var zstd = new ZstdStream(input, CompressionStreamMode.Decompress, leaveOpen: true);
    using var output = new MemoryStream();
    zstd.CopyTo(output);
    return output.ToArray();
  }

  /// <summary>
  /// Compresses data using Zstd for storage in a ZIP entry.
  /// </summary>
  public static byte[] Compress(byte[] data) {
    using var output = new MemoryStream();
    using (var zstd = new ZstdStream(output, CompressionStreamMode.Compress, leaveOpen: true))
      zstd.Write(data, 0, data.Length);
    return output.ToArray();
  }
}
