namespace Compression.Core.Dictionary.Lzp;

/// <summary>
/// LZP (Lempel-Ziv Prediction) decompressor. Rebuilds the original data from a
/// compressed stream produced by <see cref="LzpCompressor"/>.
/// </summary>
public static class LzpDecompressor {
  private const int HashBits = 20;
  private const int HashSize = 1 << HashBits;
  private const uint HashMask = HashSize - 1;

  /// <summary>
  /// Decompresses data that was compressed with <see cref="LzpCompressor.Compress"/>.
  /// </summary>
  /// <param name="compressed">The compressed data including the LZP header.</param>
  /// <returns>The original uncompressed data.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="compressed"/> is null.</exception>
  /// <exception cref="InvalidDataException">Thrown when the compressed data is too short or corrupt.</exception>
  public static byte[] Decompress(byte[] compressed) {
    ArgumentNullException.ThrowIfNull(compressed);
    if (compressed.Length < 5)
      throw new InvalidDataException("LZP compressed data is too short (missing header).");

    var originalSize = BitConverter.ToInt32(compressed, 0);
    int order = compressed[4];

    if (originalSize < 0)
      throw new InvalidDataException("LZP header contains a negative original size.");

    if (originalSize == 0)
      return [];

    var output = new byte[originalSize];
    var hashTable = new byte[HashSize];

    var srcPos = 5; // past header
    var dstPos = 0;

    while (dstPos < originalSize) {
      if (srcPos >= compressed.Length)
        throw new InvalidDataException("Unexpected end of LZP compressed data.");

      var flags = compressed[srcPos++];
      var count = Math.Min(8, originalSize - dstPos);

      for (var bit = 0; bit < count; bit++) {
        if (dstPos < order) {
          // Not enough context — must be a literal.
          if (srcPos >= compressed.Length)
            throw new InvalidDataException("Unexpected end of LZP compressed data.");

          output[dstPos] = compressed[srcPos++];
          dstPos++;
          continue;
        }

        var hash = ComputeHash(output, dstPos, order);
        var isMatch = (flags & (1 << bit)) != 0;

        if (isMatch) {
          output[dstPos] = hashTable[hash];
        } else {
          if (srcPos >= compressed.Length)
            throw new InvalidDataException("Unexpected end of LZP compressed data.");

          output[dstPos] = compressed[srcPos++];
        }

        hashTable[hash] = output[dstPos];
        dstPos++;
      }
    }

    return output;
  }

  /// <summary>
  /// Computes a 20-bit FNV-1a hash of the <paramref name="order"/> bytes preceding position <paramref name="pos"/>.
  /// </summary>
  private static int ComputeHash(byte[] data, int pos, int order) {
    var h = 2166136261u;
    for (var i = pos - order; i < pos; i++) {
      h ^= data[i];
      h *= 16777619u;
    }

    return (int)(h & HashMask);
  }
}
