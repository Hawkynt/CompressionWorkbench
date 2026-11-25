using System.Numerics;

namespace Compression.Core.Crypto;

/// <summary>
/// The X25519 Diffie-Hellman function of RFC 7748 (section 5): the Montgomery ladder over
/// Curve25519 with the u-coordinate only, scalars and coordinates as 32-byte little-endian strings.
/// </summary>
/// <remarks>Field arithmetic uses <see cref="BigInteger"/> and is not constant-time.</remarks>
public static class X25519 {
  /// <summary>The length of scalars, coordinates and shared secrets.</summary>
  public const int KeySize = 32;

  private static readonly BigInteger P = (BigInteger.One << 255) - 19;
  private const int A24 = 121665;

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
    var n = new BigInteger(k, isUnsigned: true, isBigEndian: false);
    Span<byte> uBytes = stackalloc byte[KeySize];
    u.CopyTo(uBytes);
    uBytes[31] &= 127;
    var x1 = new BigInteger(uBytes, isUnsigned: true, isBigEndian: false) % P;

    BigInteger x2 = BigInteger.One, z2 = BigInteger.Zero, x3 = x1, z3 = BigInteger.One;
    var swap = 0;
    for (var t = 254; t >= 0; --t) {
      var bit = (int)((n >> t) & BigInteger.One);
      swap ^= bit;
      if (swap != 0) {
        (x2, x3) = (x3, x2);
        (z2, z3) = (z3, z2);
      }
      swap = bit;
      var a = (x2 + z2) % P;
      var aa = a * a % P;
      var b = (x2 - z2 + P) % P;
      var bb = b * b % P;
      var e = (aa - bb + P) % P;
      var c = (x3 + z3) % P;
      var d = (x3 - z3 + P) % P;
      var da = d * a % P;
      var cb = c * b % P;
      var sum = (da + cb) % P;
      var diff = (da - cb + P) % P;
      x3 = sum * sum % P;
      z3 = x1 * (diff * diff % P) % P;
      x2 = aa * bb % P;
      z2 = e * ((aa + A24 * e) % P) % P;
    }
    if (swap != 0) {
      (x2, x3) = (x3, x2);
      (z2, z3) = (z3, z2);
    }
    var result = x2 * BigInteger.ModPow(z2, P - 2, P) % P;
    var output = new byte[KeySize];
    result.TryWriteBytes(output, out _, isUnsigned: true, isBigEndian: false);
    return output;
  }

  /// <summary>The public key (u-coordinate) of <paramref name="scalar"/>.</summary>
  public static byte[] PublicKey(ReadOnlySpan<byte> scalar) => ScalarMultiply(scalar, BasePoint);
}
