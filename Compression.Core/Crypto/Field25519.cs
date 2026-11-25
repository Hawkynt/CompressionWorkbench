using System.Buffers.Binary;

namespace Compression.Core.Crypto;

/// <summary>
/// An element of GF(2^255 - 19) in five 51-bit limbs (radix 2^51), with arithmetic that runs the
/// same instructions whatever the values: no branches and no table lookups on the operands, and
/// carries, reductions and swaps done with masks. Shared by <see cref="X25519"/> and Ed25519.
/// </summary>
internal struct Field25519 {
  private const ulong Mask51 = (1UL << 51) - 1;

  // 2p in radix 2^51, added before a subtraction so no limb goes negative
  private const ulong TwoP0 = 0xFFFFFFFFFFFDAUL;
  private const ulong TwoP = 0xFFFFFFFFFFFFEUL;

  private ulong _l0, _l1, _l2, _l3, _l4;

  /// <summary>Counts field operations on this thread; lets tests check the operation sequence does not depend on secrets.</summary>
  [ThreadStatic] internal static long Operations;

  public static Field25519 Zero => default;
  public static Field25519 One => new() { _l0 = 1 };

  /// <summary>The element whose little-endian encoding is <paramref name="bytes"/> (32 bytes; the top bit is ignored).</summary>
  public static Field25519 FromBytes(ReadOnlySpan<byte> bytes) {
    var w0 = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    var w1 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    var w2 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
    var w3 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
    return new() {
      _l0 = w0 & Mask51,
      _l1 = ((w0 >> 51) | (w1 << 13)) & Mask51,
      _l2 = ((w1 >> 38) | (w2 << 26)) & Mask51,
      _l3 = ((w2 >> 25) | (w3 << 39)) & Mask51,
      _l4 = (w3 >> 12) & Mask51,
    };
  }

  /// <summary>The canonical 32-byte little-endian encoding (fully reduced below p).</summary>
  public readonly void ToBytes(Span<byte> output) {
    var t = Carry(this);
    t = Carry(t);
    // t < 2p now; subtract p when t >= p, decided by the carry out of t + 19
    var q = (t._l0 + 19) >> 51;
    q = (t._l1 + q) >> 51;
    q = (t._l2 + q) >> 51;
    q = (t._l3 + q) >> 51;
    q = (t._l4 + q) >> 51;
    t._l0 += 19 * q;
    t._l1 += t._l0 >> 51; t._l0 &= Mask51;
    t._l2 += t._l1 >> 51; t._l1 &= Mask51;
    t._l3 += t._l2 >> 51; t._l2 &= Mask51;
    t._l4 += t._l3 >> 51; t._l3 &= Mask51;
    t._l4 &= Mask51;
    BinaryPrimitives.WriteUInt64LittleEndian(output, t._l0 | (t._l1 << 51));
    BinaryPrimitives.WriteUInt64LittleEndian(output[8..], (t._l1 >> 13) | (t._l2 << 38));
    BinaryPrimitives.WriteUInt64LittleEndian(output[16..], (t._l2 >> 26) | (t._l3 << 25));
    BinaryPrimitives.WriteUInt64LittleEndian(output[24..], (t._l3 >> 39) | (t._l4 << 12));
  }

  private static Field25519 Carry(Field25519 a) {
    a._l1 += a._l0 >> 51; a._l0 &= Mask51;
    a._l2 += a._l1 >> 51; a._l1 &= Mask51;
    a._l3 += a._l2 >> 51; a._l2 &= Mask51;
    a._l4 += a._l3 >> 51; a._l3 &= Mask51;
    a._l0 += 19 * (a._l4 >> 51); a._l4 &= Mask51;
    a._l1 += a._l0 >> 51; a._l0 &= Mask51;
    return a;
  }

  public static Field25519 operator +(Field25519 a, Field25519 b) {
    ++Operations;
    return Carry(new() { _l0 = a._l0 + b._l0, _l1 = a._l1 + b._l1, _l2 = a._l2 + b._l2, _l3 = a._l3 + b._l3, _l4 = a._l4 + b._l4 });
  }

  public static Field25519 operator -(Field25519 a, Field25519 b) {
    ++Operations;
    // operands are carried (limbs below 2^51 + 2^13), so a + 2p - b stays positive
    return Carry(new() {
      _l0 = a._l0 + TwoP0 - b._l0, _l1 = a._l1 + TwoP - b._l1, _l2 = a._l2 + TwoP - b._l2,
      _l3 = a._l3 + TwoP - b._l3, _l4 = a._l4 + TwoP - b._l4,
    });
  }

  public static Field25519 operator *(Field25519 a, Field25519 b) {
    ++Operations;
    ulong b1 = b._l1 * 19, b2 = b._l2 * 19, b3 = b._l3 * 19, b4 = b._l4 * 19;
    var r0 = M(a._l0, b._l0) + M(a._l1, b4) + M(a._l2, b3) + M(a._l3, b2) + M(a._l4, b1);
    var r1 = M(a._l0, b._l1) + M(a._l1, b._l0) + M(a._l2, b4) + M(a._l3, b3) + M(a._l4, b2);
    var r2 = M(a._l0, b._l2) + M(a._l1, b._l1) + M(a._l2, b._l0) + M(a._l3, b4) + M(a._l4, b3);
    var r3 = M(a._l0, b._l3) + M(a._l1, b._l2) + M(a._l2, b._l1) + M(a._l3, b._l0) + M(a._l4, b4);
    var r4 = M(a._l0, b._l4) + M(a._l1, b._l3) + M(a._l2, b._l2) + M(a._l3, b._l1) + M(a._l4, b._l0);
    return Reduce(r0, r1, r2, r3, r4);
  }

  private static UInt128 M(ulong x, ulong y) => (UInt128)x * y;

  private static Field25519 Reduce(UInt128 r0, UInt128 r1, UInt128 r2, UInt128 r3, UInt128 r4) {
    r1 += r0 >> 51;
    var l0 = (ulong)r0 & Mask51;
    r2 += r1 >> 51;
    var l1 = (ulong)r1 & Mask51;
    r3 += r2 >> 51;
    var l2 = (ulong)r2 & Mask51;
    r4 += r3 >> 51;
    var l3 = (ulong)r3 & Mask51;
    var top = r4 >> 51;
    var l4 = (ulong)r4 & Mask51;
    var c = (UInt128)l0 + top * 19;
    l0 = (ulong)c & Mask51;
    l1 += (ulong)(c >> 51);
    return new() { _l0 = l0, _l1 = l1, _l2 = l2, _l3 = l3, _l4 = l4 };
  }

  public readonly Field25519 Square() => this * this;

  /// <summary>Multiplies by a small public constant.</summary>
  public readonly Field25519 MultiplySmall(uint k) {
    ++Operations;
    return Reduce(M(this._l0, k), M(this._l1, k), M(this._l2, k), M(this._l3, k), M(this._l4, k));
  }

  /// <summary>The inverse, as this^(p − 2), by a fixed square-and-multiply chain over the public exponent (0 maps to 0).</summary>
  public readonly Field25519 Invert() => this.Pow(PMinus2);

  /// <summary>this^((p − 5) / 8), for square roots in Ed25519 point decoding.</summary>
  public readonly Field25519 PowP58() => this.Pow(PMinus5Over8);

  // exponents as bit strings, most significant first; both are public constants
  private static readonly bool[] PMinus2 = ExponentBits(BigExponent(-2, 1));
  private static readonly bool[] PMinus5Over8 = ExponentBits(BigExponent(-5, 8));

  private static System.Numerics.BigInteger BigExponent(int offset, int divisor)
    => (((System.Numerics.BigInteger.One << 255) - 19) + offset) / divisor;

  private static bool[] ExponentBits(System.Numerics.BigInteger e) {
    var bits = new List<bool>();
    for (var v = e; v > 0; v >>= 1)
      bits.Add(!v.IsEven);
    bits.Reverse();
    return [.. bits];
  }

  private readonly Field25519 Pow(bool[] exponent) {
    var result = One;
    foreach (var bit in exponent) {
      result = result.Square();
      if (bit) // the exponent is public; the base is not inspected
        result *= this;
    }
    return result;
  }

  /// <summary>Swaps <paramref name="a"/> and <paramref name="b"/> when <paramref name="swap"/> is 1, without branching on it.</summary>
  public static void ConditionalSwap(ref Field25519 a, ref Field25519 b, ulong swap) {
    ++Operations;
    var mask = 0UL - swap;
    ulong t;
    t = mask & (a._l0 ^ b._l0); a._l0 ^= t; b._l0 ^= t;
    t = mask & (a._l1 ^ b._l1); a._l1 ^= t; b._l1 ^= t;
    t = mask & (a._l2 ^ b._l2); a._l2 ^= t; b._l2 ^= t;
    t = mask & (a._l3 ^ b._l3); a._l3 ^= t; b._l3 ^= t;
    t = mask & (a._l4 ^ b._l4); a._l4 ^= t; b._l4 ^= t;
  }

  /// <summary>Whether the canonical encoding is odd (the "negative" bit of Ed25519).</summary>
  public readonly bool IsNegative {
    get {
      Span<byte> b = stackalloc byte[32];
      this.ToBytes(b);
      return (b[0] & 1) != 0;
    }
  }

  /// <summary>Whether the element is zero.</summary>
  public readonly bool IsZero {
    get {
      Span<byte> b = stackalloc byte[32];
      this.ToBytes(b);
      var acc = 0;
      foreach (var x in b)
        acc |= x;
      return acc == 0;
    }
  }
}
