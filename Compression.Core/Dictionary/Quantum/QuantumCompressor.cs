using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Compresses data with the Quantum algorithm (used in Microsoft CAB files).
/// </summary>
/// <remarks>
/// Uses LZ77 match finding with a 16-bit byte-aligned range coder and
/// adaptive frequency models. Mirrors <see cref="QuantumDecompressor"/>.
/// </remarks>
public static class QuantumCompressor {
  /// <summary>
  /// Compresses a single block of data using the Quantum algorithm.
  /// </summary>
  /// <param name="data">The uncompressed input data.</param>
  /// <param name="windowLevel">Window level (1–7). The window size is 1024 &lt;&lt; (level − 1).</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, int windowLevel) {
    ArgumentOutOfRangeException.ThrowIfLessThan(windowLevel, QuantumConstants.MinWindowLevel, nameof(windowLevel));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(windowLevel, QuantumConstants.MaxWindowLevel, nameof(windowLevel));

    if (data.Length == 0)
      return [];

    var windowSize = QuantumConstants.WindowSize(windowLevel);
    var offsetBits = 0;
    for (var ws = windowSize; ws > 1; ws >>= 1)
      ++offsetBits;

    using var output = new MemoryStream();
    var encoder = new QuantumRangeEncoder(output);

    // Create adaptive models with a low rescale threshold to prevent zero-width
    // symbols in the 16-bit range coder (min range after normalize = 256).
    // The decompressor must use the same threshold for data we produce.
    const int threshold = QuantumConstants.CompressorRescaleThreshold;
    var selectorModel = new QuantumModel(QuantumConstants.SelectorSymbols, threshold);
    var literalModel = new QuantumModel(QuantumConstants.LiteralSymbols, threshold);
    var lenModel4 = new QuantumModel(QuantumConstants.MatchLengthSymbols, threshold);
    var lenModel5 = new QuantumModel(QuantumConstants.MatchLengthSymbols, threshold);
    var lenModel6 = new QuantumModel(QuantumConstants.MatchLengthSymbols, threshold);
    var lenModel7 = new QuantumModel(QuantumConstants.MatchLengthSymbols, threshold);
    var lenModelLong = new QuantumModel(QuantumConstants.MatchLengthSymbols, threshold);

    var dataArray = data.ToArray();
    var matchFinder = new HashChainMatchFinder(windowSize);
    var pos = 0;

    while (pos < data.Length) {
      // Try to find a match (minimum length 4)
      Match bestMatch = default;
      if (pos + 4 <= data.Length) {
        var maxDist = Math.Min(pos, windowSize);
        var maxLen = Math.Min(QuantumConstants.MaxMatchLength, data.Length - pos);
        bestMatch = matchFinder.FindMatch(dataArray, pos, maxDist, maxLen, 4);
      }

      if (bestMatch.Length >= 4) {
        var matchLen = bestMatch.Length;
        var offset = bestMatch.Distance;

        // Choose selector based on match length
        var (selector, baseLen) = ChooseSelector(matchLen);
        var extraLen = matchLen - baseLen;

        // Encode selector
        encoder.EncodeSymbol(selectorModel, selector);

        // Encode extra length from the appropriate model
        var lenModel = selector switch {
          1 => lenModel4,
          2 => lenModel5,
          3 => lenModel6,
          4 => lenModel7,
          _ => lenModelLong
        };
        encoder.EncodeSymbol(lenModel, extraLen);

        // Encode offset as raw bits
        encoder.WriteRawBits(offset, offsetBits);

        pos += matchLen;
      } else {
        // Literal
        encoder.EncodeSymbol(selectorModel, 0);
        encoder.EncodeSymbol(literalModel, data[pos]);
        ++pos;
      }
    }

    encoder.Finish();
    return output.ToArray();
  }

  /// <summary>
  /// Chooses the best selector and base length for a given match length.
  /// Prefers the selector with the largest base (smallest extra).
  /// </summary>
  private static (int Selector, int BaseLen) ChooseSelector(int matchLen) => matchLen switch {
    4 => (1, 4),
    5 => (2, 5),
    6 => (3, 6),
    >= 7 and <= 11 => (4, 7),
    >= 12 and <= 23 => (5, 12),
    _ => (6, 24) // 24-50
  };
}
