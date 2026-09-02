using System.Numerics;

namespace Compression.Core.Dictionary.Parsing;

/// <summary>
/// A simple, codec-agnostic cost model for <see cref="Lz77OptimalParser"/>.
/// </summary>
/// <remarks>
/// <para>
/// Literals cost a flat number of bits (default 9, matching a typical literal/length
/// alphabet that reserves one symbol for "this is a literal"). A match costs a fixed
/// token overhead plus a bucketed component for the length and the distance: roughly the
/// number of bits needed to encode each magnitude. This mirrors how real LZ coders price
/// matches (Deflate length/distance extra-bits, LZMA distance slots) so that the optimal
/// parser prefers short distances and avoids tiny far matches — without binding the parser
/// to any specific bitstream.
/// </para>
/// <para>
/// The model is deterministic and stateless; swap in a Huffman/range-coder-aware model to
/// drive the same parser from real symbol statistics.
/// </para>
/// </remarks>
public sealed class DefaultLzCostModel : ILzCostModel {
  private readonly double _literalCost;
  private readonly double _matchTokenCost;

  /// <summary>
  /// Creates a cost model.
  /// </summary>
  /// <param name="literalBits">Flat bit-cost of one literal. Defaults to 9.</param>
  /// <param name="matchTokenBits">
  /// Fixed bit overhead of a match token (the flag plus base length/distance codes), on top
  /// of the magnitude-dependent component. Defaults to 9.
  /// </param>
  public DefaultLzCostModel(double literalBits = 9.0, double matchTokenBits = 9.0) {
    this._literalCost = literalBits;
    this._matchTokenCost = matchTokenBits;
  }

  /// <summary>A shared instance using the default parameters.</summary>
  public static DefaultLzCostModel Instance { get; } = new();

  /// <inheritdoc/>
  /// <summary>
  /// Performs the literal cost operation.
  /// </summary>
  public double LiteralCost(byte value) => this._literalCost;

  /// <inheritdoc/>
  /// <summary>
  /// Performs the match cost operation.
  /// </summary>
  public double MatchCost(int length, int distance) {
    // Bits to express the length magnitude (cheap base + extra bits for larger lengths).
    var lengthBits = BitsFor(length);

    // Bits to express the distance magnitude — the dominant term, so longer distances cost
    // more and the parser favours nearby matches.
    var distanceBits = BitsFor(distance);

    return this._matchTokenCost + lengthBits + distanceBits;
  }

  // Number of significant bits in a positive magnitude (>= 1), e.g. 1 -> 1, 7 -> 3, 258 -> 9.
  private static double BitsFor(int value)
    => value <= 1 ? 1.0 : 32 - BitOperations.LeadingZeroCount((uint)value);
}
