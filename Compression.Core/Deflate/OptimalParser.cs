namespace Compression.Core.Deflate;

/// <summary>
/// Finds the cheapest LZ parse of a range of input under a given cost model, by shortest
/// path over the positions of that range.
/// </summary>
/// <remarks>
/// <para>
/// This is the heart of the published Zopfli method. Every position is a node; a literal is
/// an edge one byte long and a match of length <c>l</c> is an edge <c>l</c> bytes long, each
/// weighted by what the current cost model says the corresponding symbols cost. Because
/// every edge moves strictly forward, one sweep in increasing position order relaxes the
/// graph in topological order and the result is the true minimum, not the greedy or lazy
/// approximation an ordinary DEFLATE encoder settles for.
/// </para>
/// <para>
/// Weights are integers in units of 1/65536 bit, so the parse depends on nothing but the
/// input and the counts it was seeded with.
/// </para>
/// </remarks>
internal static class OptimalParser {
  /// <summary>
  /// Parses <c>data[start..end)</c> into the cheapest sequence of symbols under
  /// <paramref name="model"/>.
  /// </summary>
  /// <param name="data">The whole input; matches may reach back before <paramref name="start"/>.</param>
  /// <param name="start">First position of the range to parse.</param>
  /// <param name="end">One past the last position of the range to parse.</param>
  /// <param name="cache">The match runs of the whole input.</param>
  /// <param name="model">The cost model to price edges with.</param>
  /// <returns>The parsed symbols, in input order.</returns>
  public static LzSymbol[] Parse(
    ReadOnlySpan<byte> data,
    int start,
    int end,
    ZopfliMatchCache cache,
    ZopfliCostModel model) {
    var span = end - start;
    if (span <= 0)
      return [];

    var cost = new long[span + 1];
    var length = new ushort[span + 1];
    var distance = new ushort[span + 1];
    cost.AsSpan(1).Fill(long.MaxValue);

    for (var i = 0; i < span; ++i) {
      var here = cost[i];
      if (here == long.MaxValue)
        continue;

      var position = start + i;

      var literalCost = here + model.LiteralCost(data[position]);
      if (literalCost < cost[i + 1]) {
        cost[i + 1] = literalCost;
        length[i + 1] = 1;
        distance[i + 1] = 0;
      }

      var runEnd = cache.RunEnd(position);
      var matchLength = ZopfliMatchCache.MinMatch;
      for (var run = cache.RunStart(position); run < runEnd; ++run) {
        var runDistance = cache.DistanceOf(run);
        var runMax = cache.MaxLengthOf(run);
        var distanceCost = here + model.DistanceCost(runDistance);

        // A match may not reach past the end of the range being parsed: the next block
        // starts there and would decode the overlap twice.
        while (matchLength <= runMax && i + matchLength <= span) {
          var candidate = distanceCost + model.LengthCost(matchLength);
          if (candidate < cost[i + matchLength]) {
            cost[i + matchLength] = candidate;
            length[i + matchLength] = (ushort)matchLength;
            distance[i + matchLength] = (ushort)runDistance;
          }

          ++matchLength;
        }

        if (i + matchLength > span)
          break;
      }
    }

    var symbols = new List<LzSymbol>();
    var pos = span;
    while (pos > 0) {
      if (distance[pos] == 0) {
        symbols.Add(LzSymbol.Literal(data[start + pos - 1]));
        --pos;
        continue;
      }

      symbols.Add(LzSymbol.Match(length[pos], distance[pos]));
      pos -= length[pos];
    }

    symbols.Reverse();
    return [.. symbols];
  }

  /// <summary>
  /// Parses <c>data[start..end)</c> greedily, taking the longest match at each position
  /// unless the next position offers a longer one.
  /// </summary>
  /// <param name="data">The whole input; matches may reach back before <paramref name="start"/>.</param>
  /// <param name="start">First position of the range to parse.</param>
  /// <param name="end">One past the last position of the range to parse.</param>
  /// <param name="cache">The match runs of the whole input.</param>
  /// <returns>The parsed symbols, in input order.</returns>
  /// <remarks>
  /// This is the ordinary lazy-matching parse of a plain DEFLATE encoder. Zopfli runs it
  /// once before the first shortest-path pass, purely to have realistic symbol counts to
  /// seed the cost model with: starting from the RFC 1951 fixed tables instead would spend
  /// the first pass, and often several after it, discovering what the input looks like.
  /// </remarks>
  public static LzSymbol[] ParseGreedy(ReadOnlySpan<byte> data, int start, int end, ZopfliMatchCache cache) {
    var symbols = new List<LzSymbol>();
    var position = start;

    while (position < end) {
      var (length, distance) = cache.LongestMatch(position);
      if (length > end - position)
        length = end - position;

      if (length >= ZopfliMatchCache.MinMatch && position + 1 < end) {
        var (nextLength, _) = cache.LongestMatch(position + 1);
        if (nextLength > end - position - 1)
          nextLength = end - position - 1;

        // A longer match one byte later is worth the literal it costs to wait for.
        if (nextLength > length) {
          symbols.Add(LzSymbol.Literal(data[position]));
          ++position;
          continue;
        }
      }

      if (length < ZopfliMatchCache.MinMatch) {
        symbols.Add(LzSymbol.Literal(data[position]));
        ++position;
        continue;
      }

      symbols.Add(LzSymbol.Match(length, distance));
      position += length;
    }

    return [.. symbols];
  }
}
