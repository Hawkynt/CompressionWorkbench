namespace Compression.Core.Dictionary.Parsing;

/// <summary>
/// An abstract LZ token produced by <see cref="Lz77OptimalParser"/>.
/// A token is either a single literal byte or a (distance, length) back-reference.
/// The token deliberately carries no bitstream encoding — a codec turns tokens into bytes.
/// </summary>
/// <param name="IsLiteral"><c>true</c> for a literal byte; <c>false</c> for a match.</param>
/// <param name="Literal">The literal byte value (valid when <see cref="IsLiteral"/> is <c>true</c>).</param>
/// <param name="Distance">The back-reference distance (valid when <see cref="IsLiteral"/> is <c>false</c>).</param>
/// <param name="Length">The match length (valid when <see cref="IsLiteral"/> is <c>false</c>).</param>
public readonly record struct LzParseToken(bool IsLiteral, byte Literal, int Distance, int Length) {
  /// <summary>Creates a literal token.</summary>
  /// <param name="value">The literal byte.</param>
  public static LzParseToken CreateLiteral(byte value) => new(true, value, 0, 0);

  /// <summary>Creates a match token.</summary>
  /// <param name="distance">The back-reference distance.</param>
  /// <param name="length">The match length.</param>
  public static LzParseToken CreateMatch(int distance, int length) => new(false, 0, distance, length);
}
