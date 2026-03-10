namespace Compression.Core.Deflate;

/// <summary>
/// Forward-DP shortest-path parser that produces a minimum-cost LZ parse
/// given Huffman code lengths.
/// </summary>
internal static class OptimalParser {
  private struct DpNode {
    public double Cost;
    public ushort Length;   // 0 = unreachable, 1 = literal, >1 = match length
    public ushort Distance; // 0 for literals
  }

  /// <summary>
  /// Parses <paramref name="data"/> into an optimal sequence of <see cref="LzSymbol"/>s
  /// using the supplied Huffman code lengths to compute edge costs.
  /// </summary>
  public static LzSymbol[] Parse(
    ReadOnlySpan<byte> data,
    ZopfliHashChain hashChain,
    ReadOnlySpan<int> litLenLengths,
    ReadOnlySpan<int> distLengths) {
    if (data.Length == 0)
      return [];

    int length = data.Length;
    var dp = new DpNode[length + 1];
    dp[0].Cost = 0;
    dp.AsSpan(1).Fill(new DpNode { Cost = double.MaxValue });

    const double UnseenPenalty = 15.0;

    for (int i = 0; i < length; ++i) {
      if (dp[i].Cost >= double.MaxValue)
        continue;

      // Literal edge: i → i+1
      double litCost = GetLitLenCost(data[i], litLenLengths, UnseenPenalty);
      double newCost = dp[i].Cost + litCost;
      if (newCost < dp[i + 1].Cost) {
        dp[i + 1].Cost = newCost;
        dp[i + 1].Length = 1;
        dp[i + 1].Distance = 0;
      }

      // Match edges from hash chain
      var matches = hashChain.FindAllMatches(data, i, DeflateConstants.WindowSize, 258);
      foreach (var match in matches) {
        int len = match.Length;
        int dist = match.Distance;
        int dest = i + len;
        if (dest > length)
          continue;

        double matchCost = GetMatchCost(len, dist, litLenLengths, distLengths, UnseenPenalty);
        newCost = dp[i].Cost + matchCost;
        if (newCost < dp[dest].Cost) {
          dp[dest].Cost = newCost;
          dp[dest].Length = (ushort)len;
          dp[dest].Distance = (ushort)dist;
        }
      }
    }

    // Traceback
    var symbols = new List<LzSymbol>();
    int pos = length;
    while (pos > 0) {
      ref var node = ref dp[pos];
      if (node.Distance == 0) {
        // Literal
        symbols.Add(LzSymbol.Literal(data[pos - 1]));
        pos -= 1;
      }
      else {
        // Match
        symbols.Add(LzSymbol.Match(node.Length, node.Distance));
        pos -= node.Length;
      }
    }

    symbols.Reverse();
    return [.. symbols];
  }

  private static double GetLitLenCost(int symbol, ReadOnlySpan<int> litLenLengths, double unseenPenalty) {
    if (symbol < litLenLengths.Length && litLenLengths[symbol] > 0)
      return litLenLengths[symbol];
    return unseenPenalty;
  }

  private static double GetMatchCost(
    int length, int distance,
    ReadOnlySpan<int> litLenLengths,
    ReadOnlySpan<int> distLengths,
    double unseenPenalty) {
    int lenCode = DeflateConstants.GetLengthCode(length);
    int lenIdx = lenCode - 257;

    double cost = 0;

    // Length code bits
    if (lenCode < litLenLengths.Length && litLenLengths[lenCode] > 0)
      cost += litLenLengths[lenCode];
    else
      cost += unseenPenalty;

    // Length extra bits
    cost += DeflateConstants.LengthExtraBits[lenIdx];

    // Distance code bits
    int distCode = DeflateConstants.GetDistanceCode(distance);
    if (distCode < distLengths.Length && distLengths[distCode] > 0)
      cost += distLengths[distCode];
    else
      cost += unseenPenalty;

    // Distance extra bits
    cost += DeflateConstants.DistanceExtraBits[distCode];

    return cost;
  }
}
