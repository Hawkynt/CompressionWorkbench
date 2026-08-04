namespace Compression.Core.Dictionary.Lzfse;

/// <summary>
/// Maps non-negative integer values (literal lengths, match lengths, distances) to a
/// small FSE-friendly symbol alphabet, with an escape symbol for values that do not
/// fit directly. See <see cref="LzfseConstants"/> for the design rationale.
/// </summary>
internal static class ValueBucket {
  /// <summary>Largest value directly representable by its own symbol.</summary>
  public const int DirectMax = 30;

  /// <summary>Symbol value meaning "the actual value is stored in the overflow stream".</summary>
  public const byte OverflowSymbol = 31;

  /// <summary>
  /// Encodes a sequence of values into bucket symbols, appending any value that
  /// does not fit directly to <paramref name="overflow"/> in encounter order.
  /// </summary>
  public static byte[] Encode(IReadOnlyList<int> values, List<int> overflow) {
    var symbols = new byte[values.Count];
    for (var i = 0; i < values.Count; ++i) {
      var value = values[i];
      if (value is >= 0 and <= DirectMax)
        symbols[i] = (byte)value;
      else {
        symbols[i] = OverflowSymbol;
        overflow.Add(value);
      }
    }
    return symbols;
  }

  /// <summary>
  /// Recovers the original values from bucket symbols and the overflow stream
  /// produced by <see cref="Encode"/>.
  /// </summary>
  public static int[] Decode(byte[] symbols, int[] overflow) {
    var result = new int[symbols.Length];
    var overflowIndex = 0;
    for (var i = 0; i < symbols.Length; ++i) {
      if (symbols[i] == OverflowSymbol) {
        if (overflowIndex >= overflow.Length)
          throw new InvalidDataException("LZFSE value stream overflow table exhausted.");
        result[i] = overflow[overflowIndex++];
      } else
        result[i] = symbols[i];
    }
    return result;
  }
}
