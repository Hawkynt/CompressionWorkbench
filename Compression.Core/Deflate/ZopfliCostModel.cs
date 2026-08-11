namespace Compression.Core.Deflate;

/// <summary>
/// Prices literal, length and distance symbols for the Zopfli shortest-path parse.
/// </summary>
/// <remarks>
/// <para>
/// The published Zopfli method (Vandevenne and Alakuijala, 2013) drives its shortest-path
/// search with the <em>entropy</em> of the symbol counts produced by the previous parse,
/// not with the integer Huffman code lengths those counts would yield. Entropy is the
/// better guide because Huffman lengths are rounded to whole bits: a symbol that carries
/// 1.2 bits of information and one that carries 1.9 both get a one-bit code, so a parse
/// steered by code lengths cannot tell them apart and systematically over-values the
/// commonest symbols. Costs here are therefore
/// <c>log2(total) - log2(count)</c> per symbol, plus the RFC 1951 extra bits of the
/// length and distance codes, which are exact whole bits.
/// </para>
/// <para>
/// A symbol with a count of zero is priced as <c>log2(total)</c>, i.e. as if it occurred
/// once. That matters: such a symbol is not forbidden, it merely did not appear in the
/// previous parse, and a fixed large penalty would wrongly rule it out for good.
/// </para>
/// <para>
/// Everything is computed in integer fixed point, in units of 1/65536 bit, so a parse is
/// a function of the input alone. No floating-point library is consulted and no rounding
/// mode is assumed, which is what lets the C# and JavaScript implementations agree
/// byte for byte.
/// </para>
/// </remarks>
internal sealed class ZopfliCostModel {
  /// <summary>Costs are expressed in units of 1/<see cref="BitScale"/> bit.</summary>
  public const long BitScale = 65536;

  private readonly long[] _litLenCost;
  private readonly long[] _lengthCost;
  private readonly long[] _distanceCost;

  private ZopfliCostModel(long[] litLenCost, long[] distCost) {
    this._litLenCost = litLenCost;

    // The shortest-path search asks for these millions of times, and both are functions of
    // the model alone, so they are worked out once here rather than per edge.
    this._lengthCost = new long[ZopfliMatchCache.MaxMatch + 1];
    for (var length = ZopfliMatchCache.MinMatch; length <= ZopfliMatchCache.MaxMatch; ++length) {
      var code = DeflateConstants.GetLengthCode(length);
      this._lengthCost[length] = litLenCost[code] + DeflateConstants.LengthExtraBits[code - 257] * ZopfliCostModel.BitScale;
    }

    this._distanceCost = new long[DeflateConstants.DistanceAlphabetSize];
    for (var code = 0; code < this._distanceCost.Length; ++code)
      this._distanceCost[code] = distCost[code] + DeflateConstants.DistanceExtraBits[code] * ZopfliCostModel.BitScale;
  }

  /// <summary>
  /// Builds the cost model implied by the symbol counts of a parse.
  /// </summary>
  /// <param name="litLenCounts">Counts per literal/length symbol.</param>
  /// <param name="distCounts">Counts per distance symbol.</param>
  /// <returns>The cost model.</returns>
  public static ZopfliCostModel FromCounts(ReadOnlySpan<long> litLenCounts, ReadOnlySpan<long> distCounts)
    => new(Entropy(litLenCounts), Entropy(distCounts));

  /// <summary>Cost of coding <paramref name="literal"/> as a literal byte.</summary>
  public long LiteralCost(int literal) => this._litLenCost[literal];

  /// <summary>Cost of the length half of a back-reference, extra bits included.</summary>
  public long LengthCost(int length) => this._lengthCost[length];

  /// <summary>Cost of the distance half of a back-reference, extra bits included.</summary>
  /// <remarks>
  /// The two halves are asked for separately because the parser walks every length that
  /// shares one distance in a row, so the distance's cost is paid for once per run rather
  /// than once per edge.
  /// </remarks>
  public long DistanceCost(int distance) => this._distanceCost[DeflateConstants.GetDistanceCode(distance)];

  private static long[] Entropy(ReadOnlySpan<long> counts) {
    var result = new long[counts.Length];

    var total = 0L;
    foreach (var count in counts)
      total += count;

    // An empty alphabet has no observations to learn from; pricing every symbol at
    // log2(alphabet size) is the uniform distribution, which is the honest prior.
    var log2Total = Log2(total == 0 ? counts.Length : total);

    for (var i = 0; i < counts.Length; ++i) {
      var cost = counts[i] == 0 ? log2Total : log2Total - Log2(counts[i]);
      result[i] = cost < 0 ? 0 : cost;
    }

    return result;
  }

  /// <summary>
  /// Base-2 logarithm of a positive integer, in units of 1/<see cref="BitScale"/>.
  /// </summary>
  /// <param name="value">The value whose logarithm is wanted; values below one give zero.</param>
  /// <returns>The logarithm, scaled by <see cref="BitScale"/>.</returns>
  /// <remarks>
  /// The value is first halved until it lies in [1,2), each halving contributing one whole
  /// bit. Squaring a number in [1,2) either leaves it in [1,2) or moves it into [2,4); which
  /// of the two happens is exactly the next fractional bit of the logarithm, so sixteen
  /// squarings yield sixteen fractional bits. Only integer multiplication and division are
  /// involved, and the largest intermediate is below 2^34, so the result is identical on any
  /// machine with 64-bit integers or IEEE-754 doubles.
  /// </remarks>
  internal static long Log2(long value) {
    if (value <= 1)
      return 0;

    var scaled = value * ZopfliCostModel.BitScale;
    var result = 0L;
    while (scaled >= 2 * ZopfliCostModel.BitScale) {
      scaled /= 2;
      result += ZopfliCostModel.BitScale;
    }

    var bit = ZopfliCostModel.BitScale / 2;
    for (var i = 0; i < 16; ++i) {
      scaled = scaled * scaled / ZopfliCostModel.BitScale;
      if (scaled >= 2 * ZopfliCostModel.BitScale) {
        scaled /= 2;
        result += bit;
      }

      bit /= 2;
    }

    return result;
  }
}
