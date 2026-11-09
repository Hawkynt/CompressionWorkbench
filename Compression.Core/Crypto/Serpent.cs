using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Compression.Core.Crypto;

/// <summary>
/// The Serpent block cipher (Ross Anderson, Eli Biham, Lars Knudsen), 128-bit blocks and keys of
/// up to 256 bits, in its bitslice description with little-endian 32-bit words — the byte order of
/// the NESSIE test vectors.
/// </summary>
/// <remarks>
/// Written from "Serpent: A Proposal for the Advanced Encryption Standard" (the bitslice
/// description, section 2): 32 rounds of key mixing, one of eight 4-bit S-boxes applied across
/// the four words, and the linear transformation; a shorter key is padded with a single 1 bit. The
/// S-box tables are the paper's; the inverse S-boxes are their inverse permutations, and
/// decryption runs the rounds backwards with the paper's inverse linear transformation.
/// </remarks>
public sealed class Serpent {
  /// <summary>The block size in bytes.</summary>
  public const int BlockSize = 16;

  private const int Rounds = 32;
  private const uint Phi = 0x9E3779B9;

  internal static readonly byte[][] SBoxes = [
    [3, 8, 15, 1, 10, 6, 5, 11, 14, 13, 4, 2, 7, 0, 9, 12],
    [15, 12, 2, 7, 9, 0, 5, 10, 1, 11, 14, 8, 6, 13, 3, 4],
    [8, 6, 7, 9, 3, 12, 10, 15, 13, 1, 14, 4, 0, 11, 5, 2],
    [0, 15, 11, 8, 12, 9, 6, 3, 13, 1, 2, 4, 10, 7, 5, 14],
    [1, 15, 8, 3, 12, 0, 11, 6, 2, 5, 4, 10, 9, 14, 7, 13],
    [15, 5, 2, 11, 4, 10, 9, 12, 0, 3, 14, 8, 13, 6, 7, 1],
    [7, 2, 12, 5, 8, 4, 6, 11, 14, 9, 1, 15, 13, 3, 10, 0],
    [1, 13, 15, 0, 14, 8, 2, 11, 7, 4, 12, 10, 9, 3, 5, 6],
  ];

  private static readonly byte[][] InverseSBoxes = Invert(SBoxes);

  private readonly uint[] _subkeys = new uint[(Rounds + 1) * 4];

  /// <summary>Expands <paramref name="key"/> (1 to 32 bytes) into the 33 round keys.</summary>
  /// <exception cref="ArgumentException">The key is empty or longer than 32 bytes.</exception>
  public Serpent(ReadOnlySpan<byte> key) {
    if (key.Length is < 1 or > 32)
      throw new ArgumentException("Serpent keys are 1 to 32 bytes.", nameof(key));

    Span<byte> padded = stackalloc byte[32];
    key.CopyTo(padded);
    if (key.Length < 32)
      padded[key.Length] = 1;

    var w = new uint[8 + this._subkeys.Length];
    for (var i = 0; i < 8; ++i)
      w[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded[(4 * i)..]);
    for (var i = 8; i < w.Length; ++i)
      w[i] = uint.RotateLeft(w[i - 8] ^ w[i - 5] ^ w[i - 3] ^ w[i - 1] ^ Phi ^ (uint)(i - 8), 11);

    for (var k = 0; k <= Rounds; ++k) {
      uint a = w[8 + 4 * k], b = w[9 + 4 * k], c = w[10 + 4 * k], d = w[11 + 4 * k];
      ApplySBox(SBoxes[(3 - k) & 7], ref a, ref b, ref c, ref d);
      this._subkeys[4 * k] = a;
      this._subkeys[4 * k + 1] = b;
      this._subkeys[4 * k + 2] = c;
      this._subkeys[4 * k + 3] = d;
    }
  }

  /// <summary>Encrypts one 16-byte block in place.</summary>
  public void Encrypt(Span<byte> block) {
    if (block.Length != BlockSize)
      throw new ArgumentException("Serpent blocks are 16 bytes.", nameof(block));
    var x0 = BinaryPrimitives.ReadUInt32LittleEndian(block);
    var x1 = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
    var x2 = BinaryPrimitives.ReadUInt32LittleEndian(block[8..]);
    var x3 = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);
    var k = this._subkeys;
    for (var r = 0; r < Rounds; ++r) {
      x0 ^= k[4 * r];
      x1 ^= k[4 * r + 1];
      x2 ^= k[4 * r + 2];
      x3 ^= k[4 * r + 3];
      SBoxEncrypt(r & 7, ref x0, ref x1, ref x2, ref x3);
      if (r == Rounds - 1)
        break;
      x0 = uint.RotateLeft(x0, 13);
      x2 = uint.RotateLeft(x2, 3);
      x1 ^= x0 ^ x2;
      x3 ^= x2 ^ (x0 << 3);
      x1 = uint.RotateLeft(x1, 1);
      x3 = uint.RotateLeft(x3, 7);
      x0 ^= x1 ^ x3;
      x2 ^= x3 ^ (x1 << 7);
      x0 = uint.RotateLeft(x0, 5);
      x2 = uint.RotateLeft(x2, 22);
    }
    BinaryPrimitives.WriteUInt32LittleEndian(block, x0 ^ k[4 * Rounds]);
    BinaryPrimitives.WriteUInt32LittleEndian(block[4..], x1 ^ k[4 * Rounds + 1]);
    BinaryPrimitives.WriteUInt32LittleEndian(block[8..], x2 ^ k[4 * Rounds + 2]);
    BinaryPrimitives.WriteUInt32LittleEndian(block[12..], x3 ^ k[4 * Rounds + 3]);
  }

  /// <summary>Decrypts one 16-byte block in place.</summary>
  /// <exception cref="ArgumentException">The block is not 16 bytes long.</exception>
  public void Decrypt(Span<byte> block) {
    if (block.Length != BlockSize)
      throw new ArgumentException("Serpent blocks are 16 bytes.", nameof(block));
    var k = this._subkeys;
    var x0 = BinaryPrimitives.ReadUInt32LittleEndian(block) ^ k[4 * Rounds];
    var x1 = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]) ^ k[4 * Rounds + 1];
    var x2 = BinaryPrimitives.ReadUInt32LittleEndian(block[8..]) ^ k[4 * Rounds + 2];
    var x3 = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]) ^ k[4 * Rounds + 3];
    for (var r = Rounds - 1; r >= 0; --r) {
      if (r != Rounds - 1) {
        x2 = uint.RotateRight(x2, 22);
        x0 = uint.RotateRight(x0, 5);
        x2 ^= x3 ^ (x1 << 7);
        x0 ^= x1 ^ x3;
        x3 = uint.RotateRight(x3, 7);
        x1 = uint.RotateRight(x1, 1);
        x3 ^= x2 ^ (x0 << 3);
        x1 ^= x0 ^ x2;
        x2 = uint.RotateRight(x2, 3);
        x0 = uint.RotateRight(x0, 13);
      }
      SBoxDecrypt(r & 7, ref x0, ref x1, ref x2, ref x3);
      x0 ^= k[4 * r];
      x1 ^= k[4 * r + 1];
      x2 ^= k[4 * r + 2];
      x3 ^= k[4 * r + 3];
    }
    BinaryPrimitives.WriteUInt32LittleEndian(block, x0);
    BinaryPrimitives.WriteUInt32LittleEndian(block[4..], x1);
    BinaryPrimitives.WriteUInt32LittleEndian(block[8..], x2);
    BinaryPrimitives.WriteUInt32LittleEndian(block[12..], x3);
  }

  private static byte[][] Invert(byte[][] boxes) {
    var inverse = new byte[boxes.Length][];
    for (var i = 0; i < boxes.Length; ++i) {
      inverse[i] = new byte[16];
      for (var x = 0; x < 16; ++x)
        inverse[i][boxes[i][x]] = (byte)x;
    }
    return inverse;
  }

  /// <summary>Applies a 4-bit S-box to each of the 32 bit columns of the four words (word 0 holds the low bit).</summary>
  internal static void ApplySBox(byte[] box, ref uint x0, ref uint x1, ref uint x2, ref uint x3) {
    uint y0 = 0, y1 = 0, y2 = 0, y3 = 0;
    for (var bit = 0; bit < 32; ++bit) {
      var nibble = ((x0 >> bit) & 1) | (((x1 >> bit) & 1) << 1) | (((x2 >> bit) & 1) << 2) | (((x3 >> bit) & 1) << 3);
      var s = (uint)box[nibble];
      y0 |= (s & 1) << bit;
      y1 |= ((s >> 1) & 1) << bit;
      y2 |= ((s >> 2) & 1) << bit;
      y3 |= ((s >> 3) & 1) << bit;
    }
    (x0, x1, x2, x3) = (y0, y1, y2, y3);
  }
  // The S-boxes as Dag Arne Osvik's boolean circuits ("Speeding Up Serpent"), converted from
  // Botan's serpent_sbox.h (Simplified BSD; see THIRD-PARTY-NOTICE.Botan.txt). Each applies the
  // 4-bit S-box to all 32 bit columns at once; the table form above stays the specification and
  // ApplySBox checks one against the other in the tests.

  internal static void SBoxEncrypt(int box, ref uint a, ref uint b, ref uint c, ref uint d) {
    switch (box) {
      case 0: SBoxE0(ref a, ref b, ref c, ref d); break;
      case 1: SBoxE1(ref a, ref b, ref c, ref d); break;
      case 2: SBoxE2(ref a, ref b, ref c, ref d); break;
      case 3: SBoxE3(ref a, ref b, ref c, ref d); break;
      case 4: SBoxE4(ref a, ref b, ref c, ref d); break;
      case 5: SBoxE5(ref a, ref b, ref c, ref d); break;
      case 6: SBoxE6(ref a, ref b, ref c, ref d); break;
      default: SBoxE7(ref a, ref b, ref c, ref d); break;
    }
  }

  internal static void SBoxDecrypt(int box, ref uint a, ref uint b, ref uint c, ref uint d) {
    switch (box) {
      case 0: SBoxD0(ref a, ref b, ref c, ref d); break;
      case 1: SBoxD1(ref a, ref b, ref c, ref d); break;
      case 2: SBoxD2(ref a, ref b, ref c, ref d); break;
      case 3: SBoxD3(ref a, ref b, ref c, ref d); break;
      case 4: SBoxD4(ref a, ref b, ref c, ref d); break;
      case 5: SBoxD5(ref a, ref b, ref c, ref d); break;
      case 6: SBoxD6(ref a, ref b, ref c, ref d); break;
      default: SBoxD7(ref a, ref b, ref c, ref d); break;
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxE0(ref uint a, ref uint b, ref uint c, ref uint d) {
    d ^= a;
    var t0 = b;
    b &= d;
    t0 ^= c;
    b ^= a;
    a |= d;
    a ^= t0;
    t0 ^= d;
    d ^= c;
    c |= b;
    c ^= t0;
    t0 = ~t0;
    t0 |= b;
    b ^= d;
    b ^= t0;
    d |= a;
    b ^= d;
    t0 ^= d;
    d = a;
    a = b;
    b = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxE1(ref uint a, ref uint b, ref uint c, ref uint d) {
    a = ~a;
    c = ~c;
    var t0 = a;
    a &= b;
    c ^= a;
    a |= d;
    d ^= c;
    b ^= a;
    a ^= t0;
    t0 |= b;
    b ^= d;
    c |= a;
    c &= t0;
    a ^= b;
    b &= c;
    b ^= a;
    a &= c;
    t0 ^= a;
    a = c;
    c = d;
    d = b;
    b = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxE2(ref uint a, ref uint b, ref uint c, ref uint d) {
    var t0 = a;
    a &= c;
    a ^= d;
    c ^= b;
    c ^= a;
    d |= t0;
    d ^= b;
    t0 ^= c;
    b = d;
    d |= t0;
    d ^= a;
    a &= b;
    t0 ^= a;
    b ^= d;
    b ^= t0;
    a = c;
    c = b;
    b = d;
    d = ~t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxE3(ref uint a, ref uint b, ref uint c, ref uint d) {
    var t0 = a;
    a |= d;
    d ^= b;
    b &= t0;
    t0 ^= c;
    c ^= d;
    d &= a;
    t0 |= b;
    d ^= t0;
    a ^= b;
    t0 &= a;
    b ^= d;
    t0 ^= c;
    b |= a;
    b ^= c;
    a ^= d;
    c = b;
    b |= d;
    a ^= b;
    b = c;
    c = d;
    d = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxE4(ref uint a, ref uint b, ref uint c, ref uint d) {
    b ^= d;
    d = ~d;
    c ^= d;
    d ^= a;
    var t0 = b;
    b &= d;
    b ^= c;
    t0 ^= d;
    a ^= t0;
    c &= t0;
    c ^= a;
    a &= b;
    d ^= a;
    t0 |= b;
    t0 ^= a;
    a |= d;
    a ^= c;
    c &= d;
    a = ~a;
    t0 ^= c;
    c = a;
    a = b;
    b = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxE5(ref uint a, ref uint b, ref uint c, ref uint d) {
    a ^= b;
    b ^= d;
    d = ~d;
    var t0 = b;
    b &= a;
    c ^= d;
    b ^= c;
    c |= t0;
    t0 ^= d;
    d &= b;
    d ^= a;
    t0 ^= b;
    t0 ^= c;
    c ^= a;
    a &= d;
    c = ~c;
    a ^= t0;
    t0 |= d;
    t0 ^= c;
    c = a;
    a = b;
    b = d;
    d = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxE6(ref uint a, ref uint b, ref uint c, ref uint d) {
    c = ~c;
    var t0 = d;
    d &= a;
    a ^= t0;
    d ^= c;
    c |= t0;
    b ^= d;
    c ^= a;
    a |= b;
    c ^= b;
    t0 ^= a;
    a |= d;
    a ^= c;
    t0 ^= d;
    t0 ^= a;
    d = ~d;
    c &= t0;
    d ^= c;
    c = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxE7(ref uint a, ref uint b, ref uint c, ref uint d) {
    var t0 = b;
    b |= c;
    b ^= d;
    t0 ^= c;
    c ^= b;
    d |= t0;
    d &= a;
    t0 ^= c;
    d ^= b;
    b |= t0;
    b ^= a;
    a |= t0;
    a ^= c;
    b ^= t0;
    c ^= b;
    b &= a;
    b ^= t0;
    c = ~c;
    c |= a;
    t0 ^= c;
    c = b;
    b = d;
    d = a;
    a = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxD0(ref uint a, ref uint b, ref uint c, ref uint d) {
    c = ~c;
    var t0 = b;
    b |= a;
    t0 = ~t0;
    b ^= c;
    c |= t0;
    b ^= d;
    a ^= t0;
    c ^= a;
    a &= d;
    t0 ^= a;
    a |= b;
    a ^= c;
    d ^= t0;
    c ^= b;
    d ^= a;
    d ^= b;
    c &= d;
    t0 ^= c;
    c = b;
    b = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxD1(ref uint a, ref uint b, ref uint c, ref uint d) {
    var t0 = b;
    b ^= d;
    d &= b;
    t0 ^= c;
    d ^= a;
    a |= b;
    c ^= d;
    a ^= t0;
    a |= c;
    b ^= d;
    a ^= b;
    b |= d;
    b ^= a;
    t0 = ~t0;
    t0 ^= b;
    b |= a;
    b ^= a;
    b |= t0;
    d ^= b;
    b = a;
    a = t0;
    t0 = c;
    c = d;
    d = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxD2(ref uint a, ref uint b, ref uint c, ref uint d) {
    c ^= d;
    d ^= a;
    var t0 = d;
    d &= c;
    d ^= b;
    b |= c;
    b ^= t0;
    t0 &= d;
    c ^= d;
    t0 &= a;
    t0 ^= c;
    c &= b;
    c |= a;
    d = ~d;
    c ^= d;
    a ^= d;
    a &= b;
    d ^= t0;
    d ^= a;
    a = b;
    b = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxD3(ref uint a, ref uint b, ref uint c, ref uint d) {
    var t0 = c;
    c ^= b;
    a ^= c;
    t0 &= c;
    t0 ^= a;
    a &= b;
    b ^= d;
    d |= t0;
    c ^= d;
    a ^= d;
    b ^= t0;
    d &= c;
    d ^= b;
    b ^= a;
    b |= c;
    a ^= d;
    b ^= t0;
    a ^= b;
    t0 = a;
    a = c;
    c = d;
    d = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxD4(ref uint a, ref uint b, ref uint c, ref uint d) {
    var t0 = c;
    c &= d;
    c ^= b;
    b |= d;
    b &= a;
    t0 ^= c;
    t0 ^= b;
    b &= c;
    a = ~a;
    d ^= t0;
    b ^= d;
    d &= a;
    d ^= c;
    a ^= b;
    c &= a;
    d ^= a;
    c ^= t0;
    c |= d;
    d ^= a;
    c ^= b;
    b = d;
    d = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxD5(ref uint a, ref uint b, ref uint c, ref uint d) {
    b = ~b;
    var t0 = d;
    c ^= b;
    d |= a;
    d ^= c;
    c |= b;
    c &= a;
    t0 ^= d;
    c ^= t0;
    t0 |= a;
    t0 ^= b;
    b &= c;
    b ^= d;
    t0 ^= c;
    d &= t0;
    t0 ^= b;
    d ^= t0;
    t0 = ~t0;
    d ^= a;
    a = b;
    b = t0;
    t0 = d;
    d = c;
    c = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxD6(ref uint a, ref uint b, ref uint c, ref uint d) {
    a ^= c;
    var t0 = c;
    c &= a;
    t0 ^= d;
    c = ~c;
    d ^= b;
    c ^= d;
    t0 |= a;
    a ^= c;
    d ^= t0;
    t0 ^= b;
    b &= d;
    b ^= a;
    a ^= d;
    a |= c;
    d ^= b;
    t0 ^= a;
    a = b;
    b = c;
    c = t0;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static void SBoxD7(ref uint a, ref uint b, ref uint c, ref uint d) {
    var t0 = c;
    c ^= a;
    a &= d;
    t0 |= d;
    c = ~c;
    d ^= b;
    b |= a;
    a ^= c;
    c &= t0;
    d &= t0;
    b ^= c;
    c ^= a;
    a |= c;
    t0 ^= b;
    a ^= d;
    d ^= t0;
    t0 |= a;
    d ^= c;
    t0 ^= c;
    c = b;
    b = a;
    a = d;
    d = t0;
  }
}
