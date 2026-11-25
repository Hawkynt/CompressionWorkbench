using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Compression.Core.Crypto;

/// <summary>
/// Ed25519 signatures (RFC 8032 section 5.1): key generation from a 32-byte seed, signing and
/// verification over twisted Edwards points in extended coordinates. Signing touches the secret
/// scalar only through constant-time code: a double-and-add-always point multiplication with
/// masked selection on <see cref="Field25519"/>, and scalar arithmetic modulo L by masked
/// conditional subtraction.
/// </summary>
public static class Ed25519 {
  /// <summary>Seed, public key and half-signature length.</summary>
  public const int KeySize = 32;

  /// <summary>Signature length (R ‖ S).</summary>
  public const int SignatureSize = 64;

  private static readonly Field25519 D;
  private static readonly Field25519 D2;
  private static readonly Field25519 SqrtM1;
  private static readonly Point BasePoint;

  static Ed25519() {
    // d = -121665 / 121666; sqrt(-1) = 2^((p - 1) / 4) (RFC 8032 section 5.1)
    D = Field25519.FromUInt(121665).Negate() * Field25519.FromUInt(121666).Invert();
    D2 = D + D;
    var p = (System.Numerics.BigInteger.One << 255) - 19;
    SqrtM1 = Field25519.FromUInt(2).Pow((p - 1) / 4);
    // B: y = 4/5, x positive, encoded 0x58 0x66 ... 0x66
    Span<byte> b = stackalloc byte[32];
    b.Fill(0x66);
    b[0] = 0x58;
    BasePoint = Decode(b) ?? throw new InvalidOperationException("The Ed25519 base point does not decode.");
  }

  private readonly record struct Point(Field25519 X, Field25519 Y, Field25519 Z, Field25519 T) {
    public static Point Identity => new(Field25519.Zero, Field25519.One, Field25519.One, Field25519.Zero);
  }

  // RFC 8032 section 5.1.4: the complete addition formula, also used for doubling
  private static Point Add(Point p, Point q) {
    var a = (p.Y - p.X) * (q.Y - q.X);
    var b = (p.Y + p.X) * (q.Y + q.X);
    var c = p.T * D2 * q.T;
    var d = p.Z * (q.Z + q.Z);
    var e = b - a;
    var f = d - c;
    var g = d + c;
    var h = b + a;
    return new(e * f, g * h, f * g, e * h);
  }

  private static Point Select(Point a, Point b, ulong choose)
    => new(Field25519.Select(a.X, b.X, choose), Field25519.Select(a.Y, b.Y, choose),
      Field25519.Select(a.Z, b.Z, choose), Field25519.Select(a.T, b.T, choose));

  /// <summary>[k]P for a 256-bit little-endian scalar: double and add always, keeping the sum by mask.</summary>
  private static Point Multiply(ReadOnlySpan<byte> k, Point p) {
    var q = Point.Identity;
    for (var i = 255; i >= 0; --i) {
      q = Add(q, q);
      var sum = Add(q, p);
      q = Select(q, sum, (ulong)((k[i >> 3] >> (i & 7)) & 1));
    }
    return q;
  }

  private static void Encode(Point p, Span<byte> output) {
    var zi = p.Z.Invert();
    var x = p.X * zi;
    (p.Y * zi).ToBytes(output);
    if (x.IsNegative)
      output[31] |= 0x80;
  }

  /// <summary>RFC 8032 section 5.1.3; null when the string is not a point.</summary>
  private static Point? Decode(ReadOnlySpan<byte> s) {
    if (s.Length != KeySize)
      return null;
    var sign = s[31] >> 7;
    Span<byte> yBytes = stackalloc byte[32];
    s.CopyTo(yBytes);
    yBytes[31] &= 0x7F;
    var y = Field25519.FromBytes(yBytes);
    Span<byte> canonical = stackalloc byte[32];
    y.ToBytes(canonical);
    if (!canonical.SequenceEqual(yBytes))
      return null; // y >= p
    var y2 = y.Square();
    var u = y2 - Field25519.One;
    var v = D * y2 + Field25519.One;
    var v3 = v.Square() * v;
    var x = u * v3 * (u * v3.Square() * v).PowP58();
    var vx2 = v * x.Square();
    if (!vx2.EqualsInConstantTime(u)) {
      if (!vx2.EqualsInConstantTime(u.Negate()))
        return null;
      x *= SqrtM1;
    }
    if (x.IsZero && sign == 1)
      return null;
    if ((x.IsNegative ? 1 : 0) != sign)
      x = x.Negate();
    return new Point(x, y, Field25519.One, x * y);
  }

  private static void Expand(ReadOnlySpan<byte> seed, Span<byte> scalar, Span<byte> prefix) {
    Span<byte> h = stackalloc byte[64];
    SHA512.HashData(seed, h);
    h[..32].CopyTo(scalar);
    scalar[0] &= 248;
    scalar[31] &= 127;
    scalar[31] |= 64;
    h[32..].CopyTo(prefix);
    h.Clear();
  }

  /// <summary>The public key A of a 32-byte <paramref name="seed"/>.</summary>
  public static byte[] PublicKey(ReadOnlySpan<byte> seed) {
    if (seed.Length != KeySize)
      throw new ArgumentException("An Ed25519 seed is 32 bytes.", nameof(seed));
    Span<byte> a = stackalloc byte[32];
    Span<byte> prefix = stackalloc byte[32];
    Expand(seed, a, prefix);
    var output = new byte[KeySize];
    Encode(Multiply(a, BasePoint), output);
    a.Clear();
    prefix.Clear();
    return output;
  }

  /// <summary>Signs <paramref name="message"/> with the key of <paramref name="seed"/> (RFC 8032 section 5.1.6).</summary>
  public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message) {
    if (seed.Length != KeySize)
      throw new ArgumentException("An Ed25519 seed is 32 bytes.", nameof(seed));
    Span<byte> a = stackalloc byte[32];
    Span<byte> prefix = stackalloc byte[32];
    Expand(seed, a, prefix);
    Span<byte> publicKey = stackalloc byte[32];
    Encode(Multiply(a, BasePoint), publicKey);

    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
    hash.AppendData(prefix);
    hash.AppendData(message);
    var r = Scalar25519.Reduce(hash.GetHashAndReset());
    var signature = new byte[SignatureSize];
    Encode(Multiply(r, BasePoint), signature.AsSpan(0, 32));
    hash.AppendData(signature.AsSpan(0, 32));
    hash.AppendData(publicKey);
    hash.AppendData(message);
    var k = Scalar25519.Reduce(hash.GetHashAndReset());
    Scalar25519.MultiplyAdd(k, a, r).CopyTo(signature, 32);
    a.Clear();
    prefix.Clear();
    Array.Clear(r);
    return signature;
  }

  /// <summary>Verifies <paramref name="signature"/> on <paramref name="message"/> under <paramref name="publicKey"/> (RFC 8032 section 5.1.7).</summary>
  public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature) {
    if (publicKey.Length != KeySize || signature.Length != SignatureSize)
      return false;
    if (Decode(publicKey) is not { } a || Decode(signature[..32]) is not { } r)
      return false;
    var s = signature[32..];
    if (!Scalar25519.IsCanonical(s))
      return false;
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
    hash.AppendData(signature[..32]);
    hash.AppendData(publicKey);
    hash.AppendData(message);
    var k = Scalar25519.Reduce(hash.GetHashAndReset());
    // [S]B == R + [k]A, compared as encodings
    Span<byte> left = stackalloc byte[32];
    Span<byte> right = stackalloc byte[32];
    Encode(Multiply(s, BasePoint), left);
    Encode(Add(r, Multiply(k, a)), right);
    return CryptographicOperations.FixedTimeEquals(left, right);
  }
}

/// <summary>
/// Scalars modulo the Ed25519 group order L = 2^252 + 27742317777372353535851937790883648493,
/// 32-byte little-endian, with masked conditional subtraction so the values never steer a branch.
/// </summary>
internal static class Scalar25519 {
  // L as five 64-bit limbs, little-endian (the fifth is zero; room for intermediate values)
  private static readonly ulong[] L = [0x5812631A5CF5D3EDUL, 0x14DEF9DEA2F79CD6UL, 0UL, 0x1000000000000000UL, 0UL];

  /// <summary>Whether a 32-byte little-endian value is below L.</summary>
  public static bool IsCanonical(ReadOnlySpan<byte> s) {
    var v = Load(s);
    return Borrow(v, L) != 0;
  }

  /// <summary>A little-endian byte string of any length (64 for a SHA-512 digest) reduced modulo L.</summary>
  public static byte[] Reduce(ReadOnlySpan<byte> value) {
    var acc = new ulong[5];
    for (var i = value.Length - 1; i >= 0; --i) {
      ShiftLeft8(acc);
      acc[0] |= value[i];
      for (var k = 8; k >= 0; --k)
        ConditionalSubtractShifted(acc, k);
    }
    return Store(acc);
  }

  /// <summary>(k · a + r) mod L; constant-time in <paramref name="a"/> and <paramref name="r"/>.</summary>
  public static byte[] MultiplyAdd(ReadOnlySpan<byte> k, ReadOnlySpan<byte> a, ReadOnlySpan<byte> r) {
    var av = ReduceToLimbs(a);
    var acc = new ulong[5];
    for (var i = 255; i >= 0; --i) {
      Add(acc, acc);
      ConditionalSubtractShifted(acc, 0);
      var bit = (ulong)((k[i >> 3] >> (i & 7)) & 1);
      var addend = new ulong[5];
      for (var j = 0; j < 5; ++j)
        addend[j] = av[j] & (0UL - bit);
      Add(acc, addend);
      ConditionalSubtractShifted(acc, 0);
    }
    Add(acc, ReduceToLimbs(r));
    ConditionalSubtractShifted(acc, 0);
    var result = Store(acc);
    Array.Clear(av);
    Array.Clear(acc);
    return result;
  }

  private static ulong[] ReduceToLimbs(ReadOnlySpan<byte> v) => Load(Reduce(v));

  private static ulong[] Load(ReadOnlySpan<byte> s) {
    Span<byte> padded = stackalloc byte[32];
    s[..Math.Min(32, s.Length)].CopyTo(padded);
    return [BinaryPrimitives.ReadUInt64LittleEndian(padded), BinaryPrimitives.ReadUInt64LittleEndian(padded[8..]),
      BinaryPrimitives.ReadUInt64LittleEndian(padded[16..]), BinaryPrimitives.ReadUInt64LittleEndian(padded[24..]), 0];
  }

  private static byte[] Store(ulong[] v) {
    var output = new byte[32];
    for (var i = 0; i < 4; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(8 * i), v[i]);
    return output;
  }

  private static void ShiftLeft8(ulong[] v) {
    for (var i = 4; i > 0; --i)
      v[i] = (v[i] << 8) | (v[i - 1] >> 56);
    v[0] <<= 8;
  }

  private static void Add(ulong[] v, ulong[] w) {
    UInt128 carry = 0;
    for (var i = 0; i < 5; ++i) {
      var s = (UInt128)v[i] + w[i] + carry;
      v[i] = (ulong)s;
      carry = s >> 64;
    }
  }

  /// <summary>The borrow out of v − w (1 when v &lt; w), computed without branches.</summary>
  private static ulong Borrow(ulong[] v, ulong[] w) {
    ulong borrow = 0;
    for (var i = 0; i < 5; ++i) {
      var d = (UInt128)v[i] - w[i] - borrow;
      borrow = (ulong)(d >> 64) & 1;
    }
    return borrow;
  }

  /// <summary>v −= L·2^k when v ≥ L·2^k, by mask.</summary>
  private static void ConditionalSubtractShifted(ulong[] v, int k) {
    var shifted = new ulong[5];
    for (var i = 0; i < 5; ++i)
      shifted[i] = (L[i] << k) | (i > 0 && k > 0 ? L[i - 1] >> (64 - k) : 0);
    var diff = new ulong[5];
    ulong borrow = 0;
    for (var i = 0; i < 5; ++i) {
      var d = (UInt128)v[i] - shifted[i] - borrow;
      diff[i] = (ulong)d;
      borrow = (ulong)(d >> 64) & 1;
    }
    var keep = 0UL - borrow; // all ones when v < L·2^k
    for (var i = 0; i < 5; ++i)
      v[i] = (v[i] & keep) | (diff[i] & ~keep);
  }
}
