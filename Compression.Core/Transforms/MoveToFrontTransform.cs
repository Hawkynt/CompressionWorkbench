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

    byte[] alphabet = new byte[256];
    for (int i = 0; i < 256; ++i)
      alphabet[i] = (byte)i;

    byte[] result = new byte[data.Length];

    for (int i = 0; i < data.Length; ++i) {
      byte b = data[i];

      // Find position of b in alphabet
      int idx = 0;
      while (alphabet[idx] != b)
        idx++;

      result[i] = (byte)idx;

      // Move to front: shift [0..idx-1] right by one, place found byte at front
      if (idx > 0) {
        byte val = alphabet[idx];
        alphabet.AsSpan(0, idx).CopyTo(alphabet.AsSpan(1));
        alphabet[0] = val;
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

    byte[] alphabet = new byte[256];
    for (int i = 0; i < 256; ++i)
      alphabet[i] = (byte)i;

    byte[] result = new byte[data.Length];

    for (int i = 0; i < data.Length; ++i) {
      int idx = data[i];
      byte b = alphabet[idx];
      result[i] = b;

      // Move to front: shift [0..idx-1] right by one, place decoded byte at front
      if (idx > 0) {
        alphabet.AsSpan(0, idx).CopyTo(alphabet.AsSpan(1));
        alphabet[0] = b;
      }
    }

    return result;
  }
}
