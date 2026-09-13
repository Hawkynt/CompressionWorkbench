using Compression.Core.Dictionary.Lzma;

namespace FileFormat.Zip;

/// <summary>
/// Helper to compress and decompress LZMA-compressed ZIP entry data.
/// ZIP LZMA format: 2-byte version header + 2-byte properties size + LZMA properties + compressed data.
/// </summary>
internal static class ZipLzmaHelper {
  /// <summary>
  /// Decompresses LZMA-compressed data from a ZIP entry.
  /// </summary>
  public static byte[] Decompress(byte[] compressedData) {
    if (compressedData.Length < 9)
      throw new InvalidDataException("ZIP LZMA data too short.");

    // Skip 2-byte version major/minor
    var offset = 2;

    // Read 2-byte properties size (little-endian)
    var propsSize = compressedData[offset] | (compressedData[offset + 1] << 8);
    offset += 2;

    if (offset + propsSize > compressedData.Length)
      throw new InvalidDataException("ZIP LZMA properties extend past data.");

    var properties = new byte[propsSize];
    compressedData.AsSpan(offset, propsSize).CopyTo(properties);
    offset += propsSize;

    using var input = new MemoryStream(compressedData, offset, compressedData.Length - offset);
    var decoder = new LzmaDecoder(input, properties);
    return decoder.Decode();
  }

  /// <summary>
  /// Compresses data using LZMA for storage in a ZIP entry.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="dictionarySize">Dictionary size in bytes (4096 to 1GB). Default 8 MB.</param>
  /// <param name="level">Compression level. Default Normal.</param>
  public static byte[] Compress(byte[] data, int dictionarySize = 1 << 23,
      LzmaCompressionLevel level = LzmaCompressionLevel.Normal) {
    var encoder = new LzmaEncoder(dictionarySize: dictionarySize, level: level);
    using var output = new MemoryStream();

    // Write 2-byte version (9.20 = 0x09, 0x14)
    output.WriteByte(0x09);
    output.WriteByte(0x14);

    // Write 2-byte properties size (5 bytes for standard LZMA properties)
    var props = encoder.Properties;
    output.WriteByte((byte)props.Length);
    output.WriteByte(0);

    // Write properties
    output.Write(props);

    // Write compressed data
    encoder.Encode(output, data);
    return output.ToArray();
  }
}
