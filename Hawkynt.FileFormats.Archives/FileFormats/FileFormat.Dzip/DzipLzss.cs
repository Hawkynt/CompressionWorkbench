#pragma warning disable CS1591
namespace FileFormat.Dzip;

/// <summary>
/// Decoder for the Bloodlines DZIP LZSS variant: 8-bit control byte gates 8 operations
/// (literal byte or 2-byte length-distance reference). Distance is 12 bits (max 4096),
/// match length is 4 bits + 3 (range 3..18).
/// </summary>
public static class DzipLzss {

  /// <summary>
  /// Decompresses a Bloodlines DZIP LZSS stream.
  /// </summary>
  /// <param name="compressed">The compressed input bytes.</param>
  /// <param name="expectedSize">The expected uncompressed output size.</param>
  /// <returns>The decompressed bytes (length == <paramref name="expectedSize"/>).</returns>
  /// <exception cref="InvalidDataException">Input is malformed, truncated, or output size mismatches.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed, int expectedSize) {
    if (expectedSize < 0)
      throw new ArgumentOutOfRangeException(nameof(expectedSize), "Expected size must be non-negative.");

    if (expectedSize == 0)
      return [];

    var output = new byte[expectedSize];
    var outPos = 0;
    var inPos = 0;

    while (outPos < expectedSize) {
      if (inPos >= compressed.Length)
        throw new InvalidDataException("Truncated DZIP LZSS stream: missing control byte.");

      var control = compressed[inPos++];

      for (var bit = 0; bit < 8; ++bit) {
        if (outPos >= expectedSize)
          break;

        var isLiteral = (control & (1 << bit)) != 0;

        if (isLiteral) {
          if (inPos >= compressed.Length)
            throw new InvalidDataException("Truncated DZIP LZSS stream: missing literal byte.");

          output[outPos++] = compressed[inPos++];
          continue;
        }

        if (inPos + 1 >= compressed.Length)
          throw new InvalidDataException("Truncated DZIP LZSS stream: missing back-reference bytes.");

        var hi = compressed[inPos++];
        var lo = compressed[inPos++];

        var length = (hi & 0x0F) + DzipConstants.LzssMinMatch;
        var distance = ((hi >> 4) | (lo << 4)) + 1;

        if (distance > DzipConstants.LzssWindowSize)
          throw new InvalidDataException($"DZIP LZSS distance {distance} exceeds {DzipConstants.LzssWindowSize}-byte window.");

        if (distance > outPos)
          throw new InvalidDataException($"DZIP LZSS back-reference distance {distance} exceeds current output position {outPos}.");

        var copyEnd = Math.Min(outPos + length, expectedSize);
        var src = outPos - distance;
        for (var i = outPos; i < copyEnd; ++i)
          output[i] = output[src++];

        outPos = copyEnd;
      }
    }

    if (outPos != expectedSize)
      throw new InvalidDataException($"DZIP LZSS produced {outPos} bytes, expected {expectedSize}.");

    return output;
  }
}
