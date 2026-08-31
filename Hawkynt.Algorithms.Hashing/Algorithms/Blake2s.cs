using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>BLAKE2s-256.</summary>
public static class Blake2s {
  /// <summary>
  /// Computes the Blake-2 s hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) => Blake2sCore.Compute(data, 32, 1, 1, 0, 0, 0, 0);
}

/// <summary>BLAKE2xs extendable-output function.</summary>
public static class Blake2xs {
  /// <summary>
  /// Computes the Blake-2 xs hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data, int outputBytes) {
    if (outputBytes is < 1 or > 65534)
      throw new ArgumentOutOfRangeException(nameof(outputBytes));

    var root = Blake2sCore.Compute(data, 32, 1, 1, 0, 0, 0, outputBytes);
    var result = new byte[outputBytes];
    var written = 0;
    uint nodeOffset = 0;
    while (written < outputBytes) {
      var take = Math.Min(32, outputBytes - written);
      var block = Blake2sCore.Compute(root, take, 0, 0, 32, nodeOffset, 32, outputBytes);
      block.CopyTo(result, written);
      written += take;
      ++nodeOffset;
    }
    return result;
  }
}

internal static class Blake2sCore {
  private const int BlockBytes = 64;
  private static readonly uint[] Iv = [
    0x6A09E667U,0xBB67AE85U,0x3C6EF372U,0xA54FF53AU,
    0x510E527FU,0x9B05688CU,0x1F83D9ABU,0x5BE0CD19U
  ];
  private static readonly byte[,] Sigma = {
    {0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15},
    {14,10,4,8,9,15,13,6,1,12,0,2,11,7,5,3},
    {11,8,12,0,5,2,15,13,10,14,3,6,7,1,9,4},
    {7,9,3,1,13,12,11,14,2,6,5,10,4,0,15,8},
    {9,0,5,7,2,4,10,15,14,1,11,12,6,8,3,13},
    {2,12,6,10,0,11,8,3,4,13,7,5,15,14,1,9},
    {12,5,1,15,14,13,4,10,0,7,6,3,9,2,8,11},
    {13,11,7,14,12,1,3,9,5,0,15,4,8,6,2,10},
    {6,15,14,9,11,3,0,8,12,2,13,7,1,4,10,5},
    {10,2,8,4,7,6,1,5,15,11,9,14,3,12,13,0}
  };

  public static byte[] Compute(
    ReadOnlySpan<byte> data,
    int outputBytes,
    byte fanout,
    byte depth,
    uint leafLength,
    uint nodeOffset,
    byte innerLength,
    int xofLength
  ) {
    if (outputBytes is < 1 or > 32)
      throw new ArgumentOutOfRangeException(nameof(outputBytes));

    Span<uint> h = stackalloc uint[8];
    Iv.CopyTo(h);
    h[0] ^= (uint)(outputBytes | (fanout << 16) | (depth << 24));
    h[1] ^= leafLength;
    h[2] ^= nodeOffset;
    h[3] ^= (uint)((xofLength & 0xFFFF) | (innerLength << 24));

    ulong counter = 0;
    var offset = 0;
    while (offset + BlockBytes < data.Length) {
      counter += BlockBytes;
      Compress(h, data.Slice(offset, BlockBytes), counter, false);
      offset += BlockBytes;
    }

    Span<byte> final = stackalloc byte[BlockBytes];
    final.Clear();
    data[offset..].CopyTo(final);
    counter += (ulong)(data.Length - offset);
    Compress(h, final, counter, true);

    var result = new byte[outputBytes];
    Span<byte> word = stackalloc byte[4];
    for (var i = 0; i < 8 && i * 4 < outputBytes; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(word, h[i]);
      word[..Math.Min(4, outputBytes - i * 4)].CopyTo(result.AsSpan(i * 4));
    }
    return result;
  }

  private static void Compress(Span<uint> h, ReadOnlySpan<byte> block, ulong counter, bool final) {
    Span<uint> m = stackalloc uint[16];
    Span<uint> v = stackalloc uint[16];
    for (var i = 0; i < 16; ++i)
      m[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
    h.CopyTo(v);
    Iv.CopyTo(v[8..]);
    v[12] ^= (uint)counter;
    v[13] ^= (uint)(counter >> 32);
    if (final)
      v[14] = ~v[14];

    for (var r = 0; r < 10; ++r) {
      G(v, 0,4,8,12, m[Sigma[r,0]], m[Sigma[r,1]]);
      G(v, 1,5,9,13, m[Sigma[r,2]], m[Sigma[r,3]]);
      G(v, 2,6,10,14, m[Sigma[r,4]], m[Sigma[r,5]]);
      G(v, 3,7,11,15, m[Sigma[r,6]], m[Sigma[r,7]]);
      G(v, 0,5,10,15, m[Sigma[r,8]], m[Sigma[r,9]]);
      G(v, 1,6,11,12, m[Sigma[r,10]], m[Sigma[r,11]]);
      G(v, 2,7,8,13, m[Sigma[r,12]], m[Sigma[r,13]]);
      G(v, 3,4,9,14, m[Sigma[r,14]], m[Sigma[r,15]]);
    }
    for (var i = 0; i < 8; ++i)
      h[i] ^= v[i] ^ v[i + 8];
  }

  private static void G(Span<uint> v, int a, int b, int c, int d, uint x, uint y) {
    v[a] = unchecked(v[a] + v[b] + x);
    v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
    v[c] = unchecked(v[c] + v[d]);
    v[b] = BitOperations.RotateRight(v[b] ^ v[c], 12);
    v[a] = unchecked(v[a] + v[b] + y);
    v[d] = BitOperations.RotateRight(v[d] ^ v[a], 8);
    v[c] = unchecked(v[c] + v[d]);
    v[b] = BitOperations.RotateRight(v[b] ^ v[c], 7);
  }
}
