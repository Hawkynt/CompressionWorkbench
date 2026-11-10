using System.Buffers.Binary;

namespace Compression.Core.Crypto;

/// <summary>
/// The Kuznyechik block cipher (GOST R 34.12-2015), 128-bit blocks and a 256-bit key, in the byte
/// order of RFC 7801's examples.
/// </summary>
/// <remarks>
/// <para>
/// Written from RFC 7801, "GOST R 34.12-2015: Block Cipher 'Kuznyechik'": the substitution Pi and
/// its inverse (section 4.1), the linear map l over GF(2^8) mod x^8+x^7+x^6+x+1 (section 4.2), the
/// transformations S, R and L (section 4.3), the key schedule with round constants
/// C_i = L(Vec_128(i)) and the Feistel steps F (section 4.4), and the nine LSX rounds plus a final
/// key addition (section 4.5). Pi, Pi^-1 and the coefficients of l are the RFC's. The coefficient
/// list in the RFC's printed formula repeats a_15 where a_14 is meant; the coefficients themselves
/// (148, 32, 133, 16, 194, 192, 1, 251, 1, 192, 194, 16, 133, 32, 148, 1 for a_15 down to a_0) are
/// confirmed by the R and L examples of section 5.
/// </para>
/// <para>
/// Byte order: byte 0 of a block, of the key and of each round key is the leftmost byte of the
/// RFC's hexadecimal strings, i.e. the most significant byte a_15. Blocks and keys are therefore
/// passed exactly as the RFC prints them, the in-memory layout VeraCrypt uses for this cipher. Only
/// the RFC's examples verify it here; no VeraCrypt volume has been checked against it.
/// </para>
/// <para>
/// S followed by L is merged into sixteen lookup tables of 128-bit words (L is linear, so the image
/// of a block is the XOR of the images of its bytes). Decryption applies L^-1 through the same kind
/// of table and Pi^-1 byte by byte.
/// </para>
/// </remarks>
public sealed class Kuznyechik {
  /// <summary>The block size in bytes.</summary>
  public const int BlockSize = 16;

  /// <summary>The key size in bytes.</summary>
  public const int KeySize = 32;

  private const int RoundKeys = 10;
  private const int FieldPolynomial = 0x1C3;

  private static readonly byte[] Pi = [
    252, 238, 221, 17, 207, 110, 49, 22, 251, 196, 250, 218, 35, 197, 4, 77,
    233, 119, 240, 219, 147, 46, 153, 186, 23, 54, 241, 187, 20, 205, 95, 193,
    249, 24, 101, 90, 226, 92, 239, 33, 129, 28, 60, 66, 139, 1, 142, 79,
    5, 132, 2, 174, 227, 106, 143, 160, 6, 11, 237, 152, 127, 212, 211, 31,
    235, 52, 44, 81, 234, 200, 72, 171, 242, 42, 104, 162, 253, 58, 206, 204,
    181, 112, 14, 86, 8, 12, 118, 18, 191, 114, 19, 71, 156, 183, 93, 135,
    21, 161, 150, 41, 16, 123, 154, 199, 243, 145, 120, 111, 157, 158, 178, 177,
    50, 117, 25, 61, 255, 53, 138, 126, 109, 84, 198, 128, 195, 189, 13, 87,
    223, 245, 36, 169, 62, 168, 67, 201, 215, 121, 214, 246, 124, 34, 185, 3,
    224, 15, 236, 222, 122, 148, 176, 188, 220, 232, 40, 80, 78, 51, 10, 74,
    167, 151, 96, 115, 30, 0, 98, 68, 26, 184, 56, 130, 100, 159, 38, 65,
    173, 69, 70, 146, 39, 94, 85, 47, 140, 163, 165, 125, 105, 213, 149, 59,
    7, 88, 179, 64, 134, 172, 29, 247, 48, 55, 107, 228, 136, 217, 231, 137,
    225, 27, 131, 73, 76, 63, 248, 254, 141, 83, 170, 144, 202, 216, 133, 97,
    32, 113, 103, 164, 45, 43, 9, 91, 203, 155, 37, 208, 190, 229, 108, 82,
    89, 166, 116, 210, 230, 244, 180, 192, 209, 102, 175, 194, 57, 75, 99, 182,
  ];

  private static readonly byte[] InversePi = [
    165, 45, 50, 143, 14, 48, 56, 192, 84, 230, 158, 57, 85, 126, 82, 145,
    100, 3, 87, 90, 28, 96, 7, 24, 33, 114, 168, 209, 41, 198, 164, 63,
    224, 39, 141, 12, 130, 234, 174, 180, 154, 99, 73, 229, 66, 228, 21, 183,
    200, 6, 112, 157, 65, 117, 25, 201, 170, 252, 77, 191, 42, 115, 132, 213,
    195, 175, 43, 134, 167, 177, 178, 91, 70, 211, 159, 253, 212, 15, 156, 47,
    155, 67, 239, 217, 121, 182, 83, 127, 193, 240, 35, 231, 37, 94, 181, 30,
    162, 223, 166, 254, 172, 34, 249, 226, 74, 188, 53, 202, 238, 120, 5, 107,
    81, 225, 89, 163, 242, 113, 86, 17, 106, 137, 148, 101, 140, 187, 119, 60,
    123, 40, 171, 210, 49, 222, 196, 95, 204, 207, 118, 44, 184, 216, 46, 54,
    219, 105, 179, 20, 149, 190, 98, 161, 59, 22, 102, 233, 92, 108, 109, 173,
    55, 97, 75, 185, 227, 186, 241, 160, 133, 131, 218, 71, 197, 176, 51, 250,
    150, 111, 110, 194, 246, 80, 255, 93, 169, 142, 23, 27, 151, 125, 236, 88,
    247, 31, 251, 124, 9, 13, 122, 103, 69, 135, 220, 232, 79, 29, 78, 4,
    235, 248, 243, 62, 61, 189, 138, 136, 221, 205, 11, 19, 152, 2, 147, 128,
    144, 208, 36, 52, 203, 237, 244, 206, 153, 16, 68, 64, 146, 58, 1, 38,
    18, 26, 72, 104, 245, 129, 139, 199, 214, 32, 10, 8, 0, 76, 215, 116,
  ];

  /// <summary>The coefficients of l for a_15 down to a_0, i.e. for block bytes 0 to 15.</summary>
  private static readonly byte[] LinearCoefficients = [148, 32, 133, 16, 194, 192, 1, 251, 1, 192, 194, 16, 133, 32, 148, 1];

  /// <summary><c>EncryptTable[i, x]</c> is L(S(block with byte i = x and all others 0)) as two little-endian words.</summary>
  private static readonly (ulong Low, ulong High)[,] EncryptTable = BuildTable(true);

  /// <summary><c>DecryptTable[i, x]</c> is L^-1(block with byte i = x and all others 0) as two little-endian words.</summary>
  private static readonly (ulong Low, ulong High)[,] DecryptTable = BuildTable(false);

  private readonly ulong[] _roundKeys = new ulong[2 * RoundKeys];

  /// <summary>Expands a 32-byte <paramref name="key"/> into the ten round keys.</summary>
  /// <exception cref="ArgumentException">The key is not 32 bytes long.</exception>
  public Kuznyechik(ReadOnlySpan<byte> key) {
    if (key.Length != KeySize)
      throw new ArgumentException("Kuznyechik keys are 32 bytes.", nameof(key));

    var k = this._roundKeys;
    var a1 = (BinaryPrimitives.ReadUInt64LittleEndian(key), BinaryPrimitives.ReadUInt64LittleEndian(key[8..]));
    var a0 = (BinaryPrimitives.ReadUInt64LittleEndian(key[16..]), BinaryPrimitives.ReadUInt64LittleEndian(key[24..]));
    (k[0], k[1]) = a1;
    (k[2], k[3]) = a0;
    Span<byte> constant = stackalloc byte[BlockSize];
    for (var i = 1; i <= 4; ++i) {
      for (var j = 1; j <= 8; ++j) {
        // C = L(Vec_128(n)): n sits in the least significant byte, a_0, which is block byte 15.
        constant.Clear();
        constant[15] = (byte)(8 * (i - 1) + j);
        var c = LinearOnly(constant);
        var t = Lsx((a1.Item1 ^ c.Low, a1.Item2 ^ c.High));
        (a1, a0) = ((t.Low ^ a0.Item1, t.High ^ a0.Item2), a1);
      }
      (k[4 * i], k[4 * i + 1]) = a1;
      (k[4 * i + 2], k[4 * i + 3]) = a0;
    }
  }

  /// <summary>Encrypts one 16-byte block in place.</summary>
  /// <exception cref="ArgumentException">The block is not 16 bytes long.</exception>
  public void Encrypt(Span<byte> block) {
    if (block.Length != BlockSize)
      throw new ArgumentException("Kuznyechik blocks are 16 bytes.", nameof(block));
    var k = this._roundKeys;
    var lo = BinaryPrimitives.ReadUInt64LittleEndian(block);
    var hi = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);
    for (var r = 0; r < RoundKeys - 1; ++r)
      (lo, hi) = Lsx((lo ^ k[2 * r], hi ^ k[2 * r + 1]));
    BinaryPrimitives.WriteUInt64LittleEndian(block, lo ^ k[2 * RoundKeys - 2]);
    BinaryPrimitives.WriteUInt64LittleEndian(block[8..], hi ^ k[2 * RoundKeys - 1]);
  }

  /// <summary>Decrypts one 16-byte block in place.</summary>
  /// <exception cref="ArgumentException">The block is not 16 bytes long.</exception>
  public void Decrypt(Span<byte> block) {
    if (block.Length != BlockSize)
      throw new ArgumentException("Kuznyechik blocks are 16 bytes.", nameof(block));
    var k = this._roundKeys;
    var lo = BinaryPrimitives.ReadUInt64LittleEndian(block) ^ k[2 * RoundKeys - 2];
    var hi = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]) ^ k[2 * RoundKeys - 1];
    var table = DecryptTable;
    var inverse = InversePi;
    for (var r = RoundKeys - 2; r >= 0; --r) {
      ulong newLo = 0, newHi = 0;
      for (var i = 0; i < 8; ++i) {
        var e = table[i, (byte)(lo >> (8 * i))];
        var f = table[i + 8, (byte)(hi >> (8 * i))];
        newLo ^= e.Low ^ f.Low;
        newHi ^= e.High ^ f.High;
      }
      lo = 0;
      hi = 0;
      for (var i = 0; i < 8; ++i) {
        lo |= (ulong)inverse[(byte)(newLo >> (8 * i))] << (8 * i);
        hi |= (ulong)inverse[(byte)(newHi >> (8 * i))] << (8 * i);
      }
      lo ^= k[2 * r];
      hi ^= k[2 * r + 1];
    }
    BinaryPrimitives.WriteUInt64LittleEndian(block, lo);
    BinaryPrimitives.WriteUInt64LittleEndian(block[8..], hi);
  }

  /// <summary>L(S(x)) through the merged tables.</summary>
  private static (ulong Low, ulong High) Lsx((ulong Low, ulong High) x) {
    var table = EncryptTable;
    ulong lo = 0, hi = 0;
    for (var i = 0; i < 8; ++i) {
      var e = table[i, (byte)(x.Low >> (8 * i))];
      var f = table[i + 8, (byte)(x.High >> (8 * i))];
      lo ^= e.Low ^ f.Low;
      hi ^= e.High ^ f.High;
    }
    return (lo, hi);
  }

  /// <summary>L applied to a block given as bytes, returned as two little-endian words.</summary>
  private static (ulong Low, ulong High) LinearOnly(ReadOnlySpan<byte> block) {
    Span<byte> a = stackalloc byte[BlockSize];
    block.CopyTo(a);
    for (var round = 0; round < 16; ++round)
      R(a);
    return (BinaryPrimitives.ReadUInt64LittleEndian(a), BinaryPrimitives.ReadUInt64LittleEndian(a[8..]));
  }

  /// <summary>R(a_15..a_0) = l(a_15..a_0) || a_15..a_1: every byte moves one place right and l enters at byte 0.</summary>
  private static void R(Span<byte> a) {
    var l = Linear(a);
    for (var i = 15; i > 0; --i)
      a[i] = a[i - 1];
    a[0] = l;
  }

  /// <summary>R^-1(a_15..a_0) = a_14..a_0 || l(a_14..a_0, a_15): every byte moves one place left and l enters at byte 15.</summary>
  private static void InverseR(Span<byte> a) {
    var first = a[0];
    for (var i = 0; i < 15; ++i)
      a[i] = a[i + 1];
    a[15] = first;
    a[15] = Linear(a);
  }

  private static byte Linear(ReadOnlySpan<byte> a) {
    byte sum = 0;
    for (var i = 0; i < 16; ++i)
      sum ^= Multiply(LinearCoefficients[i], a[i]);
    return sum;
  }

  /// <summary>Multiplication in the field Q = GF(2)[x]/(x^8+x^7+x^6+x+1).</summary>
  private static byte Multiply(byte a, byte b) {
    int x = a, y = b, product = 0;
    while (y != 0) {
      if ((y & 1) != 0)
        product ^= x;
      x <<= 1;
      if ((x & 0x100) != 0)
        x ^= FieldPolynomial;
      y >>= 1;
    }
    return (byte)product;
  }

  private static (ulong Low, ulong High)[,] BuildTable(bool encrypt) {
    var table = new (ulong, ulong)[BlockSize, 256];
    Span<byte> a = stackalloc byte[BlockSize];
    for (var i = 0; i < BlockSize; ++i)
      for (var x = 0; x < 256; ++x) {
        a.Clear();
        a[i] = encrypt ? Pi[x] : (byte)x;
        for (var round = 0; round < 16; ++round)
          if (encrypt)
            R(a);
          else
            InverseR(a);
        table[i, x] = (BinaryPrimitives.ReadUInt64LittleEndian(a), BinaryPrimitives.ReadUInt64LittleEndian(a[8..]));
      }
    return table;
  }
}
