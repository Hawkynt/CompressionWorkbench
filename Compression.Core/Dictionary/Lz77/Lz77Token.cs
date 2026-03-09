namespace Compression.Core.Dictionary.Lz77;

/// <summary>
/// Represents an LZ77 token: either a literal byte or a (distance, length) match reference.
/// </summary>
/// <param name="IsLiteral"><c>true</c> if this is a literal byte; <c>false</c> if it is a match reference.</param>
/// <param name="Literal">The literal byte value (valid when <see cref="IsLiteral"/> is <c>true</c>).</param>
/// <param name="Distance">The match distance (valid when <see cref="IsLiteral"/> is <c>false</c>).</param>
/// <param name="Length">The match length (valid when <see cref="IsLiteral"/> is <c>false</c>).</param>
public readonly record struct Lz77Token(bool IsLiteral, byte Literal, int Distance, int Length) {
  /// <summary>
  /// Creates a literal token.
  /// </summary>
  /// <param name="value">The literal byte.</param>
  /// <returns>A literal token.</returns>
  public static Lz77Token CreateLiteral(byte value) => new(true, value, 0, 0);

  /// <summary>
  /// Creates a match token.
  /// </summary>
  /// <param name="distance">The match distance.</param>
  /// <param name="length">The match length.</param>
  /// <returns>A match token.</returns>
  public static Lz77Token CreateMatch(int distance, int length) => new(false, 0, distance, length);
}
