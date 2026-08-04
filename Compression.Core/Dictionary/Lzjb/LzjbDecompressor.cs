namespace Compression.Core.Dictionary.Lzjb;

/// <summary>
/// Decodes the LZJB copymap format produced by <see cref="LzjbCompressor"/>.
/// Reference: https://en.wikipedia.org/wiki/LZJB.
/// </summary>
public static class LzjbDecompressor {
  private const int MatchBits = 6;
  private const int MatchMin = 3;
  private const int LengthMask = (1 << LzjbDecompressor.MatchBits) - 1;
  private const int ItemsPerGroup = 8;

  /// <summary>Decompresses an LZJB stream.</summary>
  /// <param name="data">The compressed byte stream.</param>
  /// <param name="originalLength">The exact length of the decompressed output.</param>
  /// <returns>The reconstructed original bytes.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int originalLength) {
    var output = new byte[originalLength];
    var op = 0;
    var ip = 0;
    var copymap = 0;
    var bit = LzjbDecompressor.ItemsPerGroup;

    while (op < originalLength) {
      if (bit == LzjbDecompressor.ItemsPerGroup) {
        copymap = data[ip++];
        bit = 0;
      }

      if (((copymap >> bit) & 1) != 0) {
        var code = data[ip] | (data[ip + 1] << 8);
        ip += 2;
        var length = (code & LzjbDecompressor.LengthMask) + LzjbDecompressor.MatchMin;
        var offset = code >> LzjbDecompressor.MatchBits;

        var refPos = op - offset;
        for (var i = 0; i < length; ++i)
          output[op + i] = output[refPos + i];
        op += length;
      } else
        output[op++] = data[ip++];

      ++bit;
    }

    return output;
  }
}
