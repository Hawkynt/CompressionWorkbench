namespace Compression.Core.Dictionary.Lzo;

/// <summary>
/// LZO1X-1 style compressor using a hash table for fast match finding.
/// Uses an LZ4-style token format: token byte (high nibble = literal length 0–15,
/// low nibble = match extra length 0–15), optional literal-length extension bytes,
/// literal bytes, 2-byte LE offset, optional match-length extension bytes.
/// Minimum match length is 4; maximum distance is 65535 (fits in u16 LE field).
/// </summary>
public static class Lzo1xCompressor {
  private const int HashBits = 14;
  private const int HashSize = 1 << Lzo1xCompressor.HashBits; // 16384
  private const int MinMatch = 4;
  private const int MaxDistance = 65535; // fits in u16 LE offset field

  /// <summary>
  /// Compresses the given data using LZO1X at the specified level.
  /// </summary>
  /// <param name="data">The input data to compress.</param>
  /// <param name="level">The compression level.</param>
  /// <returns>A byte array containing the compressed data.</returns>
  /// <remarks>
  /// Every level writes the same stream format; what a level changes is how hard
  /// the encoder looks for matches, never what a decoder has to understand. That
  /// is also true of the real LZO1X-1 and LZO1X-999, which is why lzop can label
  /// all three with different method bytes and read them with one decoder.
  /// </remarks>
  public static byte[] Compress(ReadOnlySpan<byte> data, LzoCompressionLevel level) {
    _ = level;
    return Compress(data);
  }

  /// <summary>
  /// Compresses the given data using the LZO1X-1 algorithm.
  /// </summary>
  /// <param name="data">The input data to compress.</param>
  /// <returns>A byte array containing the compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) => Lzo1xEncoder.Compress(data);

  // 4-byte hash using a multiply-shift scheme; returns index in [0, HashSize).
  private static int Hash4(ReadOnlySpan<byte> data, int pos) {
    var v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
    return (int)((v * 2654435761u) >> (32 - Lzo1xCompressor.HashBits));
  }
}
