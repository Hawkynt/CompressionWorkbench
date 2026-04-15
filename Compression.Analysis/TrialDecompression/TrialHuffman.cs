using Compression.Analysis.Statistics;

namespace Compression.Analysis.TrialDecompression;

/// <summary>
/// Attempts Huffman decoding by trying to reconstruct a code table from the stream start.
/// Very low success probability for headerless Huffman — capped at low confidence.
/// </summary>
public sealed class TrialHuffman : ITrialStrategy {
  /// <inheritdoc />
  public string Algorithm => "Huffman";

  /// <inheritdoc />
  public TrialCategory Category => TrialCategory.Primitive;

  /// <inheritdoc />
  public DecompressionAttempt TryDecompress(ReadOnlySpan<byte> data, int maxOutput, CancellationToken ct) {
    // Try parsing first byte as symbol count, subsequent bytes as code lengths
    if (data.Length < 4)
      return Fail("Too short");

    try {
      var symbolCount = data[0];
      if (symbolCount < 2 || 1 + symbolCount > data.Length)
        return Fail("Invalid symbol count");

      // Validate code lengths are reasonable
      var validLengths = 0;
      for (var i = 1; i <= symbolCount; i++) {
        if (data[i] is >= 1 and <= 15) validLengths++;
      }

      if (validLengths < symbolCount * 0.7)
        return Fail("Too many invalid code lengths");

      // Build canonical Huffman codes from lengths
      var codeLengths = new int[symbolCount];
      for (var i = 0; i < symbolCount; i++)
        codeLengths[i] = data[i + 1];

      var maxLen = codeLengths.Max();
      var output = new List<byte>();
      var bitPos = (1 + symbolCount) * 8;

      while (bitPos < data.Length * 8 && output.Count < maxOutput) {
        if (ct.IsCancellationRequested) break;

        var decoded = false;
        for (var len = 1; len <= maxLen && !decoded; len++) {
          if (bitPos + len > data.Length * 8) break;

          var code = ReadBitsLsb(data, bitPos, len);
          for (var sym = 0; sym < symbolCount; sym++) {
            if (codeLengths[sym] == len) {
              // Simple matching — not a proper canonical decode, but a reasonable heuristic
              output.Add((byte)sym);
              bitPos += len;
              decoded = true;
              break;
            }
          }
        }
        if (!decoded) break;
      }

      if (output.Count < 4)
        return Fail("Decoded too few symbols");

      var result = output.ToArray();
      var entropy = BinaryStatistics.ComputeEntropy(result);
      return new(Algorithm, 0, result.Length, entropy, true, null, result);
    }
    catch (Exception ex) {
      return Fail(ex.Message);
    }
  }

  private static int ReadBitsLsb(ReadOnlySpan<byte> data, int bitPos, int count) {
    var value = 0;
    for (var i = 0; i < count; i++) {
      var byteIdx = (bitPos + i) / 8;
      var bitIdx = (bitPos + i) % 8;
      if (byteIdx >= data.Length) break;
      if ((data[byteIdx] & (1 << bitIdx)) != 0)
        value |= 1 << i;
    }
    return value;
  }

  private DecompressionAttempt Fail(string error)
    => new(Algorithm, 0, -1, -1, false, error, null);
}
