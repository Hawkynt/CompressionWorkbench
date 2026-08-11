namespace Compression.Core.Deflate;

/// <summary>
/// Drives the Zopfli search: split the input into blocks, then re-parse each block over
/// and over until re-parsing stops paying, keeping the best parse seen.
/// </summary>
/// <remarks>
/// <para>
/// Zopfli (Vandevenne and Alakuijala, 2013) exists to spend a great deal of time producing
/// an ordinary RFC 1951 stream that happens to be smaller. Its one idea is that the parse
/// and the Huffman trees are circular: what a match costs depends on the trees, and the
/// trees depend on which matches the parse chose. Neither can be settled first, so it
/// guesses, solves the other exactly, and repeats:
/// </para>
/// <list type="number">
///   <item><description>parse the block the ordinary greedy way, purely to have realistic
///     symbol counts;</description></item>
///   <item><description>price every symbol by the entropy of those counts;</description></item>
///   <item><description>find the cheapest parse under that pricing by shortest path;</description></item>
///   <item><description>take the counts of that parse and go back to step 2.</description></item>
/// </list>
/// <para>
/// The loop is not a contraction and need not improve every round, which is why the size
/// of each round's parse is measured exactly and the smallest is what gets emitted. When
/// two consecutive rounds land on the same size the search has settled, and it is nudged
/// off that fixed point by perturbing the counts, so that the remaining rounds explore
/// instead of recomputing an answer already in hand.
/// </para>
/// </remarks>
internal static class ZopfliDeflate {
  /// <summary>One block of the plan: a range of input and the symbols it parses into.</summary>
  /// <param name="Start">First input byte of the block.</param>
  /// <param name="End">One past the last input byte of the block.</param>
  /// <param name="Symbols">The symbols the block encodes to.</param>
  internal readonly record struct PlannedBlock(int Start, int End, LzSymbol[] Symbols);

  /// <summary>
  /// How many rounds of re-parsing a block gets. Each round costs about as much as one
  /// pass of the shortest-path search over the block, so the budget shrinks as the input
  /// grows; the returns diminish sharply after the first few rounds in any case.
  /// </summary>
  private static int IterationsFor(int totalLength) => totalLength switch {
    <= 16384 => 60,
    <= 131072 => 40,
    <= 524288 => 30,
    _ => 25
  };

  /// <summary>
  /// Plans how to encode <paramref name="data"/>: where the blocks go and what symbols
  /// each holds.
  /// </summary>
  /// <param name="data">The input to compress.</param>
  /// <returns>The planned blocks, in order, covering the whole input.</returns>
  public static List<PlannedBlock> CompressOptimal(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [new(0, 0, [])];

    var cache = ZopfliMatchCache.Build(data);
    var seed = OptimalParser.ParseGreedy(data, 0, data.Length, cache);

    // Split on the seed parse. The split points are input positions, so each block can
    // then be parsed on its own terms, with its own cost model - which is the whole point
    // of splitting. Matches inside a block may still reach back into earlier blocks.
    var ranges = BlockSplitter.Split(seed);
    var byteStart = new int[ranges.Count + 1];
    var consumed = 0;
    var symbolIndex = 0;
    for (var r = 0; r < ranges.Count; ++r) {
      byteStart[r] = consumed;
      for (; symbolIndex < ranges[r].End; ++symbolIndex)
        consumed += seed[symbolIndex].IsLiteral ? 1 : seed[symbolIndex].LitLen;
    }

    byteStart[ranges.Count] = data.Length;

    var iterations = IterationsFor(data.Length);
    var result = new List<PlannedBlock>();
    for (var r = 0; r < ranges.Count; ++r) {
      var start = byteStart[r];
      var end = byteStart[r + 1];
      result.Add(new(start, end, Optimize(data, start, end, cache, iterations)));
    }

    return result;
  }

  private static LzSymbol[] Optimize(
    ReadOnlySpan<byte> data, int start, int end, ZopfliMatchCache cache, int iterations) {
    var byteCount = end - start;

    var current = OptimalParser.ParseGreedy(data, start, end, cache);
    var (litLenCounts, distCounts) = Count(current);

    var best = current;
    var bestBits = BlockBits(litLenCounts, distCounts, byteCount);
    var bestLitLen = litLenCounts;
    var bestDist = distCounts;

    var modelLitLen = litLenCounts;
    var modelDist = distCounts;

    long[]? lastLitLen = null;
    long[]? lastDist = null;
    var lastBits = -1L;
    var perturbed = false;
    var random = 0x5A17E1F1u;

    for (var iteration = 0; iteration < iterations; ++iteration) {
      var model = ZopfliCostModel.FromCounts(modelLitLen, modelDist);
      var parsed = OptimalParser.Parse(data, start, end, cache, model);
      var (parsedLitLen, parsedDist) = Count(parsed);
      var bits = BlockBits(parsedLitLen, parsedDist, byteCount);

      if (bits < bestBits) {
        best = parsed;
        bestBits = bits;
        bestLitLen = parsedLitLen;
        bestDist = parsedDist;
      }

      var nextLitLen = parsedLitLen;
      var nextDist = parsedDist;

      // Two rounds of the same size means the loop has reached a fixed point. Restarting
      // from the best counts seen, perturbed, is what turns the remaining rounds into a
      // wider search rather than a repetition.
      if (iteration >= 5 && bits == lastBits) {
        nextLitLen = (long[])bestLitLen.Clone();
        nextDist = (long[])bestDist.Clone();
        Perturb(nextLitLen, ref random);
        Perturb(nextDist, ref random);
        perturbed = true;
      }

      // Once the search is exploring, blending in the previous round's counts damps the
      // swing between rounds; converging slowly on a better answer beats oscillating.
      if (perturbed && lastLitLen is not null && lastDist is not null) {
        nextLitLen = Blend(nextLitLen, lastLitLen);
        nextDist = Blend(nextDist, lastDist);
      }

      lastLitLen = modelLitLen;
      lastDist = modelDist;
      lastBits = bits;
      modelLitLen = nextLitLen;
      modelDist = nextDist;
    }

    return best;
  }

  /// <summary>Counts the symbols of a parse, with the end-of-block symbol included.</summary>
  private static (long[] LitLen, long[] Dist) Count(LzSymbol[] symbols) {
    var litLen = new long[DeflateConstants.LiteralLengthAlphabetSize];
    var dist = new long[DeflateConstants.DistanceAlphabetSize];

    foreach (var symbol in symbols)
      if (symbol.IsLiteral)
        ++litLen[symbol.LitLen];
      else {
        ++litLen[DeflateConstants.GetLengthCode(symbol.LitLen)];
        ++dist[DeflateConstants.GetDistanceCode(symbol.Distance)];
      }

    litLen[DeflateConstants.EndOfBlock] = 1;
    return (litLen, dist);
  }

  /// <summary>Exact size of the block these counts describe, in its cheapest block type.</summary>
  private static long BlockBits(long[] litLenCounts, long[] distCounts, int byteCount)
    => ZopfliBlockCost.Cheapest(litLenCounts, distCounts, byteCount).Bits;

  /// <summary>
  /// Replaces about a third of the counts with another count drawn from the same table.
  /// </summary>
  /// <remarks>
  /// The point is to move the cost model somewhere the loop has not been, cheaply, without
  /// losing the shape of the distribution: every value written is a value the table already
  /// held. The generator is the linear congruential one of Knuth's <i>The Art of Computer
  /// Programming</i> volume 2, taken modulo 2^32; only its high bits are consulted, since
  /// the low bits of such a generator cycle far too quickly to be useful.
  /// </remarks>
  private static void Perturb(long[] counts, ref uint state) {
    for (var i = 0; i < counts.Length; ++i) {
      state = unchecked(state * 1664525u + 1013904223u);
      if (state / 256 % 3 != 0)
        continue;

      state = unchecked(state * 1664525u + 1013904223u);
      counts[i] = counts[state % (uint)counts.Length];
    }
  }

  /// <summary>
  /// Weights the current counts at one and the previous round's at one half.
  /// </summary>
  /// <remarks>
  /// Halving the older term keeps the blend bounded no matter how many rounds run, which
  /// integer counts need and floating-point ones can ignore.
  /// </remarks>
  private static long[] Blend(long[] current, long[] previous) {
    var result = new long[current.Length];
    for (var i = 0; i < current.Length; ++i)
      result[i] = current[i] + previous[i] / 2;

    return result;
  }
}
