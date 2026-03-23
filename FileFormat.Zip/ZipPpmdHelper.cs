using Compression.Core.Entropy.Ppmd;

namespace FileFormat.Zip;

/// <summary>
/// Helper to compress and decompress PPMd-compressed ZIP entry data.
/// ZIP PPMd format (method 98): 2-byte properties header (order, memSizeMB) + range-coded data.
/// Uses PPMd version I, Rev 1 (PpmdModelI).
/// </summary>
internal static class ZipPpmdHelper {
  /// <summary>
  /// Decompresses PPMd-compressed data from a ZIP entry.
  /// </summary>
  public static byte[] Decompress(byte[] compressedData, int uncompressedSize) {
    if (compressedData.Length < 2)
      throw new InvalidDataException("ZIP PPMd data too short for properties.");

    int order = compressedData[0];
    int memSizeMB = compressedData[1];
    var memorySize = memSizeMB * (1 << 20);

    using var input = new MemoryStream(compressedData, 2, compressedData.Length - 2);
    var rangeDecoder = new PpmdRangeDecoder(input);
    var model = new PpmdModelI(order, memorySize);

    var output = new byte[uncompressedSize];
    for (var i = 0; i < uncompressedSize; ++i)
      output[i] = model.DecodeSymbol(rangeDecoder);

    return output;
  }

  /// <summary>
  /// Compresses data using PPMd for storage in a ZIP entry.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="order">PPMd model order (2-16). Default 6.</param>
  /// <param name="memSizeMB">Memory size in megabytes (1-256). Default 8.</param>
  public static byte[] Compress(byte[] data, int order = 6, int memSizeMB = 8) {
    if (order < 2 || order > 16)
      throw new ArgumentOutOfRangeException(nameof(order), "ZIP PPMd order must be 2-16.");
    if (memSizeMB < 1 || memSizeMB > 256)
      throw new ArgumentOutOfRangeException(nameof(memSizeMB), "ZIP PPMd memory must be 1-256 MB.");

    using var output = new MemoryStream();

    // Write 2-byte properties header
    output.WriteByte((byte)order);
    output.WriteByte((byte)memSizeMB);

    var rangeEncoder = new PpmdRangeEncoder(output);
    var model = new PpmdModelI(order, memSizeMB * (1 << 20));

    for (var i = 0; i < data.Length; ++i)
      model.EncodeSymbol(rangeEncoder, data[i]);

    rangeEncoder.Finish();
    return output.ToArray();
  }
}
