using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Lz78;

/// <summary>
/// Decompresses data that was compressed using the LZ78 algorithm.
/// </summary>
public static class Lz78Decompressor {
  /// <summary>
  /// Decompresses a sequence of LZ78 tokens back into the original data.
  /// </summary>
  /// <param name="tokens">The LZ78 tokens produced by <see cref="Lz78Compressor"/>.</param>
  /// <param name="maxBits">
  /// Maximum number of bits for dictionary indices. Must match the value used
  /// during compression. Default is 12.
  /// </param>
  /// <returns>The decompressed byte array.</returns>
  /// <exception cref="InvalidDataException">
  /// Thrown when a token references an invalid dictionary index.
  /// </exception>
  public static byte[] Decompress(IReadOnlyList<Lz78Token> tokens, int maxBits = 12) {
    int maxEntries = 1 << maxBits;

    // Dictionary entry 0 = empty byte array (root).
    var dictionary = new List<byte[]> { Array.Empty<byte>() };
    using var output = new MemoryStream();

    foreach (var token in tokens) {
      if (token.DictionaryIndex < 0 || token.DictionaryIndex >= dictionary.Count)
        ThrowInvalidDictionaryIndex(token.DictionaryIndex, dictionary.Count);

      byte[] prefix = dictionary[token.DictionaryIndex];

      if (token.NextByte is byte nextByte) {
        // Normal token: prefix + next byte.
        var entry = new byte[prefix.Length + 1];
        prefix.CopyTo(entry, 0);
        entry[^1] = nextByte;

        output.Write(entry);
        dictionary.Add(entry);

        // Reset dictionary when it reaches maximum size.
        if (dictionary.Count >= maxEntries) {
          dictionary.Clear();
          dictionary.Add(Array.Empty<byte>());
        }
      }
      else {
        // Terminal token: emit prefix only, no new dictionary entry.
        output.Write(prefix);
      }
    }

    return output.ToArray();
  }

  [DoesNotReturn, StackTraceHidden, MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidDictionaryIndex(int index, int dictSize) =>
    throw new InvalidDataException(
      $"Invalid dictionary index {index} (dictionary size: {dictSize}).");
}
