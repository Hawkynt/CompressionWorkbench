namespace Compression.Core.Dictionary.FastLz;

/// <summary>
/// FastLZ decompression exactly as <c>fastlz_decompress</c> performs it, at both levels.
/// </summary>
/// <remarks>
/// Converted from FastLZ <c>fastlz.c</c> (MIT; see THIRD-PARTY-NOTICE.FastLZ.txt):
/// <c>fastlz1_decompress</c> and <c>fastlz2_decompress</c>, chosen by the level in the first
/// byte. See <see cref="FastLzCompressor"/> for the format.
/// </remarks>
public static class FastLzDecompressor {
  private const int MaxL2Distance = 8191;

  /// <summary>Decompresses a FastLZ block that expands to <paramref name="originalLength"/> bytes.</summary>
  /// <param name="data">The FastLZ block.</param>
  /// <param name="originalLength">The exact uncompressed size.</param>
  /// <returns>The decompressed bytes.</returns>
  /// <exception cref="InvalidDataException">
  /// The block names an unknown level, is truncated, reaches before its start or past
  /// <paramref name="originalLength"/>, or expands to fewer bytes.
  /// </exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int originalLength) {
    ArgumentOutOfRangeException.ThrowIfNegative(originalLength);
    if (data.IsEmpty)
      return originalLength == 0 ? [] : throw new InvalidDataException("FastLZ: empty block.");

    var output = new byte[originalLength];
    var level = (data[0] >> 5) + 1;
    var produced = level switch {
      1 => Decode(data, output, level2: false),
      2 => Decode(data, output, level2: true),
      _ => throw new InvalidDataException($"FastLZ: unknown level {level}."),
    };
    if (produced != originalLength)
      throw new InvalidDataException($"FastLZ block expanded to {produced} bytes, expected {originalLength}.");
    return output;
  }

  private static int Decode(ReadOnlySpan<byte> data, byte[] output, bool level2) {
    var ip = 0;
    var ipLimit = data.Length;
    var ipBound = ipLimit - 2;
    var op = 0;
    var control = data[ip++] & 31;

    while (true) {
      if (control >= 32) {
        var length = (control >> 5) - 1;
        var offset = (control & 31) << 8;
        var reference = op - offset - 1;
        if (length == 7 - 1) {
          if (level2) {
            int code;
            do {
              Check(ip <= ipBound, "match length is truncated");
              code = data[ip++];
              length += code;
            } while (code == 255);
          } else {
            Check(ip <= ipBound, "match length is truncated");
            length += data[ip++];
          }
        }

        Check(ip < ipLimit, "match distance is truncated");
        var low = data[ip++];
        reference -= low;
        length += 3;
        if (level2 && low == 255 && offset == 31 << 8) {
          Check(ip < ipBound, "far distance is truncated");
          offset = data[ip++] << 8;
          offset += data[ip++];
          reference = op - offset - MaxL2Distance - 1;
        }

        Check(op + length <= output.Length, "match runs past the output");
        Check(reference >= 0, "match reaches before the start");
        // Byte by byte: an overlapping match repeats what it has just written.
        for (var i = 0; i < length; ++i)
          output[op++] = output[reference++];
      } else {
        ++control;
        Check(op + control <= output.Length, "literal run runs past the output");
        Check(ip + control <= ipLimit, "literal run is truncated");
        data.Slice(ip, control).CopyTo(output.AsSpan(op));
        ip += control;
        op += control;
      }

      if (level2 ? ip >= ipLimit : ip > ipBound)
        break;
      control = data[ip++];
    }

    return op;
  }

  private static void Check(bool condition, string what) {
    if (!condition)
      throw new InvalidDataException("FastLZ: " + what + ".");
  }
}
