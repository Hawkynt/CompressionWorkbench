namespace Compression.Core.Deflate;

/// <summary>
/// Chooses where to cut a stream of LZ symbols into DEFLATE blocks so that the total
/// encoded size is smallest.
/// </summary>
/// <remarks>
/// <para>
/// A block carries its own Huffman trees, so a boundary buys the encoder a fresh
/// description of the data on either side and costs it a second header. Where the input
/// changes character — prose then a table of numbers, source then a base64 blob — that
/// trade is strongly worth making, and where it does not, it is not. Zopfli therefore
/// searches for the split points instead of imposing a fixed block size.
/// </para>
/// <para>
/// The published method places one split at a time, greedily, always in the block that
/// currently looks worst. This does a proper dynamic program instead: over a grid of
/// candidate boundaries it finds the cheapest partition into at most
/// <see cref="MaxBlocks"/> blocks outright, which can never do worse than placing the
/// cuts one at a time. It is affordable because the cost of a candidate block is read off
/// running symbol histograms — the histogram of any range is the difference of two
/// prefix histograms — so evaluating a candidate takes time proportional to the alphabet
/// rather than to the block.
/// </para>
/// </remarks>
internal static class BlockSplitter {
  /// <summary>The most blocks the splitter will produce.</summary>
  public const int MaxBlocks = 15;

  /// <summary>The fewest symbols worth considering splitting at all.</summary>
  private const int MinSymbolsToSplit = 512;

  /// <summary>How many candidate boundaries the grid holds at most.</summary>
  private const int MaxCandidates = 128;

  /// <summary>Represents a contiguous range of symbols forming one block.</summary>
  internal readonly record struct BlockRange(int Start, int End);

  /// <summary>
  /// Splits <paramref name="symbols"/> into blocks minimising the total encoded size.
  /// </summary>
  /// <param name="symbols">The LZ symbol sequence to split.</param>
  /// <param name="maxBlocks">Maximum number of blocks.</param>
  /// <returns>A list of contiguous <see cref="BlockRange"/>s covering all symbols.</returns>
  public static List<BlockRange> Split(ReadOnlySpan<LzSymbol> symbols, int maxBlocks = BlockSplitter.MaxBlocks) {
    if (symbols.Length < BlockSplitter.MinSymbolsToSplit || maxBlocks <= 1)
      return [new(0, symbols.Length)];

    // Candidate boundaries on a regular grid. Finer than this buys almost nothing: a
    // boundary a few symbols out of place costs a handful of bits, while the header it
    // saves or spends is hundreds.
    var interval = Math.Max(1, symbols.Length / BlockSplitter.MaxCandidates);
    var candidates = new List<int> { 0 };
    for (var i = interval; i < symbols.Length; i += interval)
      candidates.Add(i);

    if (candidates[^1] != symbols.Length)
      candidates.Add(symbols.Length);

    var count = candidates.Count;

    // Prefix histograms at the candidate boundaries, plus the input bytes consumed, so
    // that any candidate block's statistics are one subtraction away.
    var litLenPrefix = new long[count][];
    var distPrefix = new long[count][];
    var bytePrefix = new long[count];
    litLenPrefix[0] = new long[DeflateConstants.LiteralLengthAlphabetSize];
    distPrefix[0] = new long[DeflateConstants.DistanceAlphabetSize];

    for (var c = 1; c < count; ++c) {
      var litLen = (long[])litLenPrefix[c - 1].Clone();
      var dist = (long[])distPrefix[c - 1].Clone();
      var bytes = bytePrefix[c - 1];

      for (var s = candidates[c - 1]; s < candidates[c]; ++s) {
        var symbol = symbols[s];
        if (symbol.IsLiteral) {
          ++litLen[symbol.LitLen];
          ++bytes;
          continue;
        }

        ++litLen[DeflateConstants.GetLengthCode(symbol.LitLen)];
        ++dist[DeflateConstants.GetDistanceCode(symbol.Distance)];
        bytes += symbol.LitLen;
      }

      litLenPrefix[c] = litLen;
      distPrefix[c] = dist;
      bytePrefix[c] = bytes;
    }

    var cost = new long[count][];
    for (var i = 0; i < count; ++i) {
      cost[i] = new long[count];
      for (var j = i + 1; j < count; ++j)
        cost[i][j] = RangeCost(litLenPrefix, distPrefix, bytePrefix, i, j);
    }

    // best[b][j] is the cheapest way to cover the first j candidate intervals with
    // exactly b blocks; from[b][j] remembers where that partition's last block began.
    var best = new long[maxBlocks + 1][];
    var from = new int[maxBlocks + 1][];
    for (var b = 0; b <= maxBlocks; ++b) {
      best[b] = new long[count];
      from[b] = new int[count];
      best[b].AsSpan().Fill(long.MaxValue);
    }

    for (var j = 1; j < count; ++j) {
      best[1][j] = cost[0][j];
      from[1][j] = 0;
    }

    for (var b = 2; b <= maxBlocks; ++b)
      for (var j = b; j < count; ++j)
        for (var i = b - 1; i < j; ++i) {
          if (best[b - 1][i] == long.MaxValue)
            continue;

          var total = best[b - 1][i] + cost[i][j];
          if (total >= best[b][j])
            continue;

          best[b][j] = total;
          from[b][j] = i;
        }

    var bestBlocks = 1;
    for (var b = 2; b <= maxBlocks; ++b)
      if (best[b][count - 1] < best[bestBlocks][count - 1])
        bestBlocks = b;

    var boundaries = new List<int>();
    var node = count - 1;
    for (var b = bestBlocks; b >= 1; --b) {
      boundaries.Add(node);
      node = from[b][node];
    }

    boundaries.Add(0);
    boundaries.Reverse();

    var result = new List<BlockRange>();
    for (var i = 0; i + 1 < boundaries.Count; ++i)
      result.Add(new(candidates[boundaries[i]], candidates[boundaries[i + 1]]));

    return result;
  }

  private static long RangeCost(
    long[][] litLenPrefix, long[][] distPrefix, long[] bytePrefix, int from, int to) {
    var litLen = new long[DeflateConstants.LiteralLengthAlphabetSize];
    for (var s = 0; s < litLen.Length; ++s)
      litLen[s] = litLenPrefix[to][s] - litLenPrefix[from][s];

    var dist = new long[DeflateConstants.DistanceAlphabetSize];
    for (var s = 0; s < dist.Length; ++s)
      dist[s] = distPrefix[to][s] - distPrefix[from][s];

    litLen[DeflateConstants.EndOfBlock] = 1;

    return ZopfliBlockCost.Cheapest(litLen, dist, bytePrefix[to] - bytePrefix[from]).Bits;
  }
}
