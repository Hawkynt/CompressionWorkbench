using Compression.Core.Entropy.Huffman;

namespace Compression.Core.Deflate;

/// <summary>
/// DP-based block splitter that partitions an LZ symbol sequence into blocks
/// that minimize total estimated encoded size.
/// </summary>
internal static class BlockSplitter {
  /// <summary>Represents a contiguous range of symbols forming one block.</summary>
  internal readonly record struct BlockRange(int Start, int End);

  /// <summary>
  /// Splits <paramref name="symbols"/> into blocks to minimize total estimated bit cost.
  /// </summary>
  /// <param name="symbols">The LZ symbol sequence to split.</param>
  /// <param name="maxBlocks">Maximum number of blocks.</param>
  /// <returns>A list of contiguous <see cref="BlockRange"/>s covering all symbols.</returns>
  public static List<BlockRange> Split(ReadOnlySpan<LzSymbol> symbols, int maxBlocks = 15) {
    if (symbols.Length < 1024 || maxBlocks <= 1)
      return [new BlockRange(0, symbols.Length)];

    // Generate candidate split points at regular intervals
    var interval = Math.Max(symbols.Length / (maxBlocks * 3), 128);
    var candidates = new List<int> { 0 };
    for (int i = interval; i < symbols.Length; i += interval)
      candidates.Add(i);

    candidates.Add(symbols.Length);

    var numCandidates = candidates.Count;

    // Precompute cost of each candidate range [i..j)
    // cost[i][j] = estimated bits for symbols[candidates[i]..candidates[j])
    var cost = new double[numCandidates][];
    for (int i = 0; i < numCandidates; ++i) {
      cost[i] = new double[numCandidates];
      for (int j = i + 1; j < numCandidates; ++j)
        cost[i][j] = EstimateBlockBits(symbols[candidates[i]..candidates[j]]);
    }

    // DP: dp[j] = min total bits to encode symbols[0..candidates[j])
    var dp = new double[numCandidates];
    var prev = new int[numCandidates];
    Array.Fill(dp, double.MaxValue);
    dp[0] = 0;

    for (int j = 1; j < numCandidates; ++j) {
      for (int i = 0; i < j; ++i) {
        if (dp[i] >= double.MaxValue)
          continue;

        double totalCost = dp[i] + cost[i][j];
        if (totalCost < dp[j]) {
          dp[j] = totalCost;
          prev[j] = i;
        }
      }
    }

    // Traceback
    var splitPoints = new List<int>();
    int idx = numCandidates - 1;
    while (idx > 0) {
      splitPoints.Add(idx);
      idx = prev[idx];
    }
    splitPoints.Add(0);
    splitPoints.Reverse();

    // Limit to maxBlocks
    while (splitPoints.Count - 1 > maxBlocks) {
      // Merge the two adjacent blocks with smallest combined cost increase
      double bestMergeCost = double.MaxValue;
      var bestMergeIdx = 1;

      for (int i = 1; i < splitPoints.Count - 1; ++i) {
        int a = splitPoints[i - 1];
        int b = splitPoints[i];
        int c = splitPoints[i + 1];

        double before = cost[a][b] + cost[b][c];
        double after = cost[a][c];
        double delta = after - before;

        if (delta < bestMergeCost) {
          bestMergeCost = delta;
          bestMergeIdx = i;
        }
      }

      splitPoints.RemoveAt(bestMergeIdx);
    }

    // Build ranges
    var result = new List<BlockRange>();
    for (int i = 0; i < splitPoints.Count - 1; ++i)
      result.Add(new BlockRange(candidates[splitPoints[i]], candidates[splitPoints[i + 1]]));

    return result;
  }

  /// <summary>
  /// Estimates the number of bits needed to encode a block of symbols
  /// using dynamic Huffman coding.
  /// </summary>
  private static double EstimateBlockBits(ReadOnlySpan<LzSymbol> symbols) {
    if (symbols.Length == 0)
      return 0;

    var litLenFreqs = new long[DeflateConstants.LiteralLengthAlphabetSize];
    var distFreqs = new long[DeflateConstants.DistanceAlphabetSize];

    foreach (var sym in symbols) {
      if (sym.IsLiteral)
        ++litLenFreqs[sym.LitLen];
      else {
        int lenCode = DeflateConstants.GetLengthCode(sym.LitLen);
        ++litLenFreqs[lenCode];
        int distCode = DeflateConstants.GetDistanceCode(sym.Distance);
        ++distFreqs[distCode];
      }
    }
    litLenFreqs[DeflateConstants.EndOfBlock] = 1;

    // Ensure at least one distance code
    var hasDist = false;
    for (int i = 0; i < distFreqs.Length; ++i)
      if (distFreqs[i] > 0) { 
        hasDist = true; 
        break; 
      }
    
    if (!hasDist) distFreqs[0] = 1;

    // Build Huffman trees to get code lengths
    var litLenRoot = HuffmanTree.BuildFromFrequencies(litLenFreqs);
    int[] litLenLengths = HuffmanTree.GetCodeLengths(litLenRoot, DeflateConstants.LiteralLengthAlphabetSize);
    HuffmanTree.LimitCodeLengths(litLenLengths, DeflateConstants.MaxBits);

    var distRoot = HuffmanTree.BuildFromFrequencies(distFreqs);
    int[] distLengths = HuffmanTree.GetCodeLengths(distRoot, DeflateConstants.DistanceAlphabetSize);
    HuffmanTree.LimitCodeLengths(distLengths, DeflateConstants.MaxBits);

    // Estimate bits: header overhead + token bits
    double bits = 3 + 5 + 5 + 4; // BFINAL + BTYPE + HLIT + HDIST + HCLEN

    // Rough code-length table overhead
    var hlit = litLenLengths.Length;
    while (hlit > 257 && litLenLengths[hlit - 1] == 0)
     --hlit;

    var hdist = distLengths.Length;
    while (hdist > 1 && distLengths[hdist - 1] == 0)
      --hdist;

    bits += (hlit + hdist) * 3.0; // rough estimate for code-length table

    // Token bits
    foreach (var sym in symbols) {
      if (sym.IsLiteral)
        bits += litLenLengths[sym.LitLen];
      else {
        int lenCode = DeflateConstants.GetLengthCode(sym.LitLen);
        bits += litLenLengths[lenCode];
        bits += DeflateConstants.LengthExtraBits[lenCode - 257];

        int distCode = DeflateConstants.GetDistanceCode(sym.Distance);
        bits += distLengths[distCode];
        bits += DeflateConstants.DistanceExtraBits[distCode];
      }
    }

    bits += litLenLengths[DeflateConstants.EndOfBlock]; // EOB
    return bits;
  }
}
