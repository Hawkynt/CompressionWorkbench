namespace Compression.Core.Dictionary.Lzrw1;

/// <summary>
/// Decodes the LZRW1 control-word format produced by <see cref="Lzrw1Compressor"/>.
/// Reference: Ross N. Williams, http://ross.net/compression/lzrw1.html.
/// </summary>
public static class Lzrw1Decompressor {
  private const int MinMatch = 3;
  private const int ItemsPerGroup = 16;

  /// <summary>Decompresses an LZRW1 stream.</summary>
  /// <param name="data">The compressed byte stream.</param>
  /// <param name="originalLength">The exact length of the decompressed output.</param>
  /// <returns>The reconstructed original bytes.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int originalLength) {
    var output = new byte[originalLength];
    var op = 0;
    var ip = 0;

    while (op < originalLength) {
      var controlWord = (data[ip] << 8) | data[ip + 1];
      ip += 2;

      for (var i = 0; i < Lzrw1Decompressor.ItemsPerGroup && op < originalLength; ++i) {
        if ((controlWord & (1 << i)) != 0) {
          var code = (data[ip] << 8) | data[ip + 1];
          ip += 2;
          var length = (code >> 12) + Lzrw1Decompressor.MinMatch;
          var offset = (code & 0xFFF) + 1;

          var refPos = op - offset;
          for (var j = 0; j < length; ++j)
            output[op + j] = output[refPos + j];
          op += length;
        } else
          output[op++] = data[ip++];
      }
    }

    return output;
  }
}
