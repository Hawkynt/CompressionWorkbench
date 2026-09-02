using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Original BLAKE SHA-3 finalist family.</summary>
public static class Blake {
  private static readonly byte[,] Sigma = {
    { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14,15 },
    {14,10, 4, 8, 9,15,13, 6, 1,12, 0, 2,11, 7, 5, 3 },
    {11, 8,12, 0, 5, 2,15,13,10,14, 3, 6, 7, 1, 9, 4 },
    { 7, 9, 3, 1,13,12,11,14, 2, 6, 5,10, 4, 0,15, 8 },
    { 9, 0, 5, 7, 2, 4,10,15,14, 1,11,12, 6, 8, 3,13 },
    { 2,12, 6,10, 0,11, 8, 3, 4,13, 7, 5,15,14, 1, 9 },
    {12, 5, 1,15,14,13, 4,10, 0, 7, 6, 3, 9, 2, 8,11 },
    {13,11, 7,14,12, 1, 3, 9, 5, 0,15, 4, 8, 6, 2,10 },
    { 6,15,14, 9,11, 3, 0, 8,12, 2,13, 7, 1, 4,10, 5 },
    {10, 2, 8, 4, 7, 6, 1, 5,15,11, 9,14, 3,12,13, 0 }
  };

  private static readonly uint[] Constants32 = [
    0x243F6A88U,0x85A308D3U,0x13198A2EU,0x03707344U,
    0xA4093822U,0x299F31D0U,0x082EFA98U,0xEC4E6C89U,
    0x452821E6U,0x38D01377U,0xBE5466CFU,0x34E90C6CU,
    0xC0AC29B7U,0xC97C50DDU,0x3F84D5B5U,0xB5470917U
  ];

  private static readonly ulong[] Constants64 = [
    0x243F6A8885A308D3UL,0x13198A2E03707344UL,
    0xA4093822299F31D0UL,0x082EFA98EC4E6C89UL,
    0x452821E638D01377UL,0xBE5466CF34E90C6CUL,
    0xC0AC29B7C97C50DDUL,0x3F84D5B5B5470917UL,
    0x9216D5D98979FB1BUL,0xD1310BA698DFB5ACUL,
    0x2FFD72DBD01ADFB7UL,0xB8E1AFED6A267E96UL,
    0xBA7C9045F12C7F99UL,0x24A19947B3916CF7UL,
    0x0801F2E2858EFC16UL,0x636920D871574E69UL
  ];

  private static readonly uint[] Iv224 = [
    0xC1059ED8U,0x367CD507U,0x3070DD17U,0xF70E5939U,
    0xFFC00B31U,0x68581511U,0x64F98FA7U,0xBEFA4FA4U
  ];

  private static readonly uint[] Iv256 = [
    0x6A09E667U,0xBB67AE85U,0x3C6EF372U,0xA54FF53AU,
    0x510E527FU,0x9B05688CU,0x1F83D9ABU,0x5BE0CD19U
  ];

  private static readonly ulong[] Iv384 = [
    0xCBBB9D5DC1059ED8UL,0x629A292A367CD507UL,
    0x9159015A3070DD17UL,0x152FECD8F70E5939UL,
    0x67332667FFC00B31UL,0x8EB44A8768581511UL,
    0xDB0C2E0D64F98FA7UL,0x47B5481DBEFA4FA4UL
  ];

  private static readonly ulong[] Iv512 = [
    0x6A09E667F3BCC908UL,0xBB67AE8584CAA73BUL,
    0x3C6EF372FE94F82BUL,0xA54FF53A5F1D36F1UL,
    0x510E527FADE682D1UL,0x9B05688C2B3E6C1FUL,
    0x1F83D9ABFB41BD6BUL,0x5BE0CD19137E2179UL
  ];

  /// <summary>
  /// Computes the 224-bit Blake hash of the supplied data.
  /// </summary>
  public static byte[] Compute224(ReadOnlySpan<byte> data) => Compute32(data, Iv224, 28, 0x00);
  /// <summary>
  /// Computes the 256-bit Blake hash of the supplied data.
  /// </summary>
  public static byte[] Compute256(ReadOnlySpan<byte> data) => Compute32(data, Iv256, 32, 0x01);
  /// <summary>
  /// Computes the 384-bit Blake hash of the supplied data.
  /// </summary>
  public static byte[] Compute384(ReadOnlySpan<byte> data) => Compute64(data, Iv384, 48, 0x00);
  /// <summary>
  /// Computes the 512-bit Blake hash of the supplied data.
  /// </summary>
  public static byte[] Compute512(ReadOnlySpan<byte> data) => Compute64(data, Iv512, 64, 0x01);

  private static byte[] Compute32(ReadOnlySpan<byte> data, uint[] initial, int outputBytes, byte marker) {
    var state = initial.ToArray();
    var fullBlocks = data.Length / 64;
    var remainder = data.Length % 64;

    for (var block = 0; block < fullBlocks; ++block)
      Compress32(state, data.Slice(block * 64, 64), unchecked((ulong)(block + 1) * 512UL), false);

    Span<byte> first = stackalloc byte[64];
    first.Clear();
    data[(fullBlocks * 64)..].CopyTo(first);
    var totalBits = unchecked((ulong)data.Length * 8UL);

    if (remainder == 55) {
      first[55] = (byte)(0x80 | marker);
      BinaryPrimitives.WriteUInt64BigEndian(first[56..], totalBits);
      Compress32(state, first, totalBits, remainder == 0);
    } else if (remainder < 55) {
      first[remainder] = 0x80;
      first[55] = marker;
      BinaryPrimitives.WriteUInt64BigEndian(first[56..], totalBits);
      Compress32(state, first, totalBits, remainder == 0);
    } else {
      first[remainder] = 0x80;
      Compress32(state, first, totalBits, false);

      Span<byte> second = stackalloc byte[64];
      second.Clear();
      second[55] = marker;
      BinaryPrimitives.WriteUInt64BigEndian(second[56..], totalBits);
      Compress32(state, second, 0, true);
    }

    var result = new byte[outputBytes];
    for (var i = 0; i < outputBytes / 4; ++i)
      BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(i * 4, 4), state[i]);
    return result;
  }

  private static void Compress32(uint[] h, ReadOnlySpan<byte> block, ulong counterBits, bool nullCounter) {
    Span<uint> m = stackalloc uint[16];
    Span<uint> v = stackalloc uint[16];
    for (var i = 0; i < 16; ++i)
      m[i] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(i * 4, 4));

    for (var i = 0; i < 8; ++i)
      v[i] = h[i];
    v[8] = Constants32[0]; v[9] = Constants32[1]; v[10] = Constants32[2]; v[11] = Constants32[3];
    v[12] = Constants32[4]; v[13] = Constants32[5]; v[14] = Constants32[6]; v[15] = Constants32[7];
    if (!nullCounter) {
      var low = (uint)counterBits;
      var high = (uint)(counterBits >> 32);
      v[12] ^= low; v[13] ^= low;
      v[14] ^= high; v[15] ^= high;
    }

    for (var round = 0; round < 14; ++round) {
      var r = round % 10;
      G32(v, 0,4, 8,12, m, r,0); G32(v, 1,5, 9,13, m, r,1);
      G32(v, 2,6,10,14, m, r,2); G32(v, 3,7,11,15, m, r,3);
      G32(v, 0,5,10,15, m, r,4); G32(v, 1,6,11,12, m, r,5);
      G32(v, 2,7, 8,13, m, r,6); G32(v, 3,4, 9,14, m, r,7);
    }

    for (var i = 0; i < 8; ++i)
      h[i] ^= v[i] ^ v[i + 8];
  }

  private static void G32(Span<uint> v, int a, int b, int c, int d, ReadOnlySpan<uint> m, int round, int pair) {
    var i0 = Sigma[round, pair * 2];
    var i1 = Sigma[round, pair * 2 + 1];
    v[a] = unchecked(v[a] + v[b] + (m[i0] ^ Constants32[i1]));
    v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
    v[c] = unchecked(v[c] + v[d]);
    v[b] = BitOperations.RotateRight(v[b] ^ v[c], 12);
    v[a] = unchecked(v[a] + v[b] + (m[i1] ^ Constants32[i0]));
    v[d] = BitOperations.RotateRight(v[d] ^ v[a], 8);
    v[c] = unchecked(v[c] + v[d]);
    v[b] = BitOperations.RotateRight(v[b] ^ v[c], 7);
  }

  private static byte[] Compute64(ReadOnlySpan<byte> data, ulong[] initial, int outputBytes, byte marker) {
    var state = initial.ToArray();
    var fullBlocks = data.Length / 128;
    var remainder = data.Length % 128;

    for (var block = 0; block < fullBlocks; ++block)
      Compress64(state, data.Slice(block * 128, 128), unchecked((ulong)(block + 1) * 1024UL), false);

    Span<byte> first = stackalloc byte[128];
    first.Clear();
    data[(fullBlocks * 128)..].CopyTo(first);
    var totalBits = unchecked((ulong)data.Length * 8UL);

    if (remainder == 111) {
      first[111] = (byte)(0x80 | marker);
      BinaryPrimitives.WriteUInt64BigEndian(first[112..120], 0);
      BinaryPrimitives.WriteUInt64BigEndian(first[120..], totalBits);
      Compress64(state, first, totalBits, remainder == 0);
    } else if (remainder < 111) {
      first[remainder] = 0x80;
      first[111] = marker;
      BinaryPrimitives.WriteUInt64BigEndian(first[112..120], 0);
      BinaryPrimitives.WriteUInt64BigEndian(first[120..], totalBits);
      Compress64(state, first, totalBits, remainder == 0);
    } else {
      first[remainder] = 0x80;
      Compress64(state, first, totalBits, false);

      Span<byte> second = stackalloc byte[128];
      second.Clear();
      second[111] = marker;
      BinaryPrimitives.WriteUInt64BigEndian(second[112..120], 0);
      BinaryPrimitives.WriteUInt64BigEndian(second[120..], totalBits);
      Compress64(state, second, 0, true);
    }

    var result = new byte[outputBytes];
    for (var i = 0; i < outputBytes / 8; ++i)
      BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(i * 8, 8), state[i]);
    return result;
  }

  private static void Compress64(ulong[] h, ReadOnlySpan<byte> block, ulong counterBits, bool nullCounter) {
    Span<ulong> m = stackalloc ulong[16];
    Span<ulong> v = stackalloc ulong[16];
    for (var i = 0; i < 16; ++i)
      m[i] = BinaryPrimitives.ReadUInt64BigEndian(block.Slice(i * 8, 8));

    for (var i = 0; i < 8; ++i)
      v[i] = h[i];
    v[8] = Constants64[0]; v[9] = Constants64[1]; v[10] = Constants64[2]; v[11] = Constants64[3];
    v[12] = Constants64[4]; v[13] = Constants64[5]; v[14] = Constants64[6]; v[15] = Constants64[7];
    if (!nullCounter) {
      v[12] ^= counterBits;
      v[13] ^= counterBits;
    }

    for (var round = 0; round < 16; ++round) {
      var r = round % 10;
      G64(v, 0,4, 8,12, m, r,0); G64(v, 1,5, 9,13, m, r,1);
      G64(v, 2,6,10,14, m, r,2); G64(v, 3,7,11,15, m, r,3);
      G64(v, 0,5,10,15, m, r,4); G64(v, 1,6,11,12, m, r,5);
      G64(v, 2,7, 8,13, m, r,6); G64(v, 3,4, 9,14, m, r,7);
    }

    for (var i = 0; i < 8; ++i)
      h[i] ^= v[i] ^ v[i + 8];
  }

  private static void G64(Span<ulong> v, int a, int b, int c, int d, ReadOnlySpan<ulong> m, int round, int pair) {
    var i0 = Sigma[round, pair * 2];
    var i1 = Sigma[round, pair * 2 + 1];
    v[a] = unchecked(v[a] + v[b] + (m[i0] ^ Constants64[i1]));
    v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
    v[c] = unchecked(v[c] + v[d]);
    v[b] = BitOperations.RotateRight(v[b] ^ v[c], 25);
    v[a] = unchecked(v[a] + v[b] + (m[i1] ^ Constants64[i0]));
    v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
    v[c] = unchecked(v[c] + v[d]);
    v[b] = BitOperations.RotateRight(v[b] ^ v[c], 11);
  }
}
