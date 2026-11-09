using System.Buffers.Binary;

namespace Compression.Core.Crypto;

/// <summary>
/// The Camellia block cipher (Mitsubishi Electric and NTT), 128-bit blocks and 128-, 192- or
/// 256-bit keys, in the big-endian byte order of RFC 3713.
/// </summary>
/// <remarks>
/// Written from RFC 3713, "A Description of the Camellia Encryption Algorithm": the key schedule of
/// section 2.2 (KL, KR, KA, KB and the Sigma constants), the 18- or 24-round Feistel network with
/// FL/FLINV layers every six rounds of section 2.3, and the F-function of section 2.4.1. SBOX1 and
/// the Sigma constants are the RFC's; SBOX2–SBOX4 follow from SBOX1 by the RFC's rotations. The
/// S-box lookups and the byte-mixing P-function of F are merged into eight 64-bit lookup tables
/// once. Decryption is encryption with the subkeys swapped as section 2.3.3 lists.
/// </remarks>
public sealed class Camellia {
  /// <summary>The block size in bytes.</summary>
  public const int BlockSize = 16;

  private const ulong Sigma1 = 0xA09E667F3BCC908B;
  private const ulong Sigma2 = 0xB67AE8584CAA73B2;
  private const ulong Sigma3 = 0xC6EF372FE94F82BE;
  private const ulong Sigma4 = 0x54FF53A5F1D36F1C;
  private const ulong Sigma5 = 0x10E527FADE682D1D;
  private const ulong Sigma6 = 0xB05688C2B3E6C1FD;

  private static readonly byte[] Sbox1 = [
    112, 130, 44, 236, 179, 39, 192, 229, 228, 133, 87, 53, 234, 12, 174, 65,
    35, 239, 107, 147, 69, 25, 165, 33, 237, 14, 79, 78, 29, 101, 146, 189,
    134, 184, 175, 143, 124, 235, 31, 206, 62, 48, 220, 95, 94, 197, 11, 26,
    166, 225, 57, 202, 213, 71, 93, 61, 217, 1, 90, 214, 81, 86, 108, 77,
    139, 13, 154, 102, 251, 204, 176, 45, 116, 18, 43, 32, 240, 177, 132, 153,
    223, 76, 203, 194, 52, 126, 118, 5, 109, 183, 169, 49, 209, 23, 4, 215,
    20, 88, 58, 97, 222, 27, 17, 28, 50, 15, 156, 22, 83, 24, 242, 34,
    254, 68, 207, 178, 195, 181, 122, 145, 36, 8, 232, 168, 96, 252, 105, 80,
    170, 208, 160, 125, 161, 137, 98, 151, 84, 91, 30, 149, 224, 255, 100, 210,
    16, 196, 0, 72, 163, 247, 117, 219, 138, 3, 230, 218, 9, 63, 221, 148,
    135, 92, 131, 2, 205, 74, 144, 51, 115, 103, 246, 243, 157, 127, 191, 226,
    82, 155, 216, 38, 200, 55, 198, 59, 129, 150, 111, 75, 19, 190, 99, 46,
    233, 121, 167, 140, 159, 110, 188, 142, 41, 245, 249, 182, 47, 253, 180, 89,
    120, 152, 6, 106, 231, 70, 113, 186, 212, 37, 171, 66, 136, 162, 141, 250,
    114, 7, 185, 85, 248, 238, 172, 10, 54, 73, 42, 104, 60, 56, 241, 164,
    64, 40, 211, 123, 187, 201, 67, 193, 21, 227, 173, 244, 119, 199, 128, 158,
  ];

  /// <summary>Which S-box (1–4) each input byte t1..t8 of F passes through.</summary>
  private static readonly int[] SboxOfByte = [1, 2, 3, 4, 2, 3, 4, 1];

  /// <summary>The P-function of F: output byte y1..y8 is the XOR of the listed bytes t1..t8.</summary>
  private static readonly int[][] PFunction = [
    [1, 3, 4, 6, 7, 8],
    [1, 2, 4, 5, 7, 8],
    [1, 2, 3, 5, 6, 8],
    [2, 3, 4, 5, 6, 7],
    [1, 2, 6, 7, 8],
    [2, 3, 5, 7, 8],
    [3, 4, 5, 6, 8],
    [1, 4, 5, 6, 7],
  ];

  /// <summary><c>Sp[j][x]</c> is the contribution of input byte t(j+1) = x to F's output.</summary>
  private static readonly ulong[][] Sp = BuildSpTables();

  private readonly int _rounds;
  private readonly ulong[] _encryptKw = new ulong[4];
  private readonly ulong[] _encryptK;
  private readonly ulong[] _encryptKe;
  private readonly ulong[] _decryptKw = new ulong[4];
  private readonly ulong[] _decryptK;
  private readonly ulong[] _decryptKe;

  /// <summary>Expands a 16-, 24- or 32-byte <paramref name="key"/>.</summary>
  /// <exception cref="ArgumentException">The key is not 16, 24 or 32 bytes long.</exception>
  public Camellia(ReadOnlySpan<byte> key) {
    if (key.Length is not (16 or 24 or 32))
      throw new ArgumentException("Camellia keys are 16, 24 or 32 bytes.", nameof(key));

    var kl = BinaryPrimitives.ReadUInt128BigEndian(key);
    UInt128 kr = 0;
    if (key.Length == 24) {
      var right = BinaryPrimitives.ReadUInt64BigEndian(key[16..]);
      kr = new UInt128(right, ~right);
    } else if (key.Length == 32)
      kr = BinaryPrimitives.ReadUInt128BigEndian(key[16..]);

    var x = kl ^ kr;
    ulong d1 = High(x), d2 = Low(x);
    d2 ^= F(d1, Sigma1);
    d1 ^= F(d2, Sigma2);
    d1 ^= High(kl);
    d2 ^= Low(kl);
    d2 ^= F(d1, Sigma3);
    d1 ^= F(d2, Sigma4);
    var ka = new UInt128(d1, d2);

    this._rounds = key.Length == 16 ? 18 : 24;
    var k = this._encryptK = new ulong[this._rounds];
    var ke = this._encryptKe = new ulong[this._rounds / 3 - 2];
    var kw = this._encryptKw;

    if (key.Length == 16) {
      (kw[0], kw[1]) = Halves(kl, 0);
      (k[0], k[1]) = Halves(ka, 0);
      (k[2], k[3]) = Halves(kl, 15);
      (k[4], k[5]) = Halves(ka, 15);
      (ke[0], ke[1]) = Halves(ka, 30);
      (k[6], k[7]) = Halves(kl, 45);
      k[8] = Halves(ka, 45).High;
      k[9] = Halves(kl, 60).Low;
      (k[10], k[11]) = Halves(ka, 60);
      (ke[2], ke[3]) = Halves(kl, 77);
      (k[12], k[13]) = Halves(kl, 94);
      (k[14], k[15]) = Halves(ka, 94);
      (k[16], k[17]) = Halves(kl, 111);
      (kw[2], kw[3]) = Halves(ka, 111);
    } else {
      x = ka ^ kr;
      d1 = High(x);
      d2 = Low(x);
      d2 ^= F(d1, Sigma5);
      d1 ^= F(d2, Sigma6);
      var kb = new UInt128(d1, d2);

      (kw[0], kw[1]) = Halves(kl, 0);
      (k[0], k[1]) = Halves(kb, 0);
      (k[2], k[3]) = Halves(kr, 15);
      (k[4], k[5]) = Halves(ka, 15);
      (ke[0], ke[1]) = Halves(kr, 30);
      (k[6], k[7]) = Halves(kb, 30);
      (k[8], k[9]) = Halves(kl, 45);
      (k[10], k[11]) = Halves(ka, 45);
      (ke[2], ke[3]) = Halves(kl, 60);
      (k[12], k[13]) = Halves(kr, 60);
      (k[14], k[15]) = Halves(kb, 60);
      (k[16], k[17]) = Halves(kl, 77);
      (ke[4], ke[5]) = Halves(ka, 77);
      (k[18], k[19]) = Halves(kr, 94);
      (k[20], k[21]) = Halves(ka, 94);
      (k[22], k[23]) = Halves(kl, 111);
      (kw[2], kw[3]) = Halves(kb, 111);
    }

    // Section 2.3.3: kw1<->kw3, kw2<->kw4, and k and ke each reversed end to end.
    this._decryptKw[0] = kw[2];
    this._decryptKw[1] = kw[3];
    this._decryptKw[2] = kw[0];
    this._decryptKw[3] = kw[1];
    this._decryptK = [.. k];
    Array.Reverse(this._decryptK);
    this._decryptKe = [.. ke];
    Array.Reverse(this._decryptKe);
  }

  /// <summary>Encrypts one 16-byte block in place.</summary>
  /// <exception cref="ArgumentException">The block is not 16 bytes long.</exception>
  public void Encrypt(Span<byte> block) => this.Crypt(block, this._encryptKw, this._encryptK, this._encryptKe);

  /// <summary>Decrypts one 16-byte block in place.</summary>
  /// <exception cref="ArgumentException">The block is not 16 bytes long.</exception>
  public void Decrypt(Span<byte> block) => this.Crypt(block, this._decryptKw, this._decryptK, this._decryptKe);

  private void Crypt(Span<byte> block, ulong[] kw, ulong[] k, ulong[] ke) {
    if (block.Length != BlockSize)
      throw new ArgumentException("Camellia blocks are 16 bytes.", nameof(block));
    var d1 = BinaryPrimitives.ReadUInt64BigEndian(block) ^ kw[0];
    var d2 = BinaryPrimitives.ReadUInt64BigEndian(block[8..]) ^ kw[1];
    for (var r = 0; r < this._rounds; r += 2) {
      if (r != 0 && r % 6 == 0) {
        var layer = r / 3 - 2;
        d1 = FL(d1, ke[layer]);
        d2 = FLInverse(d2, ke[layer + 1]);
      }
      d2 ^= F(d1, k[r]);
      d1 ^= F(d2, k[r + 1]);
    }
    BinaryPrimitives.WriteUInt64BigEndian(block, d2 ^ kw[2]);
    BinaryPrimitives.WriteUInt64BigEndian(block[8..], d1 ^ kw[3]);
  }

  private static ulong F(ulong input, ulong key) {
    var x = input ^ key;
    var sp = Sp;
    return sp[0][x >> 56] ^ sp[1][(byte)(x >> 48)] ^ sp[2][(byte)(x >> 40)] ^ sp[3][(byte)(x >> 32)]
      ^ sp[4][(byte)(x >> 24)] ^ sp[5][(byte)(x >> 16)] ^ sp[6][(byte)(x >> 8)] ^ sp[7][(byte)x];
  }

  private static ulong FL(ulong input, ulong key) {
    var x1 = (uint)(input >> 32);
    var x2 = (uint)input;
    x2 ^= uint.RotateLeft(x1 & (uint)(key >> 32), 1);
    x1 ^= x2 | (uint)key;
    return (ulong)x1 << 32 | x2;
  }

  private static ulong FLInverse(ulong input, ulong key) {
    var y1 = (uint)(input >> 32);
    var y2 = (uint)input;
    y1 ^= y2 | (uint)key;
    y2 ^= uint.RotateLeft(y1 & (uint)(key >> 32), 1);
    return (ulong)y1 << 32 | y2;
  }

  /// <summary>The left (<c>&gt;&gt; 64</c>) and right (<c>&amp; MASK64</c>) halves of <paramref name="value"/> rotated left by <paramref name="bits"/>.</summary>
  private static (ulong High, ulong Low) Halves(UInt128 value, int bits) {
    var rotated = UInt128.RotateLeft(value, bits);
    return (High(rotated), Low(rotated));
  }

  private static ulong High(UInt128 value) => (ulong)(value >> 64);

  private static ulong Low(UInt128 value) => (ulong)value;

  private static byte Sbox(int box, byte x) => box switch {
    1 => Sbox1[x],
    2 => byte.RotateLeft(Sbox1[x], 1),
    3 => byte.RotateLeft(Sbox1[x], 7),
    _ => Sbox1[byte.RotateLeft(x, 1)],
  };

  private static ulong[][] BuildSpTables() {
    var tables = new ulong[8][];
    for (var j = 0; j < 8; ++j) {
      tables[j] = new ulong[256];
      for (var x = 0; x < 256; ++x) {
        ulong s = Sbox(SboxOfByte[j], (byte)x);
        ulong word = 0;
        for (var i = 0; i < 8; ++i)
          if (Array.IndexOf(PFunction[i], j + 1) >= 0)
            word |= s << (56 - 8 * i);
        tables[j][x] = word;
      }
    }
    return tables;
  }
}
