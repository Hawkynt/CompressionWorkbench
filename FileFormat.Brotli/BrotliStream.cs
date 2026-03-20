using Compression.Core.Dictionary.Brotli;

namespace FileFormat.Brotli;

/// <summary>
/// Provides Brotli compression and decompression as a stream wrapper (RFC 7932).
/// </summary>
/// <remarks>
/// Brotli is a general-purpose lossless compression format widely used on the web.
/// This stream wrapper provides convenient compress/decompress APIs over the
/// underlying <see cref="BrotliCompressor"/> and <see cref="BrotliDecompressor"/>.
/// </remarks>
public static class BrotliStream {
  /// <summary>
  /// Decompresses Brotli data from the given stream.
  /// </summary>
  /// <param name="input">The stream containing Brotli-compressed data.</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="InvalidDataException">Thrown when the data is not valid Brotli.</exception>
  public static byte[] Decompress(Stream input) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    return BrotliDecompressor.Decompress(ms.ToArray());
  }

  /// <summary>
  /// Decompresses Brotli data from the given byte span.
  /// </summary>
  /// <param name="data">The Brotli-compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data) =>
    BrotliDecompressor.Decompress(data);

  /// <summary>
  /// Compresses data to Brotli format.
  /// Uses LZ77 + Huffman for data large enough to benefit; falls back to
  /// uncompressed meta-blocks for very short inputs.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The Brotli-compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.Length < 16)
      return BrotliCompressor.Compress(data);

    byte[] lz77 = BrotliCompressor.CompressLz77(data);
    byte[] uncomp = BrotliCompressor.Compress(data);
    return lz77.Length < uncomp.Length ? lz77 : uncomp;
  }

  /// <summary>
  /// Compresses data to Brotli format at the specified compression level.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="level">The compression level.</param>
  /// <returns>The Brotli-compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, BrotliCompressionLevel level) =>
    BrotliCompressor.Compress(data, level);

  /// <summary>
  /// Compresses data from a stream to Brotli format.
  /// </summary>
  /// <param name="input">The input stream.</param>
  /// <returns>The Brotli-compressed data.</returns>
  public static byte[] Compress(Stream input) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    return Compress(ms.ToArray().AsSpan());
  }
}
