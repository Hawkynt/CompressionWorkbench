using System.Numerics;
using System.Runtime.Intrinsics;

namespace Compression.Core.Transforms;

/// <summary>
/// Move-to-Front transform for data preprocessing.
/// Converts symbols to their indices in a dynamically reordered alphabet,
/// producing small values for recently-seen symbols.
/// </summary>
public static class MoveToFrontTransform {
  /// <summary>
  /// Encodes data using the Move-to-Front transform.
  /// </summary>
  /// <param name="data">The input data.</param>
  /// <returns>The MTF-encoded data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    byte[] alphabet = CreateAlphabet();
    byte[] result = new byte[data.Length];

    for (int i = 0; i < data.Length; ++i) {
      byte symbol = data[i];

      // Find position of symbol in alphabet using SIMD
      int idx = FindIndex(alphabet, symbol);

      result[i] = (byte)idx;

      // Move to front: shift [0..idx-1] right by one, place found byte at front
      if (idx > 0) {
        alphabet.AsSpan(0, idx).CopyTo(alphabet.AsSpan(1));
        alphabet[0] = symbol;
      }
    }

    return result;
  }

  /// <summary>
  /// Decodes MTF-encoded data back to the original.
  /// </summary>
  /// <param name="data">The MTF-encoded data.</param>
  /// <returns>The decoded data.</returns>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    byte[] alphabet = CreateAlphabet();
    byte[] result = new byte[data.Length];

    for (int i = 0; i < data.Length; ++i) {
      int idx = data[i];
      byte symbol = alphabet[idx];
      result[i] = symbol;

      // Move to front: shift [0..idx-1] right by one, place decoded byte at front
      if (idx > 0) {
        alphabet.AsSpan(0, idx).CopyTo(alphabet.AsSpan(1));
        alphabet[0] = symbol;
      }
    }

    return result;
  }

  private static byte[] CreateAlphabet() {
    var alphabet = new byte[256];
    if (Vector256.IsHardwareAccelerated) {
      var indices = Vector256.Create(
        (byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31);
      var increment = Vector256.Create((byte)32);
      for (int i = 0; i < 256; i += 32) {
        indices.CopyTo(alphabet.AsSpan(i));
        indices += increment;
      }
    } else {
      for (int i = 0; i < 256; ++i)
        alphabet[i] = (byte)i;
    }
    return alphabet;
  }

  private static int FindIndex(byte[] alphabet, byte symbol) {
    if (Vector256.IsHardwareAccelerated) {
      var target = Vector256.Create(symbol);
      for (int idx = 0; idx < 256; idx += 32) {
        var chunk = Vector256.Create<byte>(alphabet.AsSpan(idx));
        var eq = Vector256.Equals(chunk, target);
        if (eq != Vector256<byte>.Zero)
          return idx + BitOperations.TrailingZeroCount(eq.ExtractMostSignificantBits());
      }
    } else
      for (int idx = 0; idx < 256; ++idx)
        if (alphabet[idx] == symbol)
          return idx;

    return 0; // unreachable for valid alphabets
  }
}
