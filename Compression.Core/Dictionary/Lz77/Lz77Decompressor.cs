using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Lz77;

/// <summary>
/// Reconstructs data from a sequence of LZ77 tokens.
/// </summary>
public static class Lz77Decompressor {
  /// <summary>
  /// Decompresses a sequence of LZ77 tokens back into the original data.
  /// </summary>
  /// <param name="tokens">The LZ77 token sequence.</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="InvalidDataException">A token references data before the start of the output.</exception>
  public static byte[] Decompress(IReadOnlyList<Lz77Token> tokens) {
    var output = new List<byte>();

    foreach (var token in tokens)
      if (token.IsLiteral)
        output.Add(token.Literal);
      else {
        var start = output.Count - token.Distance;
        if (start < 0)
          ThrowInvalidBackReference(token.Distance, output.Count);

        // Handle overlapping copies correctly
        for (var i = 0; i < token.Length; ++i)
          output.Add(output[start + i]);
      }

    return [.. output];
  }

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidBackReference(int distance, int outputSize) =>
    throw new InvalidDataException(
      $"Invalid LZ77 back-reference: distance {distance} exceeds output size {outputSize}.");
}
