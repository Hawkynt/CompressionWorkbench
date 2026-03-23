namespace Compression.Analysis.Fingerprinting;

/// <summary>
/// Detects Huffman-coded data by looking for a code length table structure at the
/// start of the stream. Many Huffman implementations prefix the encoded data with
/// the code lengths for each symbol.
/// </summary>
public sealed class HuffmanHeuristic : IHeuristic {

  /// <inheritdoc />
  public FingerprintResult? Analyze(ReadOnlySpan<byte> data) {
    if (data.Length < 8) return null;

    // Common Huffman table format: number of symbols (1-2 bytes) followed by code lengths (1 byte each)
    // Code lengths are typically 1-15 (or 1-20 for some implementations)

    // Try: first byte/word = symbol count, followed by code lengths
    var symbolCount = data[0];
    if (symbolCount == 0) return null;

    // Check if subsequent bytes look like code lengths (mostly in range 1-15)
    var validLengths = 0;
    var tableEnd = Math.Min(symbolCount + 1, data.Length);
    for (var i = 1; i < tableEnd; i++) {
      if (data[i] is >= 1 and <= 20) validLengths++;
    }

    if (tableEnd <= 1) return null;
    var ratio = (double)validLengths / (tableEnd - 1);
    if (ratio < 0.7) return null;

    // Also verify: the code lengths should be concentrated in a small range
    var lengths = new int[21];
    for (var i = 1; i < tableEnd; i++) {
      if (data[i] <= 20) lengths[data[i]]++;
    }
    var usedLengths = 0;
    for (var i = 1; i <= 20; i++) {
      if (lengths[i] > 0) usedLengths++;
    }

    // Huffman trees typically use 4-8 different code lengths
    if (usedLengths < 2 || usedLengths > 15) return null;

    var confidence = Math.Min(0.7, 0.3 + ratio * 0.3 + (usedLengths >= 3 ? 0.1 : 0));
    return new("Huffman", confidence, $"Code length table: {validLengths}/{tableEnd - 1} valid, {usedLengths} distinct lengths");
  }
}
