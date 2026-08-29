using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Google's CityHash64.</summary>
/// <remarks>
/// Written against the published algorithm (Pike and Alakuijala, CityHash 1.1).
/// Every intermediate there is a 64-bit unsigned value and wraps at 64 bits, so
/// the arithmetic here is <see cref="ulong"/> throughout: carrying wider values
/// and masking only at the end gives different results, because each
/// <c>>> 47</c> would otherwise mix in bits the algorithm has already dropped.
/// </remarks>
public static class CityHash {
  private const ulong K0 = 0xC3A5C85C97CB3127UL;
  private const ulong K1 = 0xB492B66FBE98F273UL;
  private const ulong K2 = 0x9AE16A3B2F90404FUL;

  /// <summary>The 64-bit hash of <paramref name="data" />.</summary>
  public static ulong Compute64(ReadOnlySpan<byte> data) => Hash64(data);

  /// <summary>The 64-bit hash of <paramref name="data" />, most significant byte first.</summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    var output = new byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(output, Compute64(data));
    return output;
  }

  private static ulong Hash64(ReadOnlySpan<byte> s) {
    var len = s.Length;
    if (len <= 32)
      return len <= 16 ? HashLen0To16(s) : HashLen17To32(s);
    if (len <= 64)
      return HashLen33To64(s);

    // For strings over 64 bytes the state is 56 bytes wide: v, w, x, y and z.
    var x = Fetch64(s, len - 40);
    var y = Fetch64(s, len - 16) + Fetch64(s, len - 56);
    var z = HashLen16(Fetch64(s, len - 48) + (ulong)len, Fetch64(s, len - 24));
    var v = WeakHashLen32WithSeeds(s, len - 64, (ulong)len, z);
    var w = WeakHashLen32WithSeeds(s, len - 32, y + K1, x);
    x = x * K1 + Fetch64(s, 0);

    // Decrease len to the nearest multiple of 64, and operate on 64-byte chunks.
    var remaining = (len - 1) & ~63;
    var offset = 0;
    do {
      x = Rotate(x + y + v.Low + Fetch64(s, offset + 8), 37) * K1;
      y = Rotate(y + v.High + Fetch64(s, offset + 48), 42) * K1;
      x ^= w.High;
      y += v.Low + Fetch64(s, offset + 40);
      z = Rotate(z + w.Low, 33) * K1;
      v = WeakHashLen32WithSeeds(s, offset, v.High * K1, x + w.Low);
      w = WeakHashLen32WithSeeds(s, offset + 32, z + w.High, y + Fetch64(s, offset + 16));
      (z, x) = (x, z);
      offset += 64;
      remaining -= 64;
    } while (remaining != 0);

    return HashLen16(HashLen16(v.Low, w.Low) + ShiftMix(y) * K1 + z,
      HashLen16(v.High, w.High) + x);
  }

  private static ulong HashLen0To16(ReadOnlySpan<byte> s) {
    var len = s.Length;
    if (len >= 8) {
      var mul = K2 + (ulong)len * 2;
      var a = Fetch64(s, 0) + K2;
      var b = Fetch64(s, len - 8);
      var c = Rotate(b, 37) * mul + a;
      var d = (Rotate(a, 25) + b) * mul;
      return HashLen16(c, d, mul);
    }

    if (len >= 4) {
      var mul = K2 + (ulong)len * 2;
      ulong a = Fetch32(s, 0);
      return HashLen16((ulong)len + (a << 3), Fetch32(s, len - 4), mul);
    }

    if (len > 0) {
      var a = s[0];
      var b = s[len >> 1];
      var c = s[len - 1];
      var y = a + ((uint)b << 8);
      var z = (uint)len + ((uint)c << 2);
      return ShiftMix(y * K2 ^ z * K0) * K2;
    }

    return K2;
  }

  private static ulong HashLen17To32(ReadOnlySpan<byte> s) {
    var len = s.Length;
    var mul = K2 + (ulong)len * 2;
    var a = Fetch64(s, 0) * K1;
    var b = Fetch64(s, 8);
    var c = Fetch64(s, len - 8) * mul;
    var d = Fetch64(s, len - 16) * K2;
    return HashLen16(Rotate(a + b, 43) + Rotate(c, 30) + d,
      a + Rotate(b + K2, 18) + c, mul);
  }

  private static ulong HashLen33To64(ReadOnlySpan<byte> s) {
    var len = s.Length;
    var mul = K2 + (ulong)len * 2;
    var a = Fetch64(s, 0) * K2;
    var b = Fetch64(s, 8);
    var c = Fetch64(s, len - 24);
    var d = Fetch64(s, len - 32);
    var e = Fetch64(s, 16) * K2;
    var f = Fetch64(s, 24) * 9;
    var g = Fetch64(s, len - 8);
    var h = Fetch64(s, len - 16) * mul;
    var u = Rotate(a + g, 43) + (Rotate(b, 30) + c) * 9;
    var v = ((a + g) ^ d) + f + 1;
    var w = BinaryPrimitives.ReverseEndianness((u + v) * mul) + h;
    var x = Rotate(e + f, 42) + c;
    var y = (BinaryPrimitives.ReverseEndianness((v + w) * mul) + g) * mul;
    var z = e + f + c;
    var a2 = BinaryPrimitives.ReverseEndianness((x + z) * mul + y) + b;
    var b2 = ShiftMix((z + a2) * mul + d + h) * mul;
    return HashLen16(a2, b2, mul);
  }

  /// <summary>Hashes 32 bytes down to a pair, from two seeds.</summary>
  private static (ulong Low, ulong High) WeakHashLen32WithSeeds(ulong w, ulong x, ulong y,
      ulong z, ulong a, ulong b) {
    a += w;
    b = Rotate(b + a + z, 21);
    var c = a;
    a += x;
    a += y;
    b += Rotate(a, 44);
    return (a + z, b + c);
  }

  private static (ulong Low, ulong High) WeakHashLen32WithSeeds(ReadOnlySpan<byte> s, int offset,
      ulong a, ulong b)
    => WeakHashLen32WithSeeds(Fetch64(s, offset), Fetch64(s, offset + 8),
      Fetch64(s, offset + 16), Fetch64(s, offset + 24), a, b);

  private static ulong HashLen16(ulong u, ulong v) => HashLen16(u, v, 0x9DDFEA08EB382D69UL);

  private static ulong HashLen16(ulong u, ulong v, ulong mul) {
    var a = (u ^ v) * mul;
    a ^= a >> 47;
    var b = (v ^ a) * mul;
    b ^= b >> 47;
    return b * mul;
  }

  private static ulong ShiftMix(ulong value) => value ^ (value >> 47);

  private static ulong Rotate(ulong value, int shift)
    => shift == 0 ? value : BitOperations.RotateRight(value, shift);

  private static uint Fetch32(ReadOnlySpan<byte> s, int offset)
    => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(offset, 4));

  private static ulong Fetch64(ReadOnlySpan<byte> s, int offset)
    => BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(offset, 8));
}
