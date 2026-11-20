using System.Buffers.Binary;
using System.Numerics;

namespace Compression.Core.Crypto;

/// <summary>
/// Triple DES in the encrypt-decrypt-encrypt order of NIST SP 800-67 (the DES rounds of FIPS 46-3),
/// 64-bit blocks under a three-key (24-byte) or two-key (16-byte, third key the first) bundle, in this
/// library's own code, so that no format's Triple DES depends on what the operating system offers.
/// </summary>
/// <remarks>
/// Converted from the Bouncy Castle C# engines <c>DesEngine</c> and <c>DesEdeEngine</c> (MIT; see
/// THIRD-PARTY-NOTICE.BouncyCastle.txt), which follow Richard Outerbridge's D3DES: the key schedule of ANSI
/// X3.92 into 32 subkey words, and the rounds over the eight combined S-box and permutation tables SP1-SP8
/// with the initial and final permutations as swaps of bit groups. The tables are its tables. Unlike .NET's
/// TripleDES, a bundle whose keys repeat is not refused: that is single DES, which is what the bytes say.
/// </remarks>
public sealed class TripleDes {
  /// <summary>The block size in bytes.</summary>
  public const int BlockSize = 8;

  private readonly int[] _encrypt1, _decrypt2, _encrypt3;
  private readonly int[] _decrypt3, _encrypt2, _decrypt1;

  /// <summary>Expands a 24-byte (three-key) or 16-byte (two-key) bundle for both directions.</summary>
  public TripleDes(ReadOnlySpan<byte> key) {
    if (key.Length is not (16 or 24))
      throw new ArgumentException("Triple DES takes a 16- or 24-byte key bundle.", nameof(key));
    var k1 = key[..8];
    var k2 = key[8..16];
    var k3 = key.Length == 24 ? key[16..24] : k1;
    this._encrypt1 = WorkingKey(true, k1);
    this._decrypt1 = WorkingKey(false, k1);
    this._encrypt2 = WorkingKey(true, k2);
    this._decrypt2 = WorkingKey(false, k2);
    this._encrypt3 = WorkingKey(true, k3);
    this._decrypt3 = WorkingKey(false, k3);
  }

  /// <summary>Encrypts one 8-byte block in place.</summary>
  public void Encrypt(Span<byte> block) => Run(block, this._encrypt1, this._decrypt2, this._encrypt3);

  /// <summary>Decrypts one 8-byte block in place.</summary>
  public void Decrypt(Span<byte> block) => Run(block, this._decrypt3, this._encrypt2, this._decrypt1);

  private static void Run(Span<byte> block, int[] first, int[] second, int[] third) {
    if (block.Length < BlockSize)
      throw new ArgumentException("Triple DES works on 8-byte blocks.", nameof(block));
    var hi = BinaryPrimitives.ReadUInt32BigEndian(block);
    var lo = BinaryPrimitives.ReadUInt32BigEndian(block[4..]);
    Des(first, ref hi, ref lo);
    Des(second, ref hi, ref lo);
    Des(third, ref hi, ref lo);
    BinaryPrimitives.WriteUInt32BigEndian(block, hi);
    BinaryPrimitives.WriteUInt32BigEndian(block[4..], lo);
  }

  /// <summary>The 32 subkey words of one DES key, in the order one direction uses them.</summary>
  private static int[] WorkingKey(bool encrypting, ReadOnlySpan<byte> key) {
    var newKey = new int[32];
    Span<bool> pc1m = stackalloc bool[56];
    Span<bool> pcr = stackalloc bool[56];
    for (var j = 0; j < 56; ++j) {
      int l = Pc1[j];
      pc1m[j] = (key[l >> 3] & ByteBit[l & 7]) != 0;
    }
    for (var i = 0; i < 16; ++i) {
      var m = (encrypting ? i : 15 - i) << 1;
      var n = m + 1;
      newKey[m] = newKey[n] = 0;
      for (var j = 0; j < 28; ++j) {
        var l = j + TotalRotations[i];
        pcr[j] = pc1m[l < 28 ? l : l - 28];
      }
      for (var j = 28; j < 56; ++j) {
        var l = j + TotalRotations[i];
        pcr[j] = pc1m[l < 56 ? l : l - 28];
      }
      for (var j = 0; j < 24; ++j) {
        if (pcr[Pc2[j]])
          newKey[m] |= BigByte[j];
        if (pcr[Pc2[j + 24]])
          newKey[n] |= BigByte[j];
      }
    }
    for (var i = 0; i < 32; i += 2) {
      int i1 = newKey[i], i2 = newKey[i + 1];
      newKey[i] = (int)((uint)((i1 & 0x00fc0000) << 6) | (uint)((i1 & 0x00000fc0) << 10) | ((uint)(i2 & 0x00fc0000) >> 10) | ((uint)(i2 & 0x00000fc0) >> 6));
      newKey[i + 1] = (int)((uint)((i1 & 0x0003f000) << 12) | (uint)((i1 & 0x0000003f) << 16) | ((uint)(i2 & 0x0003f000) >> 4) | (uint)(i2 & 0x0000003f));
    }
    return newKey;
  }

  /// <summary>One DES pass: the initial permutation, sixteen rounds, the final permutation.</summary>
  private static void Des(int[] key, ref uint hi32, ref uint lo32) {
    var left = hi32;
    var right = lo32;
    var work = ((left >> 4) ^ right) & 0x0f0f0f0f;
    right ^= work;
    left ^= work << 4;
    work = ((left >> 16) ^ right) & 0x0000ffff;
    right ^= work;
    left ^= work << 16;
    work = ((right >> 2) ^ left) & 0x33333333;
    left ^= work;
    right ^= work << 2;
    work = ((right >> 8) ^ left) & 0x00ff00ff;
    left ^= work;
    right ^= work << 8;
    right = BitOperations.RotateLeft(right, 1);
    work = (left ^ right) & 0xaaaaaaaa;
    left ^= work;
    right ^= work;
    left = BitOperations.RotateLeft(left, 1);

    for (var round = 0; round < 8; ++round) {
      left ^= F(right, key[round * 4], key[round * 4 + 1]);
      right ^= F(left, key[round * 4 + 2], key[round * 4 + 3]);
    }

    right = BitOperations.RotateRight(right, 1);
    work = (left ^ right) & 0xaaaaaaaa;
    left ^= work;
    right ^= work;
    left = BitOperations.RotateRight(left, 1);
    work = ((left >> 8) ^ right) & 0x00ff00ff;
    right ^= work;
    left ^= work << 8;
    work = ((left >> 2) ^ right) & 0x33333333;
    right ^= work;
    left ^= work << 2;
    work = ((right >> 16) ^ left) & 0x0000ffff;
    left ^= work;
    right ^= work << 16;
    work = ((right >> 4) ^ left) & 0x0f0f0f0f;
    left ^= work;
    right ^= work << 4;
    hi32 = right;
    lo32 = left;
  }

  /// <summary>The round function over two subkey words: expansion, S-boxes and P in the SP tables.</summary>
  private static uint F(uint half, int key0, int key1) {
    var work = BitOperations.RotateRight(half, 4) ^ (uint)key0;
    var value = Sp7[work & 0x3f] | Sp5[(work >> 8) & 0x3f] | Sp3[(work >> 16) & 0x3f] | Sp1[(work >> 24) & 0x3f];
    work = half ^ (uint)key1;
    return value | Sp8[work & 0x3f] | Sp6[(work >> 8) & 0x3f] | Sp4[(work >> 16) & 0x3f] | Sp2[(work >> 24) & 0x3f];
  }

  private static readonly byte[] ByteBit = [
    128, 64, 32, 16, 8, 4, 2, 1
  ];

  private static readonly int[] BigByte = [
    0x800000, 0x400000, 0x200000, 0x100000, 0x080000, 0x040000, 0x020000, 0x010000,
    0x008000, 0x004000, 0x002000, 0x001000, 0x000800, 0x000400, 0x000200, 0x000100,
    0x000080, 0x000040, 0x000020, 0x000010, 0x000008, 0x000004, 0x000002, 0x000001
  ];

  private static readonly byte[] Pc1 = [
    56, 48, 40, 32, 24, 16, 8, 0, 57, 49, 41, 33, 25, 17,
    9, 1, 58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35,
    62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21,
    13, 5, 60, 52, 44, 36, 28, 20, 12, 4, 27, 19, 11, 3
  ];

  private static readonly byte[] TotalRotations = [
    1, 2, 4, 6, 8, 10, 12, 14,
    15, 17, 19, 21, 23, 25, 27, 28
  ];

  private static readonly byte[] Pc2 = [
    13, 16, 10, 23, 0, 4, 2, 27, 14, 5, 20, 9,
    22, 18, 11, 3, 25, 7, 15, 6, 26, 19, 12, 1,
    40, 51, 30, 36, 46, 54, 29, 39, 50, 44, 32, 47,
    43, 48, 38, 55, 33, 52, 45, 41, 49, 35, 28, 31
  ];

  private static readonly uint[] Sp1 = [
    0x01010400, 0x00000000, 0x00010000, 0x01010404, 0x01010004, 0x00010404, 0x00000004, 0x00010000,
    0x00000400, 0x01010400, 0x01010404, 0x00000400, 0x01000404, 0x01010004, 0x01000000, 0x00000004,
    0x00000404, 0x01000400, 0x01000400, 0x00010400, 0x00010400, 0x01010000, 0x01010000, 0x01000404,
    0x00010004, 0x01000004, 0x01000004, 0x00010004, 0x00000000, 0x00000404, 0x00010404, 0x01000000,
    0x00010000, 0x01010404, 0x00000004, 0x01010000, 0x01010400, 0x01000000, 0x01000000, 0x00000400,
    0x01010004, 0x00010000, 0x00010400, 0x01000004, 0x00000400, 0x00000004, 0x01000404, 0x00010404,
    0x01010404, 0x00010004, 0x01010000, 0x01000404, 0x01000004, 0x00000404, 0x00010404, 0x01010400,
    0x00000404, 0x01000400, 0x01000400, 0x00000000, 0x00010004, 0x00010400, 0x00000000, 0x01010004
  ];

  private static readonly uint[] Sp2 = [
    0x80108020, 0x80008000, 0x00008000, 0x00108020, 0x00100000, 0x00000020, 0x80100020, 0x80008020,
    0x80000020, 0x80108020, 0x80108000, 0x80000000, 0x80008000, 0x00100000, 0x00000020, 0x80100020,
    0x00108000, 0x00100020, 0x80008020, 0x00000000, 0x80000000, 0x00008000, 0x00108020, 0x80100000,
    0x00100020, 0x80000020, 0x00000000, 0x00108000, 0x00008020, 0x80108000, 0x80100000, 0x00008020,
    0x00000000, 0x00108020, 0x80100020, 0x00100000, 0x80008020, 0x80100000, 0x80108000, 0x00008000,
    0x80100000, 0x80008000, 0x00000020, 0x80108020, 0x00108020, 0x00000020, 0x00008000, 0x80000000,
    0x00008020, 0x80108000, 0x00100000, 0x80000020, 0x00100020, 0x80008020, 0x80000020, 0x00100020,
    0x00108000, 0x00000000, 0x80008000, 0x00008020, 0x80000000, 0x80100020, 0x80108020, 0x00108000
  ];

  private static readonly uint[] Sp3 = [
    0x00000208, 0x08020200, 0x00000000, 0x08020008, 0x08000200, 0x00000000, 0x00020208, 0x08000200,
    0x00020008, 0x08000008, 0x08000008, 0x00020000, 0x08020208, 0x00020008, 0x08020000, 0x00000208,
    0x08000000, 0x00000008, 0x08020200, 0x00000200, 0x00020200, 0x08020000, 0x08020008, 0x00020208,
    0x08000208, 0x00020200, 0x00020000, 0x08000208, 0x00000008, 0x08020208, 0x00000200, 0x08000000,
    0x08020200, 0x08000000, 0x00020008, 0x00000208, 0x00020000, 0x08020200, 0x08000200, 0x00000000,
    0x00000200, 0x00020008, 0x08020208, 0x08000200, 0x08000008, 0x00000200, 0x00000000, 0x08020008,
    0x08000208, 0x00020000, 0x08000000, 0x08020208, 0x00000008, 0x00020208, 0x00020200, 0x08000008,
    0x08020000, 0x08000208, 0x00000208, 0x08020000, 0x00020208, 0x00000008, 0x08020008, 0x00020200
  ];

  private static readonly uint[] Sp4 = [
    0x00802001, 0x00002081, 0x00002081, 0x00000080, 0x00802080, 0x00800081, 0x00800001, 0x00002001,
    0x00000000, 0x00802000, 0x00802000, 0x00802081, 0x00000081, 0x00000000, 0x00800080, 0x00800001,
    0x00000001, 0x00002000, 0x00800000, 0x00802001, 0x00000080, 0x00800000, 0x00002001, 0x00002080,
    0x00800081, 0x00000001, 0x00002080, 0x00800080, 0x00002000, 0x00802080, 0x00802081, 0x00000081,
    0x00800080, 0x00800001, 0x00802000, 0x00802081, 0x00000081, 0x00000000, 0x00000000, 0x00802000,
    0x00002080, 0x00800080, 0x00800081, 0x00000001, 0x00802001, 0x00002081, 0x00002081, 0x00000080,
    0x00802081, 0x00000081, 0x00000001, 0x00002000, 0x00800001, 0x00002001, 0x00802080, 0x00800081,
    0x00002001, 0x00002080, 0x00800000, 0x00802001, 0x00000080, 0x00800000, 0x00002000, 0x00802080
  ];

  private static readonly uint[] Sp5 = [
    0x00000100, 0x02080100, 0x02080000, 0x42000100, 0x00080000, 0x00000100, 0x40000000, 0x02080000,
    0x40080100, 0x00080000, 0x02000100, 0x40080100, 0x42000100, 0x42080000, 0x00080100, 0x40000000,
    0x02000000, 0x40080000, 0x40080000, 0x00000000, 0x40000100, 0x42080100, 0x42080100, 0x02000100,
    0x42080000, 0x40000100, 0x00000000, 0x42000000, 0x02080100, 0x02000000, 0x42000000, 0x00080100,
    0x00080000, 0x42000100, 0x00000100, 0x02000000, 0x40000000, 0x02080000, 0x42000100, 0x40080100,
    0x02000100, 0x40000000, 0x42080000, 0x02080100, 0x40080100, 0x00000100, 0x02000000, 0x42080000,
    0x42080100, 0x00080100, 0x42000000, 0x42080100, 0x02080000, 0x00000000, 0x40080000, 0x42000000,
    0x00080100, 0x02000100, 0x40000100, 0x00080000, 0x00000000, 0x40080000, 0x02080100, 0x40000100
  ];

  private static readonly uint[] Sp6 = [
    0x20000010, 0x20400000, 0x00004000, 0x20404010, 0x20400000, 0x00000010, 0x20404010, 0x00400000,
    0x20004000, 0x00404010, 0x00400000, 0x20000010, 0x00400010, 0x20004000, 0x20000000, 0x00004010,
    0x00000000, 0x00400010, 0x20004010, 0x00004000, 0x00404000, 0x20004010, 0x00000010, 0x20400010,
    0x20400010, 0x00000000, 0x00404010, 0x20404000, 0x00004010, 0x00404000, 0x20404000, 0x20000000,
    0x20004000, 0x00000010, 0x20400010, 0x00404000, 0x20404010, 0x00400000, 0x00004010, 0x20000010,
    0x00400000, 0x20004000, 0x20000000, 0x00004010, 0x20000010, 0x20404010, 0x00404000, 0x20400000,
    0x00404010, 0x20404000, 0x00000000, 0x20400010, 0x00000010, 0x00004000, 0x20400000, 0x00404010,
    0x00004000, 0x00400010, 0x20004010, 0x00000000, 0x20404000, 0x20000000, 0x00400010, 0x20004010
  ];

  private static readonly uint[] Sp7 = [
    0x00200000, 0x04200002, 0x04000802, 0x00000000, 0x00000800, 0x04000802, 0x00200802, 0x04200800,
    0x04200802, 0x00200000, 0x00000000, 0x04000002, 0x00000002, 0x04000000, 0x04200002, 0x00000802,
    0x04000800, 0x00200802, 0x00200002, 0x04000800, 0x04000002, 0x04200000, 0x04200800, 0x00200002,
    0x04200000, 0x00000800, 0x00000802, 0x04200802, 0x00200800, 0x00000002, 0x04000000, 0x00200800,
    0x04000000, 0x00200800, 0x00200000, 0x04000802, 0x04000802, 0x04200002, 0x04200002, 0x00000002,
    0x00200002, 0x04000000, 0x04000800, 0x00200000, 0x04200800, 0x00000802, 0x00200802, 0x04200800,
    0x00000802, 0x04000002, 0x04200802, 0x04200000, 0x00200800, 0x00000000, 0x00000002, 0x04200802,
    0x00000000, 0x00200802, 0x04200000, 0x00000800, 0x04000002, 0x04000800, 0x00000800, 0x00200002
  ];

  private static readonly uint[] Sp8 = [
    0x10001040, 0x00001000, 0x00040000, 0x10041040, 0x10000000, 0x10001040, 0x00000040, 0x10000000,
    0x00040040, 0x10040000, 0x10041040, 0x00041000, 0x10041000, 0x00041040, 0x00001000, 0x00000040,
    0x10040000, 0x10000040, 0x10001000, 0x00001040, 0x00041000, 0x00040040, 0x10040040, 0x10041000,
    0x00001040, 0x00000000, 0x00000000, 0x10040040, 0x10000040, 0x10001000, 0x00041040, 0x00040000,
    0x00041040, 0x00040000, 0x10041000, 0x00001000, 0x00000040, 0x10040040, 0x00001000, 0x00041040,
    0x10001000, 0x00000040, 0x10000040, 0x10040000, 0x10040040, 0x10000000, 0x00040000, 0x10001040,
    0x00000000, 0x10041040, 0x00040040, 0x10000040, 0x10040000, 0x10001000, 0x10001040, 0x00000000,
    0x10041040, 0x00041000, 0x00041000, 0x00001040, 0x00001040, 0x00040040, 0x10000000, 0x10041000
  ];
}
