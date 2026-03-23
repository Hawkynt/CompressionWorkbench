namespace Compression.Core.Checksums;

/// <summary>
/// MD5 hash function (RFC 1321). Produces 16-byte (128-bit) message digests.
/// Used for legacy key derivation in formats like SQX.
/// </summary>
public static class Md5 {
  /// <summary>
  /// Computes the MD5 hash of the given data.
  /// </summary>
  /// <param name="data">The data to hash.</param>
  /// <returns>The 16-byte MD5 hash.</returns>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    uint a0 = 0x67452301;
    var b0 = 0xEFCDAB89;
    var c0 = 0x98BADCFE;
    uint d0 = 0x10325476;

    var bitLen = (long)data.Length * 8;

    // Pad: append 0x80, then zeros, then 64-bit length (LE)
    var padLen = (56 - (data.Length + 1) % 64 + 64) % 64 + 1;
    var padded = new byte[data.Length + padLen + 8];
    data.CopyTo(padded);
    padded[data.Length] = 0x80;
    padded[padded.Length - 8] = (byte)(bitLen);
    padded[padded.Length - 7] = (byte)(bitLen >> 8);
    padded[padded.Length - 6] = (byte)(bitLen >> 16);
    padded[padded.Length - 5] = (byte)(bitLen >> 24);
    padded[padded.Length - 4] = (byte)(bitLen >> 32);
    padded[padded.Length - 3] = (byte)(bitLen >> 40);
    padded[padded.Length - 2] = (byte)(bitLen >> 48);
    padded[padded.Length - 1] = (byte)(bitLen >> 56);

    // Process each 64-byte block
    for (var offset = 0; offset < padded.Length; offset += 64) {
      var m = new uint[16];
      for (var i = 0; i < 16; ++i)
        m[i] = BitConverter.ToUInt32(padded, offset + i * 4);

      uint a = a0, b = b0, c = c0, d = d0;

      // Round 1
      for (var i = 0; i < 16; ++i) {
        var f = (b & c) | (~b & d);
        var g = (uint)i;
        var temp = d; d = c; c = b;
        b += RotateLeft(a + f + K[i] + m[g], S1[i]);
        a = temp;
      }

      // Round 2
      for (var i = 16; i < 32; ++i) {
        var f = (d & b) | (~d & c);
        var g = (uint)((5 * (i - 16) + 1) % 16);
        var temp = d; d = c; c = b;
        b += RotateLeft(a + f + K[i] + m[g], S2[i - 16]);
        a = temp;
      }

      // Round 3
      for (var i = 32; i < 48; ++i) {
        var f = b ^ c ^ d;
        var g = (uint)((3 * (i - 32) + 5) % 16);
        var temp = d; d = c; c = b;
        b += RotateLeft(a + f + K[i] + m[g], S3[i - 32]);
        a = temp;
      }

      // Round 4
      for (var i = 48; i < 64; ++i) {
        var f = c ^ (b | ~d);
        var g = (uint)((7 * (i - 48)) % 16);
        var temp = d; d = c; c = b;
        b += RotateLeft(a + f + K[i] + m[g], S4[i - 48]);
        a = temp;
      }

      a0 += a; b0 += b; c0 += c; d0 += d;
    }

    var result = new byte[16];
    BitConverter.TryWriteBytes(result.AsSpan(0), a0);
    BitConverter.TryWriteBytes(result.AsSpan(4), b0);
    BitConverter.TryWriteBytes(result.AsSpan(8), c0);
    BitConverter.TryWriteBytes(result.AsSpan(12), d0);
    return result;
  }

  private static uint RotateLeft(uint x, int n) => (x << n) | (x >> (32 - n));

  private static readonly int[] S1 = [7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22];
  private static readonly int[] S2 = [5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20];
  private static readonly int[] S3 = [4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23];
  private static readonly int[] S4 = [6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21];

  private static readonly uint[] K = [
    0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee,
    0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
    0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be,
    0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
    0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa,
    0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
    0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed,
    0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
    0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c,
    0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
    0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05,
    0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
    0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039,
    0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
    0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1,
    0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391
  ];
}
