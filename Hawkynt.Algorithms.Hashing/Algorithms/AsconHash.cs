using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>ASCON-HASH / Ascon-Hash256 variant carried by the JavaScript registry.</summary>
public static class AsconHash {
  private static readonly ulong[] HashIv = [
    0xEE9398AADB67F03DUL,
    0x8BB21831C60F1002UL,
    0xB48A92DB98D5DA62UL,
    0x43189921B8F8E3E8UL,
    0x348FA5C9D525E140UL
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data) => ComputeCore(data, 32, HashIv);

  internal static byte[] ComputeCore(ReadOnlySpan<byte> data, int outputBytes, ReadOnlySpan<ulong> initialState) {
    if (outputBytes < 0)
      throw new ArgumentOutOfRangeException(nameof(outputBytes));

    Span<ulong> state = stackalloc ulong[5];
    initialState.CopyTo(state);
    var offset = 0;
    while (offset + 8 <= data.Length) {
      state[0] ^= BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8));
      AsconPermutation.P12(state);
      offset += 8;
    }

    Span<byte> final = stackalloc byte[8];
    final.Clear();
    data[offset..].CopyTo(final);
    final[data.Length - offset] = 0x80;
    state[0] ^= BinaryPrimitives.ReadUInt64BigEndian(final);

    var result = new byte[outputBytes];
    Span<byte> block = stackalloc byte[8];
    var written = 0;
    while (written < result.Length) {
      AsconPermutation.P12(state);
      BinaryPrimitives.WriteUInt64BigEndian(block, state[0]);
      var take = Math.Min(8, result.Length - written);
      block[..take].CopyTo(result.AsSpan(written));
      written += take;
    }
    return result;
  }
}

/// <summary>ASCON-XOF variant carried by the JavaScript registry.</summary>
public static class AsconXof {
  private static readonly ulong[] XofIv = [
    0xB57E273B814CD416UL,
    0x2B51042562AE2420UL,
    0x66A3A7768DDF2218UL,
    0x5AAD0A7A8153650CUL,
    0x4F3E0E32539493B6UL
  ];

  public static byte[] Compute(ReadOnlySpan<byte> data, int outputBytes) {
    if (outputBytes is < 1 or > 1024)
      throw new ArgumentOutOfRangeException(nameof(outputBytes));
    return AsconHash.ComputeCore(data, outputBytes, XofIv);
  }
}

internal static class AsconPermutation {
  private static readonly byte[] RoundConstants = [0xF0,0xE1,0xD2,0xC3,0xB4,0xA5,0x96,0x87,0x78,0x69,0x5A,0x4B];

  public static void P12(Span<ulong> state) {
    if (state.Length < 5)
      throw new ArgumentException("Ascon state requires five 64-bit words.", nameof(state));

    foreach (var constant in RoundConstants) {
      state[2] ^= constant;

      state[0] ^= state[4];
      state[4] ^= state[3];
      state[2] ^= state[1];

      var t0 = ~state[0] & state[1];
      var t1 = ~state[1] & state[2];
      var t2 = ~state[2] & state[3];
      var t3 = ~state[3] & state[4];
      var t4 = ~state[4] & state[0];

      state[0] ^= t1;
      state[1] ^= t2;
      state[2] ^= t3;
      state[3] ^= t4;
      state[4] ^= t0;

      state[1] ^= state[0];
      state[0] ^= state[4];
      state[3] ^= state[2];
      state[2] = ~state[2];

      state[0] ^= BitOperations.RotateRight(state[0], 19) ^ BitOperations.RotateRight(state[0], 28);
      state[1] ^= BitOperations.RotateRight(state[1], 61) ^ BitOperations.RotateRight(state[1], 39);
      state[2] ^= BitOperations.RotateRight(state[2], 1) ^ BitOperations.RotateRight(state[2], 6);
      state[3] ^= BitOperations.RotateRight(state[3], 10) ^ BitOperations.RotateRight(state[3], 17);
      state[4] ^= BitOperations.RotateRight(state[4], 7) ^ BitOperations.RotateRight(state[4], 41);
    }
  }
}
