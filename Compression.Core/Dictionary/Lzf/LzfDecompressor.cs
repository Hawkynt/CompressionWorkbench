namespace Compression.Core.Dictionary.Lzf;

/// <summary>
/// Decodes the LZF wire format produced by <see cref="LzfCompressor"/>.
/// Format reference: http://software.schmorp.de/pkg/liblzf.html.
/// </summary>
public static class LzfDecompressor {
  /// <summary>Decompresses an LZF stream.</summary>
  /// <param name="data">The compressed byte stream.</param>
  /// <param name="originalLength">The exact length of the decompressed output.</param>
  /// <returns>The reconstructed original bytes.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int originalLength) {
    var output = new byte[originalLength];
    var op = 0;
    var ip = 0;

    while (op < originalLength) {
      var ctrl = data[ip++];

      if (ctrl < 32) {
        var length = ctrl + 1;
        data.Slice(ip, length).CopyTo(output.AsSpan(op));
        ip += length;
        op += length;
        continue;
      }

      var lengthField = ctrl >> 5;
      int length2;
      int offset;

      if (lengthField == 7) {
        length2 = data[ip++] + 7;
        offset = ((ctrl & 0x1F) << 8) | data[ip++];
      } else {
        length2 = lengthField;
        offset = ((ctrl & 0x1F) << 8) | data[ip++];
      }

      length2 += 1;
      offset += 1;

      var refPos = op - offset;
      for (var i = 0; i < length2; ++i)
        output[op + i] = output[refPos + i];
      op += length2;
    }

    return output;
  }
}
