using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Fowler-Noll-Vo hash family.</summary>
public static class Fnv {
  /// <summary>
  /// Performs the compute-1 32 operation provided by <see cref="Fnv"/>.
  /// </summary>
  public static uint Compute1_32(ReadOnlySpan<byte> data, uint offsetBasis = 2166136261U) {
    var hash = offsetBasis;
    foreach (var value in data) {
      hash = unchecked(hash * 16777619U);
      hash ^= value;
    }
    return hash;
  }

  /// <summary>
  /// Performs the compute-1 a 32 operation provided by <see cref="Fnv"/>.
  /// </summary>
  public static uint Compute1A_32(ReadOnlySpan<byte> data, uint offsetBasis = 2166136261U) {
    var hash = offsetBasis;
    foreach (var value in data) {
      hash ^= value;
      hash = unchecked(hash * 16777619U);
    }
    return hash;
  }

  /// <summary>
  /// Performs the compute-1 64 operation provided by <see cref="Fnv"/>.
  /// </summary>
  public static ulong Compute1_64(ReadOnlySpan<byte> data, ulong offsetBasis = 14695981039346656037UL) {
    var hash = offsetBasis;
    foreach (var value in data) {
      hash = unchecked(hash * 1099511628211UL);
      hash ^= value;
    }
    return hash;
  }

  /// <summary>
  /// Performs the compute-1 a 64 operation provided by <see cref="Fnv"/>.
  /// </summary>
  public static ulong Compute1A_64(ReadOnlySpan<byte> data, ulong offsetBasis = 14695981039346656037UL) {
    var hash = offsetBasis;
    foreach (var value in data) {
      hash ^= value;
      hash = unchecked(hash * 1099511628211UL);
    }
    return hash;
  }
}

/// <summary>MurmurHash3 x86-32 and x64-128 variants.</summary>
public static class MurmurHash3 {
  /// <summary>
  /// Computes the 32-bit Murmur Hash-3 hash of the supplied data.
  /// </summary>
  public static uint Compute32(ReadOnlySpan<byte> data, uint seed = 0) {
    const uint c1 = 0xCC9E2D51;
    const uint c2 = 0x1B873593;

    var hash = seed;
    var blocks = data.Length / 4;
    for (var i = 0; i < blocks; ++i) {
      var k = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
      k = unchecked(k * c1);
      k = BitOperations.RotateLeft(k, 15);
      k = unchecked(k * c2);

      hash ^= k;
      hash = BitOperations.RotateLeft(hash, 13);
      hash = unchecked(hash * 5 + 0xE6546B64);
    }

    uint tail = 0;
    var tailSpan = data[(blocks * 4)..];
    switch (tailSpan.Length) {
      case 3:
        tail ^= (uint)tailSpan[2] << 16;
        goto case 2;
      case 2:
        tail ^= (uint)tailSpan[1] << 8;
        goto case 1;
      case 1:
        tail ^= tailSpan[0];
        tail = unchecked(tail * c1);
        tail = BitOperations.RotateLeft(tail, 15);
        tail = unchecked(tail * c2);
        hash ^= tail;
        break;
    }

    hash ^= (uint)data.Length;
    return Fmix32(hash);
  }

  /// <summary>
  /// Performs the static operation provided by <see cref="MurmurHash3"/>.
  /// </summary>
  public static (ulong Low, ulong High) Compute128(ReadOnlySpan<byte> data, ulong seed = 0) {
    const ulong c1 = 0x87C37B91114253D5UL;
    const ulong c2 = 0x4CF5AD432745937FUL;

    var h1 = seed;
    var h2 = seed;
    var blocks = data.Length / 16;

    for (var i = 0; i < blocks; ++i) {
      var block = data[(i * 16)..];
      var k1 = BinaryPrimitives.ReadUInt64LittleEndian(block);
      var k2 = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);

      k1 = unchecked(k1 * c1);
      k1 = BitOperations.RotateLeft(k1, 31);
      k1 = unchecked(k1 * c2);
      h1 ^= k1;

      h1 = BitOperations.RotateLeft(h1, 27);
      h1 = unchecked(h1 + h2);
      h1 = unchecked(h1 * 5 + 0x52DCE729);

      k2 = unchecked(k2 * c2);
      k2 = BitOperations.RotateLeft(k2, 33);
      k2 = unchecked(k2 * c1);
      h2 ^= k2;

      h2 = BitOperations.RotateLeft(h2, 31);
      h2 = unchecked(h2 + h1);
      h2 = unchecked(h2 * 5 + 0x38495AB5);
    }

    ulong tail1 = 0;
    ulong tail2 = 0;
    var tail = data[(blocks * 16)..];
    switch (tail.Length) {
      case 15: tail2 ^= (ulong)tail[14] << 48; goto case 14;
      case 14: tail2 ^= (ulong)tail[13] << 40; goto case 13;
      case 13: tail2 ^= (ulong)tail[12] << 32; goto case 12;
      case 12: tail2 ^= (ulong)tail[11] << 24; goto case 11;
      case 11: tail2 ^= (ulong)tail[10] << 16; goto case 10;
      case 10: tail2 ^= (ulong)tail[9] << 8; goto case 9;
      case 9:
        tail2 ^= tail[8];
        tail2 = unchecked(tail2 * c2);
        tail2 = BitOperations.RotateLeft(tail2, 33);
        tail2 = unchecked(tail2 * c1);
        h2 ^= tail2;
        goto case 8;
      case 8: tail1 ^= (ulong)tail[7] << 56; goto case 7;
      case 7: tail1 ^= (ulong)tail[6] << 48; goto case 6;
      case 6: tail1 ^= (ulong)tail[5] << 40; goto case 5;
      case 5: tail1 ^= (ulong)tail[4] << 32; goto case 4;
      case 4: tail1 ^= (ulong)tail[3] << 24; goto case 3;
      case 3: tail1 ^= (ulong)tail[2] << 16; goto case 2;
      case 2: tail1 ^= (ulong)tail[1] << 8; goto case 1;
      case 1:
        tail1 ^= tail[0];
        tail1 = unchecked(tail1 * c1);
        tail1 = BitOperations.RotateLeft(tail1, 31);
        tail1 = unchecked(tail1 * c2);
        h1 ^= tail1;
        break;
    }

    var length = (ulong)data.Length;
    h1 ^= length;
    h2 ^= length;

    h1 = unchecked(h1 + h2);
    h2 = unchecked(h2 + h1);

    h1 = Fmix64(h1);
    h2 = Fmix64(h2);

    h1 = unchecked(h1 + h2);
    h2 = unchecked(h2 + h1);
    return (h1, h2);
  }

  /// <summary>
  /// Computes the 128-bit Murmur Hash-3 hash and returns its encoded bytes.
  /// </summary>
  public static byte[] Compute128Bytes(ReadOnlySpan<byte> data, ulong seed = 0) {
    var (low, high) = Compute128(data, seed);
    var result = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(result, low);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), high);
    return result;
  }

  private static uint Fmix32(uint value) {
    value ^= value >> 16;
    value = unchecked(value * 0x85EBCA6B);
    value ^= value >> 13;
    value = unchecked(value * 0xC2B2AE35);
    value ^= value >> 16;
    return value;
  }

  private static ulong Fmix64(ulong value) {
    value ^= value >> 33;
    value = unchecked(value * 0xFF51AFD7ED558CCDUL);
    value ^= value >> 33;
    value = unchecked(value * 0xC4CEB9FE1A85EC53UL);
    value ^= value >> 33;
    return value;
  }
}

/// <summary>SipHash-2-4 keyed 64-bit hash.</summary>
public static class SipHash24 {
  /// <summary>
  /// Computes the Sip Hash-24 hash of the supplied data.
  /// </summary>
  public static ulong Compute(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key) {
    if (key.Length != 16)
      throw new ArgumentException("SipHash-2-4 requires a 16-byte key.", nameof(key));

    var k0 = BinaryPrimitives.ReadUInt64LittleEndian(key);
    var k1 = BinaryPrimitives.ReadUInt64LittleEndian(key[8..]);

    var v0 = 0x736F6D6570736575UL ^ k0;
    var v1 = 0x646F72616E646F6DUL ^ k1;
    var v2 = 0x6C7967656E657261UL ^ k0;
    var v3 = 0x7465646279746573UL ^ k1;

    var offset = 0;
    while (offset + 8 <= data.Length) {
      var m = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
      v3 ^= m;
      SipRound(ref v0, ref v1, ref v2, ref v3);
      SipRound(ref v0, ref v1, ref v2, ref v3);
      v0 ^= m;
      offset += 8;
    }

    var last = (ulong)data.Length << 56;
    var tail = data[offset..];
    for (var i = 0; i < tail.Length; ++i)
      last |= (ulong)tail[i] << (8 * i);

    v3 ^= last;
    SipRound(ref v0, ref v1, ref v2, ref v3);
    SipRound(ref v0, ref v1, ref v2, ref v3);
    v0 ^= last;
    v2 ^= 0xFF;

    for (var i = 0; i < 4; ++i)
      SipRound(ref v0, ref v1, ref v2, ref v3);

    return v0 ^ v1 ^ v2 ^ v3;
  }

  private static void SipRound(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3) {
    v0 = unchecked(v0 + v1);
    v1 = BitOperations.RotateLeft(v1, 13);
    v1 ^= v0;
    v0 = BitOperations.RotateLeft(v0, 32);

    v2 = unchecked(v2 + v3);
    v3 = BitOperations.RotateLeft(v3, 16);
    v3 ^= v2;

    v0 = unchecked(v0 + v3);
    v3 = BitOperations.RotateLeft(v3, 21);
    v3 ^= v0;

    v2 = unchecked(v2 + v1);
    v1 = BitOperations.RotateLeft(v1, 17);
    v1 ^= v2;
    v2 = BitOperations.RotateLeft(v2, 32);
  }
}
