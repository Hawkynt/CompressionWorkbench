using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Subterranean-Hash, a 256-bit lightweight hash based on the 257-bit Subterranean permutation.</summary>
public static class SubterraneanHash {
  /// <summary>
  /// Computes the Subterranean Hash hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    Span<uint> state = stackalloc uint[9];
    state.Clear();

    foreach (var value in data) {
      DuplexByte(state, value);
      DuplexZero(state);
    }

    DuplexZero(state);
    DuplexZero(state);
    for (var round = 0; round < 8; ++round)
      DuplexZero(state);

    var result = new byte[32];
    for (var offset = 0; offset < result.Length; offset += 4) {
      var value = Extract(state);
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset, 4), value);
      if (offset + 4 < result.Length)
        DuplexZero(state);
    }
    return result;
  }

  private static void DuplexByte(Span<uint> state, byte value) {
    Round(state);
    // These are the exact 8 Subterranean injection positions used by the source implementation.
    if ((value & 0x01) != 0) Toggle(state, 1);
    if ((value & 0x02) != 0) Toggle(state, 176);
    if ((value & 0x04) != 0) Toggle(state, 136);
    if ((value & 0x08) != 0) Toggle(state, 35);
    if ((value & 0x10) != 0) Toggle(state, 249);
    if ((value & 0x20) != 0) Toggle(state, 134);
    if ((value & 0x40) != 0) Toggle(state, 197);
    if ((value & 0x80) != 0) Toggle(state, 234);
    Toggle(state, 64); // 9th padding bit.
  }

  private static void DuplexZero(Span<uint> state) {
    Round(state);
    state[0] ^= 0x02U;
  }

  private static void Round(Span<uint> state) {
    Span<bool> current = stackalloc bool[257];
    Span<bool> next = stackalloc bool[257];
    for (var i = 0; i < 257; ++i)
      current[i] = Get(state, i);

    // chi: s[i] ^= (~s[i+1]) & s[i+2]
    for (var i = 0; i < 257; ++i)
      next[i] = current[i] ^ (!current[(i + 1) % 257] && current[(i + 2) % 257]);
    next.CopyTo(current);

    // iota
    current[0] = !current[0];

    // theta: s[i] ^= s[i+3] ^ s[i+8]
    for (var i = 0; i < 257; ++i)
      next[i] = current[i] ^ current[(i + 3) % 257] ^ current[(i + 8) % 257];
    next.CopyTo(current);

    // pi: s[i] = s[(12*i) mod 257]
    for (var i = 0; i < 257; ++i)
      next[i] = current[(i * 12) % 257];

    state.Clear();
    for (var i = 0; i < 257; ++i)
      if (next[i])
        state[i >> 5] |= 1U << (i & 31);
  }

  private static uint Extract(ReadOnlySpan<uint> state) {
    uint x, y;

    x = state[0];
    x = (x & 0x00010000U)
      | ((x & 0x00000800U) << 6)
      | ((x & 0x00400000U) << 7)
      | ((x & 0x00000004U) << 10)
      | ((x & 0x00020000U) << 13)
      | ((x & 0x00800000U) >> 16)
      | ((x & 0x00000010U) << 20)
      | ((x & 0x40000100U) >> 4)
      | ((x & 0x00008002U) >> 1);
    y = x & 0x65035091U;

    x = state[1];
    x = (x & 0x00000008U)
      | ((x & 0x00004000U) << 5)
      | ((x & 0x00000004U) << 8)
      | ((x & 0x10000000U) >> 22)
      | ((x & 0x00000001U) << 28)
      | ((x & 0x00001000U) >> 3);
    y ^= x & 0x10080648U;

    x = state[2];
    x = ((x & 0x00000200U) << 2)
      | ((x & 0x10000000U) << 3)
      | ((x & 0x00000001U) << 8)
      | ((x & 0x00000040U) << 9)
      | ((x & 0x80000000U) >> 18)
      | ((x & 0x00020000U) >> 16)
      | ((x & 0x00000010U) << 18)
      | ((x & 0x00000008U) << 22)
      | ((x & 0x01000000U) >> 3);
    y ^= x & 0x8260A902U;

    x = state[3];
    x = ((x & 0x00200000U) << 6)
      | ((x & 0x00008000U) << 8)
      | ((x & 0x02000000U) >> 23)
      | ((x & 0x08000000U) >> 22)
      | ((x & 0x01000000U) >> 6);
    y ^= x & 0x08840024U;

    x = state[4];
    y ^= (x << 20) & 0x00100000U;
    x = ((x & 0x00040000U) << 5)
      | ((x & 0x00000200U) << 9)
      | ((x & 0x00001000U) << 15)
      | ((x & 0x00000002U) << 19)
      | ((x & 0x00000100U) >> 6)
      | ((x & 0x00000040U) >> 1);
    y ^= x & 0x08940024U;

    x = state[5];
    x = ((x & 0x00000004U) << 11)
      | ((x & 0x00000200U) << 12)
      | ((x & 0x00010000U) >> 15)
      | ((x & 0x01000000U) >> 13)
      | ((x & 0x08000000U) >> 12)
      | ((x & 0x20000000U) >> 7)
      | ((x & 0x00000020U) << 26)
      | ((x & 0x40000000U) >> 5);
    y ^= x & 0x8260A802U;

    x = state[6];
    x = (x & 0x00080000U)
      | ((x & 0x00000020U) << 1)
      | ((x & 0x40000000U) >> 27)
      | ((x & 0x00000002U) << 7)
      | ((x & 0x80000000U) >> 21)
      | ((x & 0x00200000U) >> 12);
    y ^= x & 0x00080748U;

    x = state[7];
    x = ((x & 0x02000000U) >> 21)
      | ((x & 0x80000000U) >> 19)
      | ((x & 0x00010000U) << 14)
      | ((x & 0x00000800U) << 18)
      | ((x & 0x00000008U) << 23)
      | BitOperations.RotateLeft(x & 0x20400002U, 27)
      | ((x & 0x00040000U) >> 4)
      | ((x & 0x00000400U) >> 3)
      | ((x & 0x00020000U) >> 1);
    y ^= x & 0x75035090U;

    return y ^ state[8];
  }

  private static bool Get(ReadOnlySpan<uint> state, int bit) =>
    ((state[bit >> 5] >> (bit & 31)) & 1U) != 0;

  private static void Toggle(Span<uint> state, int bit) =>
    state[bit >> 5] ^= 1U << (bit & 31);
}
