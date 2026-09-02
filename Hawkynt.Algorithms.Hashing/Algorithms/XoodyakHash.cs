using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Xoodyak hash mode from the JavaScript registry.</summary>
public static class XoodyakHash {
  private const int StateBytes = 48;
  private const int Rate = 16;

  /// <summary>
  /// Computes the Xoodyak Hash hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    Span<byte> state = stackalloc byte[StateBytes];
    var count = 0;
    var mode = 0; // 0=initial absorb, 1=absorb, 2=squeeze
    var inputOffset = 0;
    var remaining = data.Length;
    var domain = 0x01;

    while (remaining > 0) {
      if (count >= Rate) {
        state[Rate] ^= 0x01;
        state[StateBytes - 1] ^= (byte)domain;
        XoodooPermutation.Permute(state);
        mode = 1;
        count = 0;
        domain = 0;
      }

      var take = Math.Min(Rate - count, remaining);
      for (var i = 0; i < take; ++i)
        state[count + i] ^= data[inputOffset + i];
      count += take;
      inputOffset += take;
      remaining -= take;
    }

    domain = mode == 0 ? 0x01 : 0x00;
    state[count] ^= 0x01;
    state[StateBytes - 1] ^= (byte)domain;
    XoodooPermutation.Permute(state);

    var result = new byte[32];
    count = 0;
    var written = 0;
    while (written < result.Length) {
      if (count >= Rate) {
        state[0] ^= 0x01;
        XoodooPermutation.Permute(state);
        count = 0;
      }
      var take = Math.Min(Rate - count, result.Length - written);
      state.Slice(count, take).CopyTo(result.AsSpan(written));
      count += take;
      written += take;
    }
    return result;
  }
}

internal static class XoodooPermutation {
  private static readonly uint[] RoundConstants = [
    0x00000058U, 0x00000038U, 0x000003C0U, 0x000000D0U,
    0x00000120U, 0x00000014U, 0x00000060U, 0x0000002CU,
    0x00000380U, 0x000000F0U, 0x000001A0U, 0x00000012U
  ];

  public static void Permute(Span<byte> state) {
    Span<uint> a = stackalloc uint[12];
    Span<uint> b = stackalloc uint[12];
    for (var i = 0; i < 12; ++i)
      a[i] = BinaryPrimitives.ReadUInt32LittleEndian(state.Slice(i * 4, 4));

    foreach (var rc in RoundConstants) {
      var p0 = a[0] ^ a[4] ^ a[8];
      var p1 = a[1] ^ a[5] ^ a[9];
      var p2 = a[2] ^ a[6] ^ a[10];
      var p3 = a[3] ^ a[7] ^ a[11];
      var e0 = BitOperations.RotateLeft(p3, 5) ^ BitOperations.RotateLeft(p3, 14);
      var e1 = BitOperations.RotateLeft(p0, 5) ^ BitOperations.RotateLeft(p0, 14);
      var e2 = BitOperations.RotateLeft(p1, 5) ^ BitOperations.RotateLeft(p1, 14);
      var e3 = BitOperations.RotateLeft(p2, 5) ^ BitOperations.RotateLeft(p2, 14);
      a[0] ^= e0; a[4] ^= e0; a[8] ^= e0;
      a[1] ^= e1; a[5] ^= e1; a[9] ^= e1;
      a[2] ^= e2; a[6] ^= e2; a[10] ^= e2;
      a[3] ^= e3; a[7] ^= e3; a[11] ^= e3;

      b[0] = a[0]; b[1] = a[1]; b[2] = a[2]; b[3] = a[3];
      b[4] = a[7]; b[5] = a[4]; b[6] = a[5]; b[7] = a[6];
      b[8] = BitOperations.RotateLeft(a[8], 11);
      b[9] = BitOperations.RotateLeft(a[9], 11);
      b[10] = BitOperations.RotateLeft(a[10], 11);
      b[11] = BitOperations.RotateLeft(a[11], 11);
      b[0] ^= rc;

      a[0] = b[0] ^ (~b[4] & b[8]);
      a[1] = b[1] ^ (~b[5] & b[9]);
      a[2] = b[2] ^ (~b[6] & b[10]);
      a[3] = b[3] ^ (~b[7] & b[11]);
      a[4] = b[4] ^ (~b[8] & b[0]);
      a[5] = b[5] ^ (~b[9] & b[1]);
      a[6] = b[6] ^ (~b[10] & b[2]);
      a[7] = b[7] ^ (~b[11] & b[3]);
      b[8] ^= ~b[0] & b[4];
      b[9] ^= ~b[1] & b[5];
      b[10] ^= ~b[2] & b[6];
      b[11] ^= ~b[3] & b[7];

      a[4] = BitOperations.RotateLeft(a[4], 1);
      a[5] = BitOperations.RotateLeft(a[5], 1);
      a[6] = BitOperations.RotateLeft(a[6], 1);
      a[7] = BitOperations.RotateLeft(a[7], 1);
      a[8] = BitOperations.RotateLeft(b[10], 8);
      a[9] = BitOperations.RotateLeft(b[11], 8);
      a[10] = BitOperations.RotateLeft(b[8], 8);
      a[11] = BitOperations.RotateLeft(b[9], 8);
    }

    for (var i = 0; i < 12; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(state.Slice(i * 4, 4), a[i]);
  }
}
