namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Encodes data using the -lzs- format (LArc).
/// Plain LZSS with a 2KB sliding window, no Huffman coding.
/// </summary>
public static class LzsEncoder {
  private const int WindowSize = 2048;
  private const int WindowMask = WindowSize - 1;
  private const int Threshold = 2;
  private const int MaxMatch = 17; // 4-bit length + 2

  /// <summary>
  /// Encodes data using the -lzs- format.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data)
    => Encode(data.ToArray());

  /// <summary>
  /// Encodes data using the -lzs- format.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Encode(byte[] data) {
    var window = new byte[WindowSize];
    Array.Fill(window, (byte)0x20);
    var windowPos = 0;

    using var output = new MemoryStream();
    var srcPos = 0;

    while (srcPos < data.Length) {
      var flagPos = (int)output.Position;
      output.WriteByte(0); // placeholder for flag byte
      var flags = 0;

      for (var bit = 0; bit < 8 && srcPos < data.Length; ++bit) {
        // Find best match in window
        var bestLen = 0;
        var bestOff = 0;

        for (var off = 1; off < WindowSize && off <= srcPos; ++off) {
          var matchPos = (windowPos - off) & WindowMask;
          var len = 0;
          while (len < MaxMatch && srcPos + len < data.Length
              && window[(matchPos + len) & WindowMask] == data[srcPos + len])
            ++len;

          if (len > bestLen) {
            bestLen = len;
            bestOff = matchPos;
          }
        }

        if (bestLen >= Threshold) {
          // Match: encode offset + length
          var b1 = bestOff & 0xFF;
          var b2 = ((bestOff >> 3) & 0xE0) | ((bestLen - Threshold) & 0x0F);
          output.WriteByte((byte)b1);
          output.WriteByte((byte)b2);

          for (var j = 0; j < bestLen; ++j) {
            window[windowPos] = data[srcPos++];
            windowPos = (windowPos + 1) & WindowMask;
          }
        } else {
          // Literal
          flags |= 1 << bit;
          output.WriteByte(data[srcPos]);
          window[windowPos] = data[srcPos++];
          windowPos = (windowPos + 1) & WindowMask;
        }
      }

      // Patch flag byte
      var savedPos = output.Position;
      output.Position = flagPos;
      output.WriteByte((byte)flags);
      output.Position = savedPos;
    }

    return output.ToArray();
  }
}
