using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>GOST R 34.11-94 using the D-A GOST 28147-89 S-box.</summary>
public static class Gost3411_94 {
  private static readonly byte[,] SBox = {
    {0xA,0x4,0x5,0x6,0x8,0x1,0x3,0x7,0xD,0xC,0xE,0x0,0x9,0x2,0xB,0xF},
    {0x5,0xF,0x4,0x0,0x2,0xD,0xB,0x9,0x1,0x7,0x6,0x3,0xC,0xE,0xA,0x8},
    {0x7,0xF,0xC,0xE,0x9,0x4,0x1,0x0,0x3,0xB,0x5,0x2,0x6,0xA,0x8,0xD},
    {0x4,0xA,0x7,0xC,0x0,0xF,0x2,0x8,0xE,0x1,0x6,0x5,0xD,0xB,0x9,0x3},
    {0x7,0x6,0x4,0xB,0x9,0xC,0x2,0xA,0x1,0x8,0x0,0xE,0xF,0xD,0x3,0x5},
    {0x7,0x6,0x2,0x4,0xD,0x9,0xF,0x0,0xA,0x1,0x5,0xB,0x8,0xE,0xC,0x3},
    {0xD,0xE,0x4,0x1,0x7,0x0,0x5,0xA,0x3,0xC,0x8,0xF,0x6,0x2,0x9,0xB},
    {0x1,0x3,0xA,0x9,0x5,0xB,0x4,0xF,0x8,0x6,0x7,0xE,0xD,0x0,0x2,0xC}
  };

  private static readonly byte[] C2 = [
    0x00,0xFF,0x00,0xFF,0x00,0xFF,0x00,0xFF,
    0xFF,0x00,0xFF,0x00,0xFF,0x00,0xFF,0x00,
    0x00,0xFF,0xFF,0x00,0xFF,0x00,0x00,0xFF,
    0xFF,0x00,0x00,0x00,0xFF,0xFF,0x00,0xFF
  ];

  /// <summary>
  /// Computes the GOST-3411 94 hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) {
    var hash = new byte[32];
    var sum = new byte[32];
    var offset = 0;

    while (offset + 32 <= data.Length) {
      var block = data.Slice(offset, 32);
      AddToSum(sum, block);
      ProcessBlock(hash, block);
      offset += 32;
    }

    if (offset < data.Length) {
      var final = new byte[32];
      data[offset..].CopyTo(final);
      AddToSum(sum, final);
      ProcessBlock(hash, final);
    }

    var length = new byte[32];
    var bitLength = unchecked((ulong)data.Length * 8UL);
    BinaryPrimitives.WriteUInt64LittleEndian(length, bitLength);
    ProcessBlock(hash, length);
    ProcessBlock(hash, sum);
    return hash;
  }

  private static void ProcessBlock(byte[] hash, ReadOnlySpan<byte> message) {
    var u = hash.ToArray();
    var v = message.ToArray();
    var w = new byte[32];
    var s = new byte[32];

    for (var i = 0; i < 4; ++i) {
      for (var j = 0; j < 32; ++j)
        w[j] = (byte)(u[j] ^ v[j]);

      var key = Permute(w);
      Encrypt(key, hash.AsSpan(i * 8, 8), s.AsSpan(i * 8, 8));

      if (i < 3) {
        TransformA(u);
        if (i == 1)
          for (var j = 0; j < 32; ++j)
            u[j] ^= C2[j];

        TransformA(v);
        TransformA(v);
      }
    }

    for (var i = 0; i < 12; ++i)
      TransformFw(s);
    for (var i = 0; i < 32; ++i)
      s[i] ^= message[i];
    TransformFw(s);
    for (var i = 0; i < 32; ++i)
      s[i] ^= hash[i];
    for (var i = 0; i < 61; ++i)
      TransformFw(s);

    s.CopyTo(hash, 0);
  }

  private static byte[] Permute(ReadOnlySpan<byte> input) {
    var key = new byte[32];
    for (var i = 0; i < 8; ++i) {
      key[4 * i] = input[i];
      key[4 * i + 1] = input[8 + i];
      key[4 * i + 2] = input[16 + i];
      key[4 * i + 3] = input[24 + i];
    }
    return key;
  }

  private static void TransformA(byte[] value) {
    Span<byte> tail = stackalloc byte[8];
    for (var i = 0; i < 8; ++i)
      tail[i] = (byte)(value[i] ^ value[i + 8]);
    value.AsSpan(8, 24).CopyTo(value);
    tail.CopyTo(value.AsSpan(24));
  }

  private static void TransformFw(byte[] value) {
    Span<ushort> words = stackalloc ushort[16];
    for (var i = 0; i < 16; ++i)
      words[i] = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(i * 2, 2));

    var last = (ushort)(words[0] ^ words[1] ^ words[2] ^ words[3] ^ words[12] ^ words[15]);
    for (var i = 0; i < 15; ++i)
      words[i] = words[i + 1];
    words[15] = last;

    for (var i = 0; i < 16; ++i)
      BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(i * 2, 2), words[i]);
  }

  private static void AddToSum(byte[] sum, ReadOnlySpan<byte> block) {
    var carry = 0;
    for (var i = 0; i < 32; ++i) {
      var total = sum[i] + block[i] + carry;
      sum[i] = (byte)total;
      carry = total >> 8;
    }
  }

  private static void Encrypt(ReadOnlySpan<byte> keyBytes, ReadOnlySpan<byte> input, Span<byte> output) {
    Span<uint> keys = stackalloc uint[8];
    for (var i = 0; i < 8; ++i)
      keys[i] = BinaryPrimitives.ReadUInt32LittleEndian(keyBytes.Slice(i * 4, 4));

    var n1 = BinaryPrimitives.ReadUInt32LittleEndian(input);
    var n2 = BinaryPrimitives.ReadUInt32LittleEndian(input[4..]);

    for (var cycle = 0; cycle < 3; ++cycle)
      for (var i = 0; i < 8; ++i)
        Round(ref n1, ref n2, keys[i]);

    for (var i = 7; i >= 0; --i)
      Round(ref n1, ref n2, keys[i]);

    BinaryPrimitives.WriteUInt32LittleEndian(output, n2);
    BinaryPrimitives.WriteUInt32LittleEndian(output[4..], n1);
  }

  private static void Round(ref uint n1, ref uint n2, uint key) {
    var old = n1;
    n1 = n2 ^ RoundFunction(n1, key);
    n2 = old;
  }

  private static uint RoundFunction(uint data, uint key) {
    var value = unchecked(data + key);
    uint substituted = 0;
    for (var i = 0; i < 8; ++i) {
      var nibble = (int)((value >> (4 * i)) & 0xFU);
      substituted |= (uint)SBox[i, nibble] << (4 * i);
    }
    return BitOperations.RotateLeft(substituted, 11);
  }
}
