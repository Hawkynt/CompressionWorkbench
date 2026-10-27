namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes -lzs- compressed data (LArc format).
/// Uses plain LZSS with a 2KB sliding window, no Huffman coding.
/// </summary>
/// <remarks>
/// Format: flag byte, then 8 tokens per flag.
/// Flag bit 1 = literal byte.
/// Flag bit 0 = match: 11-bit offset (big-endian across 2 bytes) + 4-bit length.
/// The 2 bytes encode: high 8 bits = first byte, low 3 bits of offset in top 3 bits of second byte,
/// length in low 4 bits of second byte. Minimum match = 2. Window initialized to spaces.
/// </remarks>
public static class LzsDecoder {
  private const int WindowSize = 2048;
  private const int WindowMask = WindowSize - 1;
  private const int Threshold = 2;

  /// <summary>
  /// Decodes -lzs- compressed data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decode(ReadOnlySpan<byte> compressed, int originalSize)
    => Decode(compressed.ToArray(), originalSize);

  /// <summary>
  /// Decodes -lzs- compressed data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decode(byte[] compressed, int originalSize) {
    var output = new byte[originalSize];
    var window = new byte[WindowSize];
    Array.Fill(window, (byte)0x20);
    var windowPos = 0;
    var outPos = 0;
    var srcPos = 0;

    while (outPos < originalSize && srcPos < compressed.Length) {
      int flags = compressed[srcPos++];

      for (var bit = 0; bit < 8 && outPos < originalSize; ++bit) {
        if ((flags & (1 << bit)) != 0) {
          // Literal
          if (srcPos >= compressed.Length)
            return output;
          var b = compressed[srcPos++];
          output[outPos++] = b;
          window[windowPos] = b;
          windowPos = (windowPos + 1) & WindowMask;
        } else {
          // Match
          if (srcPos + 1 >= compressed.Length)
            return output;
          int b1 = compressed[srcPos++];
          int b2 = compressed[srcPos++];

          var offset = b1 | ((b2 & 0xE0) << 3);
          var length = (b2 & 0x0F) + Threshold;

          for (var j = 0; j < length && outPos < originalSize; ++j) {
            var b = window[(offset + j) & WindowMask];
            output[outPos++] = b;
            window[windowPos] = b;
            windowPos = (windowPos + 1) & WindowMask;
          }
        }
      }
    }

    return output;
  }
}
