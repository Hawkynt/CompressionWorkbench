namespace Hawkynt.Algorithms.Hashing;

/// <summary>The four standardized KNOT-HASH parameter sets.</summary>
public enum KnotHashVariant {
  /// <summary>KNOT-HASH-256-256: 256-bit digest over the 256-bit permutation.</summary>
  KnotHash256_256,
  /// <summary>KNOT-HASH-256-384: 256-bit digest over the 384-bit permutation.</summary>
  KnotHash256_384,
  /// <summary>KNOT-HASH-384-384: 384-bit digest over the 384-bit permutation.</summary>
  KnotHash384_384,
  /// <summary>KNOT-HASH-512-512: 512-bit digest over the 512-bit permutation.</summary>
  KnotHash512_512
}

/// <summary>KNOT-HASH family with explicit parameter-set selection.</summary>
/// <remarks>
/// Digest size alone cannot identify a KNOT variant because both KNOT-HASH-256-256 and
/// KNOT-HASH-256-384 produce 256-bit digests. The variant therefore remains an explicit part of
/// the API while <see cref="SupportedHashSizes"/> advertises the distinct digest sizes produced by
/// the family.
/// </remarks>
public static class KnotHash {
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [
    HashSizeRange.Exact(256),
    new(384, 512, 128)
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data, KnotHashVariant variant = KnotHashVariant.KnotHash256_256) => variant switch {
    KnotHashVariant.KnotHash256_256 => KnotHashCore.Compute(data, 32, 4, 68, 256, false),
    KnotHashVariant.KnotHash256_384 => KnotHashCore.Compute(data, 32, 16, 80, 384, true),
    KnotHashVariant.KnotHash384_384 => KnotHashCore.Compute(data, 48, 6, 104, 384, false),
    KnotHashVariant.KnotHash512_512 => KnotHashCore.Compute(data, 64, 8, 140, 512, false),
    _ => throw new ArgumentOutOfRangeException(nameof(variant))
  };
}

internal static class KnotHashCore {
  private static readonly byte[] Rc7 = [
    0x01,0x02,0x04,0x08,0x10,0x20,0x41,0x03,0x06,0x0C,0x18,0x30,
    0x61,0x42,0x05,0x0A,0x14,0x28,0x51,0x23,0x47,0x0F,0x1E,0x3C,
    0x79,0x72,0x64,0x48,0x11,0x22,0x45,0x0B,0x16,0x2C,0x59,0x33,
    0x67,0x4E,0x1D,0x3A,0x75,0x6A,0x54,0x29,0x53,0x27,0x4F,0x1F,
    0x3E,0x7D,0x7A,0x74,0x68,0x50,0x21,0x43,0x07,0x0E,0x1C,0x38,
    0x71,0x62,0x44,0x09,0x12,0x24,0x49,0x13,0x26,0x4D,0x1B,0x36,
    0x6D,0x5A,0x35,0x6B,0x56,0x2D,0x5B,0x37,0x6F,0x5E,0x3D,0x7B,
    0x76,0x6C,0x58,0x31,0x63,0x46,0x0D,0x1A,0x34,0x69,0x52,0x25,
    0x4B,0x17,0x2E,0x5D,0x3B,0x77,0x6E,0x5C
  ];

  private static readonly byte[] Rc8 = [
    0x01,0x02,0x04,0x08,0x11,0x23,0x47,0x8E,0x1C,0x38,0x71,0xE2,
    0xC4,0x89,0x12,0x25,0x4B,0x97,0x2E,0x5C,0xB8,0x70,0xE0,0xC0,
    0x81,0x03,0x06,0x0C,0x19,0x32,0x64,0xC9,0x92,0x24,0x49,0x93,
    0x26,0x4D,0x9B,0x37,0x6E,0xDC,0xB9,0x72,0xE4,0xC8,0x90,0x20,
    0x41,0x82,0x05,0x0A,0x15,0x2B,0x56,0xAD,0x5B,0xB6,0x6D,0xDA,
    0xB5,0x6B,0xD6,0xAC,0x59,0xB2,0x65,0xCB,0x96,0x2C,0x58,0xB0,
    0x61,0xC3,0x87,0x0F,0x1F,0x3E,0x7D,0xFB,0xF6,0xED,0xDB,0xB7,
    0x6F,0xDE,0xBD,0x7A,0xF5,0xEB,0xD7,0xAE,0x5D,0xBA,0x74,0xE8,
    0xD1,0xA2,0x44,0x88,0x10,0x21,0x43,0x86,0x0D,0x1B,0x36,0x6C,
    0xD8,0xB1,0x63,0xC7,0x8F,0x1E,0x3C,0x79,0xF3,0xE7,0xCE,0x9C,
    0x39,0x73,0xE6,0xCC,0x98,0x31,0x62,0xC5,0x8B,0x16,0x2D,0x5A,
    0xB4,0x69,0xD2,0xA4,0x48,0x91,0x22,0x45
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data, int outputBytes, int rate, int rounds, int stateBits, bool domain80) {
    var state = new byte[stateBits / 8];
    if (domain80)
      state[^1] ^= 0x80;

    var offset = 0;
    while (offset + rate <= data.Length) {
      for (var i = 0; i < rate; ++i)
        state[i] ^= data[offset + i];
      Permute(state, rounds, stateBits);
      offset += rate;
    }
    var remaining = data.Length - offset;
    for (var i = 0; i < remaining; ++i)
      state[i] ^= data[offset + i];
    state[remaining] ^= 0x01;
    Permute(state, rounds, stateBits);

    var result = new byte[outputBytes];
    var squeeze = outputBytes / 2;
    state.AsSpan(0, squeeze).CopyTo(result);
    Permute(state, rounds, stateBits);
    state.AsSpan(0, squeeze).CopyTo(result.AsSpan(squeeze));
    return result;
  }

  private static void Permute(Span<byte> state, int rounds, int stateBits) {
    if (stateBits == 256)
      Permute256(state, rounds);
    else if (stateBits == 384)
      Permute384(state, rounds);
    else
      Permute512(state, rounds);
  }

  private static void Permute256(Span<byte> state, int rounds) {
    var a0 = Read64(state, 0);
    var a1 = Read64(state, 8);
    var a2 = Read64(state, 16);
    var a3 = Read64(state, 24);
    for (var r = 0; r < rounds; ++r) {
      a0 ^= Rc7[r];
      Sbox(ref a0, ref a1, ref a2, ref a3);
      a1 = Rotate(a1, 1, 64);
      a2 = Rotate(a2, 8, 64);
      a3 = Rotate(a3, 25, 64);
    }
    Write64(state, 0, a0); Write64(state, 8, a1); Write64(state, 16, a2); Write64(state, 24, a3);
  }

  private static void Permute384(Span<byte> state, int rounds) {
    var a0 = Read96(state, 0);
    var a1 = Read96(state, 12);
    var a2 = Read96(state, 24);
    var a3 = Read96(state, 36);
    for (var r = 0; r < rounds; ++r) {
      a0 ^= Rc7[r];
      Sbox(ref a0, ref a1, ref a2, ref a3);
      a1 = Rotate(a1, 1, 96);
      a2 = Rotate(a2, 8, 96);
      a3 = Rotate(a3, 55, 96);
    }
    Write96(state, 0, a0); Write96(state, 12, a1); Write96(state, 24, a2); Write96(state, 36, a3);
  }

  private static void Permute512(Span<byte> state, int rounds) {
    var a0 = Read128(state, 0);
    var a1 = Read128(state, 16);
    var a2 = Read128(state, 32);
    var a3 = Read128(state, 48);
    for (var r = 0; r < rounds; ++r) {
      a0 ^= Rc8[r];
      Sbox(ref a0, ref a1, ref a2, ref a3);
      a1 = Rotate(a1, 1, 128);
      a2 = Rotate(a2, 16, 128);
      a3 = Rotate(a3, 25, 128);
    }
    Write128(state, 0, a0); Write128(state, 16, a1); Write128(state, 32, a2); Write128(state, 48, a3);
  }

  private static void Sbox(ref UInt128 a0, ref UInt128 a1, ref UInt128 a2, ref UInt128 a3) {
    var t1 = ~a0;
    var t3 = a2 ^ (a1 & t1);
    var b3 = a3 ^ t3;
    var t6 = a3 ^ t1;
    var b2 = (a1 | a2) ^ t6;
    t1 = a1 ^ a3;
    a0 = t1 ^ (t3 & t6);
    var b1 = t3 ^ (b2 & t1);
    a1 = b1; a2 = b2; a3 = b3;
  }

  private static UInt128 Rotate(UInt128 value, int bits, int width) {
    var mask = width == 128 ? UInt128.MaxValue : ((UInt128.One << width) - 1);
    value &= mask;
    return ((value << bits) | (value >> (width - bits))) & mask;
  }

  // The lanes are held as UInt128 whatever their width, the way the 96- and
  // 128-bit permutations do; Rotate masks each back to its own width.
  private static UInt128 Read64(ReadOnlySpan<byte> s, int offset) {
    UInt128 v = 0;
    for (var i = 0; i < 8; ++i) v |= (UInt128)s[offset + i] << (8 * i);
    return v;
  }
  private static void Write64(Span<byte> s, int offset, UInt128 v) { for (var i = 0; i < 8; ++i) s[offset + i] = (byte)(v >> (8 * i)); }
  private static UInt128 Read96(ReadOnlySpan<byte> s, int offset) { UInt128 v = 0; for (var i = 0; i < 12; ++i) v |= (UInt128)s[offset + i] << (8 * i); return v; }
  private static void Write96(Span<byte> s, int offset, UInt128 v) { for (var i = 0; i < 12; ++i) s[offset + i] = (byte)(v >> (8 * i)); }
  private static UInt128 Read128(ReadOnlySpan<byte> s, int offset) { UInt128 v = 0; for (var i = 0; i < 16; ++i) v |= (UInt128)s[offset + i] << (8 * i); return v; }
  private static void Write128(Span<byte> s, int offset, UInt128 v) { for (var i = 0; i < 16; ++i) s[offset + i] = (byte)(v >> (8 * i)); }
}
