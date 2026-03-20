using Compression.Core.Checksums;

namespace FileFormat.Gzip;

/// <summary>
/// Low-level helpers for working with raw Deflate bitstreams inside Gzip framing.
/// Enables zero-decompression restreaming between formats sharing the Deflate codec.
/// </summary>
public static class GzipRawHelper {

  /// <summary>
  /// Extracts the raw Deflate bitstream from a Gzip stream without decompressing.
  /// Also returns the CRC-32 and original size from the trailer.
  /// </summary>
  /// <param name="gzipData">The complete Gzip data.</param>
  /// <returns>The raw Deflate bytes, CRC-32 of uncompressed data, and original size mod 2^32.</returns>
  public static (byte[] DeflateData, uint Crc32, uint OriginalSize) Unwrap(ReadOnlySpan<byte> gzipData) {
    // Parse header to find where Deflate data starts
    using var ms = new MemoryStream(gzipData.ToArray(), writable: false);
    _ = GzipHeader.Read(ms);
    int headerEnd = (int)ms.Position;

    // Trailer is last 8 bytes: CRC32 (4) + ISIZE (4), both little-endian
    if (gzipData.Length < headerEnd + 8)
      throw new InvalidDataException("Gzip data too short for trailer.");

    var trailerStart = gzipData.Length - 8;
    uint crc32 = BitConverter.ToUInt32(gzipData[trailerStart..]);
    uint originalSize = BitConverter.ToUInt32(gzipData[(trailerStart + 4)..]);

    // Raw Deflate is everything between header and trailer
    var deflateData = gzipData[headerEnd..trailerStart].ToArray();
    return (deflateData, crc32, originalSize);
  }

  /// <summary>
  /// Wraps a raw Deflate bitstream in Gzip framing.
  /// The caller provides the CRC-32 and original size (already known from the source format).
  /// </summary>
  /// <param name="deflateData">The raw Deflate bitstream.</param>
  /// <param name="crc32">CRC-32 of the uncompressed data.</param>
  /// <param name="originalSize">Original uncompressed size mod 2^32.</param>
  /// <returns>Complete Gzip data.</returns>
  public static byte[] Wrap(ReadOnlySpan<byte> deflateData, uint crc32, uint originalSize) {
    using var ms = new MemoryStream();
    new GzipHeader().Write(ms);
    ms.Write(deflateData);
    var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
    writer.Write(crc32);
    writer.Write(originalSize);
    writer.Flush();
    return ms.ToArray();
  }

  /// <summary>
  /// Wraps a raw Deflate bitstream in Gzip framing, writing to a stream.
  /// </summary>
  public static void Wrap(Stream output, ReadOnlySpan<byte> deflateData, uint crc32, uint originalSize) {
    new GzipHeader().Write(output);
    output.Write(deflateData);
    var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
    writer.Write(crc32);
    writer.Write(originalSize);
    writer.Flush();
  }
}
