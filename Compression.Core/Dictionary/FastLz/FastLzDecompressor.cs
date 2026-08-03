namespace Compression.Core.Dictionary.FastLz;

/// <summary>
/// Decodes the FastLZ level-1 block format produced by <see cref="FastLzCompressor"/>.
/// Format reference: https://ariya.github.io/FastLZ/ (block format section).
/// </summary>
public static class FastLzDecompressor {
  /// <summary>Decompresses a FastLZ level-1 stream.</summary>
  /// <param name="data">The compressed byte stream.</param>
  /// <param name="originalLength">The exact length of the decompressed output.</param>
  /// <returns>The reconstructed original bytes.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int originalLength) {
    var output = new byte[originalLength];
    var op = 0;
    var ip = 0;

    while (op < originalLength) {
      var opcode = data[ip++];
      var type = opcode >> 5;

      if (type == 0) {
        var length = (opcode & 0x1F) + 1;
        data.Slice(ip, length).CopyTo(output.AsSpan(op));
        ip += length;
        op += length;
        continue;
      }

      var high = opcode & 0x1F;
      var low = data[ip++];
      var encodedDistance = (high << 8) | low;

      int length2;
      if (type == 7) {
        var extra = data[ip++];
        length2 = extra + 9;
      } else
        length2 = type + 2;

      var distance = encodedDistance + 1;
      var refPos = op - distance;
      for (var i = 0; i < length2; ++i)
        output[op + i] = output[refPos + i];
      op += length2;
    }

    return output;
  }
}
