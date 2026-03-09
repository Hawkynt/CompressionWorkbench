namespace Compression.Core.Deflate;

/// <summary>
/// Represents a literal or length/distance symbol in the DEFLATE LZ parse.
/// </summary>
/// <param name="LitLen">For literals: the byte value (0–255). For matches: the match length (3–258).</param>
/// <param name="Distance">0 for literals, 1–32768 for back-references.</param>
internal readonly record struct LzSymbol(ushort LitLen, ushort Distance) {
  /// <summary>Returns <c>true</c> if this symbol is a literal byte.</summary>
  public bool IsLiteral => Distance == 0;

  /// <summary>Creates a literal symbol.</summary>
  public static LzSymbol Literal(byte value) => new(value, 0);

  /// <summary>Creates a length/distance match symbol.</summary>
  public static LzSymbol Match(int length, int distance) => new((ushort)length, (ushort)distance);
}
