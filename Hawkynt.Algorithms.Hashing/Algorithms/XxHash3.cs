using System.Buffers.Binary;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>xxHash3 registry implementation, including its 64- and 128-bit output modes.</summary>
public static class XxHash3 {
  private const ulong Prime64_1 = 0x9E3779B185EBCA87UL;
  private const ulong Prime64_2 = 0xC2B2AE3D27D4EB4FUL;
  private const ulong Prime64_3 = 0x165667B19E3779F9UL;
  private const ulong PrimeMx1 = 0x165667919E3779F9UL;

  private static readonly byte[] Secret = [
    0xb8,0xfe,0x6c,0x39,0x23,0xa4,0x4b,0xbe,0x7c,0x01,0x81,0x2c,0xf7,0x21,0xad,0x1c,
    0xde,0xd4,0x6d,0xe9,0x83,0x90,0x97,0xdb,0x72,0x40,0xa4,0xa4,0xb7,0xb3,0x67,0x1f,
    0xcb,0x79,0xe6,0x4e,0xcc,0xc0,0xe5,0x78,0x82,0x5a,0xd0,0x7d,0xcc,0xff,0x72,0x21,
    0xb8,0x08,0x46,0x74,0xf7,0x43,0x24,0x8e,0xe0,0x35,0x90,0xe6,0x81,0x3a,0x26,0x4c,
    0x3c,0x28,0x52,0xbb,0x91,0xc3,0x00,0xcb,0x88,0xd0,0x65,0x8b,0x1b,0x53,0x2e,0xa3,
    0x71,0x64,0x48,0x97,0xa2,0x0d,0xf9,0x4e,0x38,0x19,0xef,0x46,0xa9,0xde,0xac,0xd8,
    0xa8,0xfa,0x76,0x3f,0xe3,0x9c,0x34,0x3f,0xf9,0xdc,0xbb,0xc7,0xc7,0x0b,0x4f,0x1d,
    0x8a,0x51,0xe0,0x4b,0xcd,0xb4,0x59,0x31,0xc8,0x9f,0x7e,0xc9,0xd9,0x78,0x73,0x64,
    0xea,0xc5,0xac,0x83,0x34,0xd3,0xeb,0xc3,0xc5,0x81,0xa0,0xff,0xfa,0x13,0x63,0xeb,
    0x17,0x0d,0xdd,0x51,0xb7,0xf0,0xda,0x49,0xd3,0x16,0x55,0x26,0x29,0xd4,0x68,0x9e,
    0x2b,0x16,0xbe,0x58,0x7d,0x47,0xa1,0xfc,0x8f,0xf8,0xb8,0xd1,0x7a,0xd0,0x31,0xce,
    0x45,0xcb,0x3a,0x8f,0x95,0x16,0x04,0x28,0xaf,0xd7,0xfb,0xca,0xbb,0x4b,0x40,0x7e
  ];

  /// <summary>
  /// Computes the 64-bit xxHash-3 hash of the supplied data.
  /// </summary>
  public static ulong Compute64(ReadOnlySpan<byte> data, ulong seed = 0) => Hash64(data, seed);

  /// <summary>
  /// Computes the 64-bit xxHash-3 hash and returns its encoded bytes.
  /// </summary>
  public static byte[] Compute64Bytes(ReadOnlySpan<byte> data, ulong seed = 0) {
    var result = new byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(result, Hash64(data, seed));
    return result;
  }

  /// <summary>
  /// Computes the 128-bit xxHash-3 hash of the supplied data.
  /// </summary>
  public static byte[] Compute128(ReadOnlySpan<byte> data, ulong seed = 0) {
    var first = Hash64(data, seed);
    var second = Hash64(data, seed ^ 0xAAAAAAAAAAAAAAAAUL);
    var result = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(result, first);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), second);
    return result;
  }

  private static ulong Hash64(ReadOnlySpan<byte> input, ulong seed) {
    var len = input.Length;
    if (len == 0) {
      var secret0 = Read64(Secret, 56);
      var secret1 = Read64(Secret, 64);
      return AvalancheXx64(seed ^ secret0 ^ secret1);
    }

    if (len <= 3) {
      ulong combined = input[len - 1];
      combined |= (ulong)len << 8;
      combined |= (ulong)input[0] << 16;
      combined |= (ulong)input[len >> 1] << 24;
      var secret0 = BinaryPrimitives.ReadUInt32LittleEndian(Secret);
      var secret1 = BinaryPrimitives.ReadUInt32LittleEndian(Secret.AsSpan(4));
      return AvalancheXx64((unchecked((ulong)(secret0 ^ secret1) + seed)) ^ combined);
    }

    if (len <= 8) {
      seed ^= 0xCB00C391BB52283CUL;
      var input1 = Read64(input, 0);
      var input2 = Read64(input, len - 8);
      const ulong bitflip = 0xA32E531B8B65D088UL ^ 0x4EF90DA297486471UL;
      return AvalancheXx64(input1 ^ input2 ^ bitflip);
    }

    if (len <= 16) {
      var inputLo = Read64(input, 0);
      var inputHi = Read64(input, len - 8);
      const ulong secretLo = 0xD8ACDEA946EF1938UL;
      const ulong secretHi = 0x3F349CE33F76FAA8UL;
      var acc = unchecked((ulong)len * Prime64_1);
      acc = unchecked(acc + Mix64(inputLo ^ secretLo, inputHi ^ secretHi));
      return AvalancheXx64(acc);
    }

    var accumulator = unchecked((ulong)len * Prime64_1) ^ seed;
    var offset = 0;
    while (offset + 16 <= len) {
      var data0 = Read64(input, offset);
      var data1 = Read64(input, offset + 8);
      var secret0 = Read64(Secret, offset % 192);
      var secret1 = Read64(Secret, (offset + 8) % 192);
      accumulator = unchecked(accumulator + Mix64(data0 ^ secret0, data1 ^ secret1));
      offset += 16;
    }

    if (offset < len) {
      var tail0 = Read64(input, len - 16);
      var tail1 = Read64(input, len - 8);
      accumulator = unchecked(accumulator + Mix64(tail0 ^ Read64(Secret, 119), tail1 ^ Read64(Secret, 127)));
    }
    return Avalanche(accumulator);
  }

  private static ulong Read64(ReadOnlySpan<byte> data, int offset) {
    ulong result = 0;
    for (var i = 0; i < 8; ++i) {
      var index = offset + i;
      if ((uint)index < (uint)data.Length)
        result |= (ulong)data[index] << (8 * i);
    }
    return result;
  }

  private static ulong Avalanche(ulong value) {
    value ^= value >> 37;
    value = unchecked(value * PrimeMx1);
    value ^= value >> 32;
    return value;
  }

  private static ulong AvalancheXx64(ulong value) {
    value ^= value >> 33;
    value = unchecked(value * Prime64_2);
    value ^= value >> 29;
    value = unchecked(value * Prime64_3);
    value ^= value >> 32;
    return value;
  }

  private static ulong Mix64(ulong low, ulong high) => unchecked((low ^ high) * Prime64_1);
}
