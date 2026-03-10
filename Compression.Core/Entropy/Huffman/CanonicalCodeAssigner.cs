namespace Compression.Core.Entropy.Huffman;

/// <summary>
/// Computes canonical Huffman next-code values from code lengths (Steps 1 and 2 of RFC 1951 §3.2.2).
/// </summary>
internal static class CanonicalCodeAssigner {
  /// <summary>
  /// Computes the first code value for each code length using canonical assignment.
  /// </summary>
  /// <param name="codeLengths">Array where index = symbol, value = code length (0 = unused).</param>
  /// <param name="maxLen">The maximum code length.</param>
  /// <param name="nextCode">Output span of size <paramref name="maxLen"/> + 1 receiving the first code for each length.</param>
  internal static void ComputeNextCodes(ReadOnlySpan<int> codeLengths, int maxLen, Span<uint> nextCode) {
    // Step 1: Count codes per length
    Span<int> blCount = stackalloc int[maxLen + 1];
    for (int i = 0; i < codeLengths.Length; ++i)
      if (codeLengths[i] > 0)
        ++blCount[codeLengths[i]];

    // Step 2: Compute first code for each length
    uint code = 0;
    for (int bits = 1; bits <= maxLen; ++bits) {
      code = (code + (uint)blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }
  }
}
