namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes -lz5- compressed data (LArc variant).
/// Uses LZSS with a 4KB sliding window, no Huffman coding.
/// </summary>
/// <remarks>
/// Similar to -lzs- but with a 4KB window.
/// Flag byte encodes 8 tokens. Bit 1 = literal, bit 0 = match.
/// Match: 12-bit offset (first byte + high 4 bits of second byte),
/// 4-bit length in low nibble of second byte. Minimum match = 2.
/// Window initialized to spaces.
/// </remarks>
public static class Lz5Decoder {
  private const int WindowSize = 4096;
  private const int WindowMask = WindowSize - 1;
  private const int Threshold = 2;

  /// <summary>
  /// Decodes -lz5- compressed data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decode(ReadOnlySpan<byte> compressed, int originalSize)
    => Decode(compressed.ToArray(), originalSize);

  /// <summary>
  /// Decodes -lz5- compressed data.
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

          var offset = b1 | ((b2 & 0xF0) << 4);
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
