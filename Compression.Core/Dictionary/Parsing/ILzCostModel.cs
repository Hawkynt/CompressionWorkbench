namespace Compression.Core.Dictionary.Parsing;

/// <summary>
/// Pluggable cost model for <see cref="Lz77OptimalParser"/>. It reports the estimated
/// bit-cost of encoding a literal or a (length, distance) match. The parser minimises the
/// total reported cost, so any encoder-aware model (fixed, length/distance-bucketed,
/// Huffman, or range-coder-aware) can be injected without changing the parser.
/// </summary>
/// <remarks>
/// Costs are returned in arbitrary units (conventionally bits). Only relative magnitudes
/// matter to the shortest-path search; the absolute scale is irrelevant.
/// </remarks>
public interface ILzCostModel {
  /// <summary>Estimated bit-cost of emitting a single literal byte.</summary>
  /// <param name="value">The literal byte value.</param>
  double LiteralCost(byte value);

  /// <summary>Estimated bit-cost of emitting a (length, distance) match.</summary>
  /// <param name="length">The match length (>= the coder's minimum match length).</param>
  /// <param name="distance">The back-reference distance (1-based).</param>
  double MatchCost(int length, int distance);
}
