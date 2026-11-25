namespace Compression.Core.Crypto;

/// <summary>
/// The X25519 Diffie-Hellman function of RFC 7748 (section 5): the Montgomery ladder over
/// Curve25519 with the u-coordinate only, scalars and coordinates as 32-byte little-endian strings.
/// The ladder runs the same field operations for every scalar: its conditional swaps are masked,
/// and <see cref="Field25519"/> neither branches nor looks up tables on its operands.
/// </summary>
public static class X25519 {
  /// <summary>The length of scalars, coordinates and shared secrets.</summary>
  public const int KeySize = 32;

  private const uint A24 = 121665;

  /// <summary>The base point's u-coordinate, 9.</summary>
  public static ReadOnlySpan<byte> BasePoint => [9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

  /// <summary>X25519(<paramref name="scalar"/>, <paramref name="u"/>): the scalar is clamped, the top bit of u is ignored.</summary>
  /// <exception cref="ArgumentException">Either input is not 32 bytes.</exception>
  public static byte[] ScalarMultiply(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> u) {
    if (scalar.Length != KeySize || u.Length != KeySize)
      throw new ArgumentException("X25519 scalars and coordinates are 32 bytes.");
    Span<byte> k = stackalloc byte[KeySize];
    scalar.CopyTo(k);
    k[0] &= 248;
    k[31] &= 127;
    k[31] |= 64;

    var x1 = Field25519.FromBytes(u);
    var x2 = Field25519.One;
    var z2 = Field25519.Zero;
    var x3 = x1;
    var z3 = Field25519.One;
    ulong swap = 0;
    for (var t = 254; t >= 0; --t) {
      var bit = (ulong)((k[t >> 3] >> (t & 7)) & 1);
      swap ^= bit;
      Field25519.ConditionalSwap(ref x2, ref x3, swap);
      Field25519.ConditionalSwap(ref z2, ref z3, swap);
      swap = bit;
      var a = x2 + z2;
      var aa = a.Square();
      var b = x2 - z2;
      var bb = b.Square();
      var e = aa - bb;
      var c = x3 + z3;
      var d = x3 - z3;
      var da = d * a;
      var cb = c * b;
      x3 = (da + cb).Square();
      z3 = x1 * (da - cb).Square();
      x2 = aa * bb;
      z2 = e * (aa + e.MultiplySmall(A24));
    }
    Field25519.ConditionalSwap(ref x2, ref x3, swap);
    Field25519.ConditionalSwap(ref z2, ref z3, swap);
    k.Clear();
    var output = new byte[KeySize];
    (x2 * z2.Invert()).ToBytes(output);
    return output;
  }

  /// <summary>The public key (u-coordinate) of <paramref name="scalar"/>.</summary>
  public static byte[] PublicKey(ReadOnlySpan<byte> scalar) => ScalarMultiply(scalar, BasePoint);
}
