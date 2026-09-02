using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>Whirlpool as standardized in ISO/IEC 10118-3.</summary>
public static class Whirlpool {
  /// <summary>
  /// Gets the supported hash-output sizes, in bits.
  /// </summary>
  public static global::System.Collections.Generic.IReadOnlyList<global::Hawkynt.Algorithms.Hashing.HashSizeRange> SupportedHashSizes => global::Hawkynt.Algorithms.Hashing.HashSizeSets.Bits512;

  private static readonly ulong[] RoundConstants = [
    0x1823C6E887B8014FUL,0x36A6D2F5796F9152UL,0x60BC9B8EA30C7B35UL,0x1DE0D7C22E4BFE57UL,0x157737E59FF04ADAUL,
    0x58C9290AB1A06B85UL,0xBD5D10F4CB3E0567UL,0xE427418BA77D95D8UL,0xFBEE7C66DD17479EUL,0xCA2DBF07AD5A8333UL
  ];

  private static readonly byte[] SBox = [
    0x18,0x23,0xC6,0xE8,0x87,0xB8,0x01,0x4F,0x36,0xA6,0xD2,0xF5,0x79,0x6F,0x91,0x52,
    0x60,0xBC,0x9B,0x8E,0xA3,0x0C,0x7B,0x35,0x1D,0xE0,0xD7,0xC2,0x2E,0x4B,0xFE,0x57,
    0x15,0x77,0x37,0xE5,0x9F,0xF0,0x4A,0xDA,0x58,0xC9,0x29,0x0A,0xB1,0xA0,0x6B,0x85,
    0xBD,0x5D,0x10,0xF4,0xCB,0x3E,0x05,0x67,0xE4,0x27,0x41,0x8B,0xA7,0x7D,0x95,0xD8,
    0xFB,0xEE,0x7C,0x66,0xDD,0x17,0x47,0x9E,0xCA,0x2D,0xBF,0x07,0xAD,0x5A,0x83,0x33,
    0x63,0x02,0xAA,0x71,0xC8,0x19,0x49,0xD9,0xF2,0xE3,0x5B,0x88,0x9A,0x26,0x32,0xB0,
    0xE9,0x0F,0xD5,0x80,0xBE,0xCD,0x34,0x48,0xFF,0x7A,0x90,0x5F,0x20,0x68,0x1A,0xAE,
    0xB4,0x54,0x93,0x22,0x64,0xF1,0x73,0x12,0x40,0x08,0xC3,0xEC,0xDB,0xA1,0x8D,0x3D,
    0x97,0x00,0xCF,0x2B,0x76,0x82,0xD6,0x1B,0xB5,0xAF,0x6A,0x50,0x45,0xF3,0x30,0xEF,
    0x3F,0x55,0xA2,0xEA,0x65,0xBA,0x2F,0xC0,0xDE,0x1C,0xFD,0x4D,0x92,0x75,0x06,0x8A,
    0xB2,0xE6,0x0E,0x1F,0x62,0xD4,0xA8,0x96,0xF9,0xC5,0x25,0x59,0x84,0x72,0x39,0x4C,
    0x5E,0x78,0x38,0x8C,0xD1,0xA5,0xE2,0x61,0xB3,0x21,0x9C,0x1E,0x43,0xC7,0xFC,0x04,
    0x51,0x99,0x6D,0x0D,0xFA,0xDF,0x7E,0x24,0x3B,0xAB,0xCE,0x11,0x8F,0x4E,0xB7,0xEB,
    0x3C,0x81,0x94,0xF7,0xB9,0x13,0x2C,0xD3,0xE7,0x6E,0xC4,0x03,0x56,0x44,0x7F,0xA9,
    0x2A,0xBB,0xC1,0x53,0xDC,0x0B,0x9D,0x6C,0x31,0x74,0xF6,0x46,0xAC,0x89,0x14,0xE1,
    0x16,0x3A,0x69,0x09,0x70,0xB6,0xD0,0xED,0xCC,0x42,0x98,0xA4,0x28,0x5C,0xF8,0x86
  ];

  private static readonly ulong[] Table = BuildTable();

  /// <summary>
  /// Computes the Whirlpool hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    Span<ulong> state = stackalloc ulong[8];
    var offset = 0;
    while (offset + 64 <= data.Length) {
      Compress(state, data.Slice(offset, 64));
      offset += 64;
    }

    var remaining = data.Length - offset;
    var finalBlocks = remaining + 1 > 56 ? 128 : 64;
    var final = new byte[finalBlocks];
    data[offset..].CopyTo(final);
    final[remaining] = 0x80;
    BinaryPrimitives.WriteUInt64BigEndian(final.AsSpan(finalBlocks - 8), checked((ulong)data.Length * 8));
    for (var i = 0; i < finalBlocks; i += 64)
      Compress(state, final.AsSpan(i, 64));

    var result = new byte[64];
    for (var i = 0; i < 8; ++i)
      BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(i * 8, 8), state[i]);
    return result;
  }

  private static void Compress(Span<ulong> state, ReadOnlySpan<byte> block) {
    Span<ulong> message = stackalloc ulong[8];
    Span<ulong> key = stackalloc ulong[8];
    Span<ulong> nextKey = stackalloc ulong[8];
    Span<ulong> cipher = stackalloc ulong[8];
    Span<ulong> nextCipher = stackalloc ulong[8];

    for (var i = 0; i < 8; ++i) {
      message[i] = BinaryPrimitives.ReadUInt64BigEndian(block.Slice(i * 8, 8));
      key[i] = state[i];
      cipher[i] = message[i] ^ key[i];
    }

    for (var round = 0; round < 10; ++round) {
      for (var i = 0; i < 8; ++i)
        nextKey[i] = Operation(key[i], key[(i + 7) & 7], key[(i + 6) & 7], key[(i + 5) & 7], key[(i + 4) & 7], key[(i + 3) & 7], key[(i + 2) & 7], key[(i + 1) & 7]);
      nextKey[0] ^= RoundConstants[round];
      nextKey.CopyTo(key);

      for (var i = 0; i < 8; ++i)
        nextCipher[i] = Operation(cipher[i], cipher[(i + 7) & 7], cipher[(i + 6) & 7], cipher[(i + 5) & 7], cipher[(i + 4) & 7], cipher[(i + 3) & 7], cipher[(i + 2) & 7], cipher[(i + 1) & 7]) ^ key[i];
      nextCipher.CopyTo(cipher);
    }

    for (var i = 0; i < 8; ++i)
      state[i] ^= cipher[i] ^ message[i];
  }

  private static ulong Operation(ulong x0, ulong x1, ulong x2, ulong x3, ulong x4, ulong x5, ulong x6, ulong x7) =>
    Table[ByteAt(x0, 0)] ^
    BitOperations.RotateRight(Table[ByteAt(x1, 1)], 8) ^
    BitOperations.RotateRight(Table[ByteAt(x2, 2)], 16) ^
    BitOperations.RotateRight(Table[ByteAt(x3, 3)], 24) ^
    BitOperations.RotateRight(Table[ByteAt(x4, 4)], 32) ^
    BitOperations.RotateRight(Table[ByteAt(x5, 5)], 40) ^
    BitOperations.RotateRight(Table[ByteAt(x6, 6)], 48) ^
    BitOperations.RotateRight(Table[ByteAt(x7, 7)], 56);

  private static int ByteAt(ulong word, int position) => (int)((word >> ((7 - position) * 8)) & 0xFF);

  private static ulong[] BuildTable() {
    var table = new ulong[256];
    for (var i = 0; i < table.Length; ++i) {
      var s = SBox[i];
      var s2 = Multiply(s, 2);
      var s4 = Multiply(s, 4);
      var s5 = (byte)(s4 ^ s);
      var s8 = Multiply(s, 8);
      var s9 = (byte)(s8 ^ s);
      table[i] = ((ulong)s << 56) | ((ulong)s << 48) | ((ulong)s4 << 40) | ((ulong)s << 32) |
                 ((ulong)s8 << 24) | ((ulong)s5 << 16) | ((ulong)s2 << 8) | s9;
    }
    return table;
  }

  private static byte Multiply(byte value, int factor) {
    var result = 0;
    var a = (int)value;
    var b = factor;
    while (b != 0) {
      if ((b & 1) != 0)
        result ^= a;
      a <<= 1;
      if ((a & 0x100) != 0)
        a ^= 0x11D;
      b >>= 1;
    }
    return (byte)result;
  }
}
