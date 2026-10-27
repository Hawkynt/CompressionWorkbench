using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Encodes data using the LHA -lh1- adaptive Huffman method.
/// </summary>
/// <remarks>
/// 4KB sliding window (initialized to 0x20), 314 symbols (256 literals +
/// 58 match-length codes), adaptive Huffman rebuilt every 4096 symbols.
/// Positions encoded as 6+6 = 12 bits.
/// </remarks>
public sealed class Lh1Encoder {
  private const int NChar = 256;
  private const int WindowSize = 4096;
  private const int WindowMask = WindowSize - 1;
  private const int Threshold = 2;
  private const int MaxMatch = 60;
  private const int NumCodes = NChar + MaxMatch - Threshold + 1; // 314
  private const int BlockSize = 4096;
  private const int PositionBits = 6;

  /// <summary>
  /// Compresses data using the -lh1- algorithm.
  /// </summary>
  /// <param name="data">The input data to compress.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    using var output = new MemoryStream();
    var bits = new BitWriter<MsbBitOrder>(output);

    var window = new byte[WindowSize];
    Array.Fill(window, (byte)0x20);
    var windowPos = 0;

    var freq = new int[NumCodes];
    Array.Fill(freq, 1);
    var symbolCount = 0;

    var (codeLengths, codes) = BuildHuffmanTable(freq);

    var i = 0;
    while (i < data.Length) {
      // Rebuild tree periodically
      if (symbolCount >= BlockSize) {
        for (var s = 0; s < NumCodes; ++s)
          freq[s] = (freq[s] + 1) >> 1;
        (codeLengths, codes) = BuildHuffmanTable(freq);
        symbolCount = 0;
      }

      // Find best match in window
      var bestLen = 0;
      var bestPos = 0;

      for (var j = 1; j < WindowSize && j <= i; ++j) {
        var srcIdx = (windowPos - j + WindowSize) & WindowMask;
        var len = 0;
        while (len < MaxMatch && i + len < data.Length) {
          var wIdx = (srcIdx + len) & WindowMask;
          if (window[wIdx] != data[i + len])
            break;
          ++len;
        }
        if (len > bestLen && len >= Threshold) {
          bestLen = len;
          bestPos = j - 1; // position = distance - 1
        }
      }

      if (bestLen >= Threshold) {
        var code = NChar + bestLen - Threshold;
        bits.WriteBits(codes[code], codeLengths[code]);
        ++freq[code];
        ++symbolCount;

        // Write position: 6 high bits + 6 low bits
        var posHigh = bestPos >> 6;
        var posLow = bestPos & 0x3F;
        bits.WriteBits((uint)posHigh, PositionBits);
        bits.WriteBits((uint)posLow, 6);

        for (var k = 0; k < bestLen; ++k) {
          window[windowPos] = data[i + k];
          windowPos = (windowPos + 1) & WindowMask;
        }
        i += bestLen;
      }
      else {
        var b = data[i];
        bits.WriteBits(codes[b], codeLengths[b]);
        ++freq[b];
        ++symbolCount;

        window[windowPos] = b;
        windowPos = (windowPos + 1) & WindowMask;
        ++i;
      }
    }

    bits.FlushBits();
    return output.ToArray();
  }

  private static (int[] Lengths, uint[] Codes) BuildHuffmanTable(int[] freq) {
    var lengths = LzhEncoder.BuildCodeLengths(freq, 16);
    var codes = LzhEncoder.BuildCanonicalCodes(lengths);
    return (lengths, codes);
  }
}
