using FileFormat.Bzip2;
using Compression.Core.Streams;

namespace FileFormat.Zip;

/// <summary>
/// Helper to compress and decompress BZip2-compressed ZIP entry data.
/// </summary>
internal static class ZipBzip2Helper {
  /// <summary>
  /// Decompresses BZip2-compressed data from a ZIP entry.
  /// </summary>
  public static byte[] Decompress(byte[] compressedData) {
    using var input = new MemoryStream(compressedData);
    using var bzip2 = new Bzip2Stream(input, CompressionStreamMode.Decompress, leaveOpen: true);
    using var output = new MemoryStream();
    bzip2.CopyTo(output);
    return output.ToArray();
  }

  /// <summary>
  /// Compresses data using BZip2 for storage in a ZIP entry.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="blockSize100k">Block size multiplier 1-9 (N × 100 KB). Default 9.</param>
  public static byte[] Compress(byte[] data, int blockSize100k = 9) {
    using var output = new MemoryStream();
    using (var bzip2 = new Bzip2Stream(output, CompressionStreamMode.Compress, blockSize100k: blockSize100k, leaveOpen: true))
      bzip2.Write(data, 0, data.Length);
    return output.ToArray();
  }
}
