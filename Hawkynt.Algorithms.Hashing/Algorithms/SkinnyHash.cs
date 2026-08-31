using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>The two SKINNY-HASH tweakey-state variants.</summary>
public enum SkinnyHashVariant {
  /// <summary>SKINNY-tk2-HASH: 32-byte state and 4-byte absorption rate.</summary>
  Tk2,
  /// <summary>SKINNY-tk3-HASH: 48-byte state and 16-byte absorption rate.</summary>
  Tk3
}

/// <summary>SKINNY-HASH lightweight hash family based on SKINNY-128.</summary>
public static class SkinnyHash {
  /// <summary>
  /// Creates a <see cref="SkinnyHash"/> containing exactly one bit size.
  /// </summary>
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [HashSizeRange.Exact(256)];

  /// <summary>
  /// Computes the Skinny Hash hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data, SkinnyHashVariant variant = SkinnyHashVariant.Tk2) => variant switch {
    SkinnyHashVariant.Tk2 => ComputeCore(data, false),
    SkinnyHashVariant.Tk3 => ComputeCore(data, true),
    _ => throw new ArgumentOutOfRangeException(nameof(variant))
  };

  private static byte[] ComputeCore(ReadOnlySpan<byte> data, bool tk3) {
    var stateSize = tk3 ? 48 : 32;
    var rate = tk3 ? 16 : 4;
    var state = new byte[stateSize];
    state[rate] = 0x80;

    var offset = 0;
    while (offset + rate <= data.Length) {
      for (var i = 0; i < rate; ++i)
        state[i] ^= data[offset + i];
      state = Permute(state, tk3);
      offset += rate;
    }

    var remaining = data.Length - offset;
    for (var i = 0; i < remaining; ++i)
      state[i] ^= data[offset + i];
    state[remaining] ^= 0x80;
    state = Permute(state, tk3);

    var result = new byte[32];
    state.AsSpan(0, 16).CopyTo(result);
    state = Permute(state, tk3);
    state.AsSpan(0, 16).CopyTo(result.AsSpan(16));
    return result;
  }

  private static byte[] Permute(ReadOnlySpan<byte> state, bool tk3) {
    var blocks = tk3 ? 3 : 2;
    var result = new byte[blocks * 16];
    Span<byte> plaintext = stackalloc byte[16];
    plaintext.Clear();
    for (var block = 0; block < blocks; ++block) {
      plaintext[0] = (byte)block;
      Encrypt(state, plaintext, result.AsSpan(block * 16, 16), tk3);
    }
    return result;
  }

  private static void Encrypt(ReadOnlySpan<byte> tweakey, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, bool tk3) {
    Span<uint> s = stackalloc uint[4];
    Span<uint> tk1 = stackalloc uint[4];
    Span<uint> tk2 = stackalloc uint[4];
    Span<uint> tk3Words = stackalloc uint[4];
    tk3Words.Clear();

    for (var i = 0; i < 4; ++i) {
      s[i] = BinaryPrimitives.ReadUInt32LittleEndian(plaintext.Slice(i * 4, 4));
      tk1[i] = BinaryPrimitives.ReadUInt32LittleEndian(tweakey.Slice(i * 4, 4));
      tk2[i] = BinaryPrimitives.ReadUInt32LittleEndian(tweakey.Slice(16 + i * 4, 4));
      if (tk3)
        tk3Words[i] = BinaryPrimitives.ReadUInt32LittleEndian(tweakey.Slice(32 + i * 4, 4));
    }

    var rc = 0U;
    var rounds = tk3 ? 56 : 48;
    for (var round = 0; round < rounds; round += 4) {
      SboxAll(s);
      rc = NextRoundConstant(rc);
      s[0] ^= tk1[0] ^ tk2[0] ^ tk3Words[0] ^ (rc & 0x0F);
      s[1] ^= tk1[1] ^ tk2[1] ^ tk3Words[1] ^ (rc >> 4);
      s[2] ^= 0x02;
      s[1] = BitOperations.RotateLeft(s[1], 8);
      s[2] = BitOperations.RotateLeft(s[2], 16);
      s[3] = BitOperations.RotateLeft(s[3], 24);
      s[1] ^= s[2]; s[2] ^= s[0]; s[3] ^= s[2];
      AdvanceTweakey(tk1, tk2, tk3Words, 2, tk3);

      SboxAll(s);
      rc = NextRoundConstant(rc);
      s[3] ^= tk1[2] ^ tk2[2] ^ tk3Words[2] ^ (rc & 0x0F);
      s[0] ^= tk1[3] ^ tk2[3] ^ tk3Words[3] ^ (rc >> 4);
      s[1] ^= 0x02;
      s[0] = BitOperations.RotateLeft(s[0], 8);
      s[1] = BitOperations.RotateLeft(s[1], 16);
      s[2] = BitOperations.RotateLeft(s[2], 24);
      s[0] ^= s[1]; s[1] ^= s[3]; s[2] ^= s[1];
      AdvanceTweakey(tk1, tk2, tk3Words, 0, tk3);

      SboxAll(s);
      rc = NextRoundConstant(rc);
      s[2] ^= tk1[0] ^ tk2[0] ^ tk3Words[0] ^ (rc & 0x0F);
      s[3] ^= tk1[1] ^ tk2[1] ^ tk3Words[1] ^ (rc >> 4);
      s[0] ^= 0x02;
      s[3] = BitOperations.RotateLeft(s[3], 8);
      s[0] = BitOperations.RotateLeft(s[0], 16);
      s[1] = BitOperations.RotateLeft(s[1], 24);
      s[3] ^= s[0]; s[0] ^= s[2]; s[1] ^= s[0];
      AdvanceTweakey(tk1, tk2, tk3Words, 2, tk3);

      SboxAll(s);
      rc = NextRoundConstant(rc);
      s[1] ^= tk1[2] ^ tk2[2] ^ tk3Words[2] ^ (rc & 0x0F);
      s[2] ^= tk1[3] ^ tk2[3] ^ tk3Words[3] ^ (rc >> 4);
      s[3] ^= 0x02;
      s[2] = BitOperations.RotateLeft(s[2], 8);
      s[3] = BitOperations.RotateLeft(s[3], 16);
      s[0] = BitOperations.RotateLeft(s[0], 24);
      s[2] ^= s[3]; s[3] ^= s[1]; s[0] ^= s[3];
      AdvanceTweakey(tk1, tk2, tk3Words, 0, tk3);
    }

    for (var i = 0; i < 4; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(ciphertext.Slice(i * 4, 4), s[i]);
  }

  private static void SboxAll(Span<uint> state) {
    for (var i = 0; i < state.Length; ++i)
      state[i] = Sbox(state[i]);
  }

  private static uint Sbox(uint x) {
    x = ~x;
    x ^= (x >> 2 & x >> 3) & 0x11111111U;
    var y = (x << 5 & x << 1) & 0x20202020U;
    x ^= ((x << 5 & x << 4) & 0x40404040U) ^ y;
    y = (x << 2 & x << 1) & 0x80808080U;
    x ^= ((x >> 2 & x << 1) & 0x02020202U) ^ y;
    y = (x >> 5 & x << 1) & 0x04040404U;
    x ^= ((x >> 1 & x >> 2) & 0x08080808U) ^ y;
    x = ~x;
    return ((x & 0x08080808U) << 1)
      | ((x & 0x32323232U) << 2)
      | ((x & 0x01010101U) << 5)
      | ((x & 0x80808080U) >> 6)
      | ((x & 0x40404040U) >> 4)
      | ((x & 0x04040404U) >> 2);
  }

  private static uint NextRoundConstant(uint rc) => ((rc << 1) ^ (rc >> 5 & 1) ^ (rc >> 4 & 1) ^ 1) & 0x3F;

  private static void AdvanceTweakey(Span<uint> tk1, Span<uint> tk2, Span<uint> tk3, int index, bool hasTk3) {
    PermuteTweakeyHalf(tk1, index);
    PermuteTweakeyHalf(tk2, index);
    tk2[index] = Lfsr2(tk2[index]);
    tk2[index + 1] = Lfsr2(tk2[index + 1]);
    if (!hasTk3)
      return;
    PermuteTweakeyHalf(tk3, index);
    tk3[index] = Lfsr3(tk3[index]);
    tk3[index + 1] = Lfsr3(tk3[index + 1]);
  }

  private static void PermuteTweakeyHalf(Span<uint> tweakey, int index) {
    var row2 = tweakey[index];
    var row3 = tweakey[index + 1];
    var rotated = BitOperations.RotateLeft(row3, 16);
    tweakey[index] = ((row2 >> 8) & 0x000000FFU) | ((row2 << 16) & 0x00FF0000U) | (rotated & 0xFF00FF00U);
    tweakey[index + 1] = ((row2 >> 16) & 0x000000FFU) | (row2 & 0xFF000000U) | ((rotated << 8) & 0x0000FF00U) | (rotated & 0x00FF0000U);
  }

  private static uint Lfsr2(uint value) => ((value << 1) & 0xFEFEFEFEU) ^ (((value >> 7) ^ (value >> 5)) & 0x01010101U);
  private static uint Lfsr3(uint value) => ((value >> 1) & 0x7F7F7F7FU) ^ (((value << 7) ^ (value << 1)) & 0x80808080U);
}
