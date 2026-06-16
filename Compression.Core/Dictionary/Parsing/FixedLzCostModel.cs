namespace Compression.Core.Dictionary.Parsing;

/// <summary>
/// A flat cost model for <see cref="Lz77OptimalParser"/>: every literal costs a fixed amount
/// and every match costs a fixed amount, regardless of length or distance. This matches codecs
/// whose token serialization is fixed-width (e.g. 2 bytes per literal, 5 bytes per match), so
/// the optimal parser minimizes the exact serialized size.
/// </summary>
public sealed class FixedLzCostModel : ILzCostModel {
  private readonly double _literalCost;
  private readonly double _matchCost;

  /// <summary>
  /// Creates a flat cost model.
  /// </summary>
  /// <param name="literalBits">Cost of any literal.</param>
  /// <param name="matchBits">Cost of any match.</param>
  public FixedLzCostModel(double literalBits, double matchBits) {
    this._literalCost = literalBits;
    this._matchCost = matchBits;
  }

  /// <inheritdoc/>
  public double LiteralCost(byte value) => this._literalCost;

  /// <inheritdoc/>
  public double MatchCost(int length, int distance) => this._matchCost;
}
