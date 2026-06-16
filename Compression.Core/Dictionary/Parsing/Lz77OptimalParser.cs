using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Parsing;

/// <summary>
/// A reusable, codec-agnostic <b>forward cost-based optimal LZ77 parser</b>.
/// </summary>
/// <remarks>
/// <para>
/// Given the input bytes, a match provider and a pluggable <see cref="ILzCostModel"/>, the
/// parser computes the minimum-total-cost sequence of literal/match tokens via a forward
/// shortest-path dynamic program over byte positions (the classic LZMA-style
/// <c>price[i]</c> pass). Edge <c>i -&gt; i+1</c> is a literal; edge <c>i -&gt; i+len</c> is a
/// match of length <c>len</c>. Because a match of length <c>L</c> at a given distance also
/// permits every shorter length down to the minimum match, the parser relaxes all those
/// sub-length edges, which lets a short cheap match win over a long expensive one when the
/// cost model says so.
/// </para>
/// <para>
/// The result is independent of any bitstream: it returns abstract <see cref="LzParseToken"/>s
/// that a codec converts into bytes. The optimal parse is never larger than a greedy parse
/// under the same cost model.
/// </para>
/// <para>
/// To stay out of <c>O(n^2)</c> pathological territory, the look-ahead is bounded:
/// matches at or above <c>niceLength</c> are taken greedily (the position is skipped past),
/// and per-position sub-length enumeration is capped at the best match length.
/// </para>
/// </remarks>
public sealed class Lz77OptimalParser {

  /// <summary>
  /// Supplies the best match at a position, or a zero-length match if none exists.
  /// Decoupling this from the parser keeps the primitive reusable across match finders.
  /// </summary>
  /// <param name="data">The full input buffer.</param>
  /// <param name="position">The position to find a match for.</param>
  /// <returns>The best match (longest, or best per the finder), or default if none.</returns>
  public delegate Match MatchProvider(ReadOnlySpan<byte> data, int position);

  private readonly ILzCostModel _costModel;
  private readonly int _minMatch;
  private readonly int _maxMatch;
  private readonly int _niceLength;

  /// <summary>
  /// Creates an optimal parser.
  /// </summary>
  /// <param name="costModel">The bit-cost model used to price literals and matches.</param>
  /// <param name="minMatch">Minimum valid match length. Defaults to 3.</param>
  /// <param name="maxMatch">Maximum match length the coder can encode. Defaults to 258.</param>
  /// <param name="niceLength">
  /// Matches of at least this length are accepted greedily to bound the search. Defaults to 128.
  /// </param>
  public Lz77OptimalParser(
    ILzCostModel costModel,
    int minMatch = 3,
    int maxMatch = 258,
    int niceLength = 128) {
    ArgumentNullException.ThrowIfNull(costModel);
    if (minMatch < 1)
      throw new ArgumentOutOfRangeException(nameof(minMatch));
    if (maxMatch < minMatch)
      throw new ArgumentOutOfRangeException(nameof(maxMatch));

    this._costModel = costModel;
    this._minMatch = minMatch;
    this._maxMatch = maxMatch;
    this._niceLength = Math.Max(minMatch, niceLength);
  }

  private struct Node {
    public double Cost;     // minimum cost to reach this position
    public int Length;      // 1 = literal, > 1 = match length arriving here
    public int Distance;    // 0 for literals
  }

  /// <summary>
  /// Computes the minimum-cost LZ parse of <paramref name="data"/>.
  /// </summary>
  /// <param name="data">The input bytes.</param>
  /// <param name="matchProvider">Supplies the best match at each position.</param>
  /// <returns>The ordered token sequence whose concatenation reconstructs the input.</returns>
  public List<LzParseToken> Parse(ReadOnlySpan<byte> data, MatchProvider matchProvider) {
    ArgumentNullException.ThrowIfNull(matchProvider);

    var n = data.Length;
    var result = new List<LzParseToken>();
    if (n == 0)
      return result;

    var dp = new Node[n + 1];
    for (var i = 1; i <= n; ++i)
      dp[i].Cost = double.PositiveInfinity;
    dp[0].Cost = 0.0;

    for (var i = 0; i < n; ++i) {
      var baseCost = dp[i].Cost;
      // The forward pass always reaches position i with a finite cost (literal edges form a
      // spanning path), but guard anyway for safety.
      if (double.IsPositiveInfinity(baseCost))
        continue;

      // Literal edge i -> i+1.
      Relax(dp, i + 1, baseCost + this._costModel.LiteralCost(data[i]), length: 1, distance: 0);

      // Match edges i -> i+len.
      var match = matchProvider(data, i);
      var bestLen = Math.Min(match.Length, Math.Min(this._maxMatch, n - i));
      if (match.Distance <= 0 || bestLen < this._minMatch)
        continue;

      var distance = match.Distance;

      // Greedy shortcut: a long match dominates; take it whole and skip sub-length pricing.
      if (bestLen >= this._niceLength) {
        Relax(dp, i + bestLen, baseCost + this._costModel.MatchCost(bestLen, distance), bestLen, distance);
        continue;
      }

      // Price every sub-length so a cheaper short match can win over a long one.
      for (var len = this._minMatch; len <= bestLen; ++len)
        Relax(dp, i + len, baseCost + this._costModel.MatchCost(len, distance), len, distance);
    }

    // Traceback from the end.
    var pos = n;
    while (pos > 0) {
      ref var node = ref dp[pos];
      if (node.Distance == 0) {
        result.Add(LzParseToken.CreateLiteral(data[pos - 1]));
        pos -= 1;
      } else {
        result.Add(LzParseToken.CreateMatch(node.Distance, node.Length));
        pos -= node.Length;
      }
    }

    result.Reverse();
    return result;
  }

  private static void Relax(Node[] dp, int dest, double cost, int length, int distance) {
    if (cost >= dp[dest].Cost)
      return;

    dp[dest].Cost = cost;
    dp[dest].Length = length;
    dp[dest].Distance = distance;
  }
}
