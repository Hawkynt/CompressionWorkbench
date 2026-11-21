using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Compression.Core.Crypto;

/// <summary>
/// scrypt (RFC 7914): PBKDF2-HMAC-SHA256 with one iteration spreads the password over p blocks of
/// 128·r bytes, ROMix runs each through N BlockMix-Salsa20/8 steps and as many data-dependent
/// lookups into the N-entry table, and a second one-iteration PBKDF2 keyed with the password and
/// salted with the mixed blocks gives the key.
/// </summary>
/// <remarks>
/// The table is the one large allocation (128·r·N bytes, reused for every block); BlockMix and the
/// Salsa20/8 core run on spans and locals.
/// </remarks>
public static class Scrypt {
  /// <summary>Derives <paramref name="length"/> bytes.</summary>
  /// <param name="password">The password bytes.</param>
  /// <param name="salt">The salt.</param>
  /// <param name="n">The CPU/memory cost: a power of two, at least 2.</param>
  /// <param name="r">The block size factor (at least 1).</param>
  /// <param name="p">The parallelisation factor (at least 1); the blocks are mixed one after another.</param>
  /// <param name="length">The number of bytes to derive.</param>
  public static byte[] Derive(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int n, int r, int p, int length) {
    if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(n), "N must be a power of two of at least 2.");
    ArgumentOutOfRangeException.ThrowIfLessThan(r, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(p, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
    var words = 32 * r;
    if ((long)words * n > Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(n), "scrypt's table (128·r·N bytes) is too large.");

    var b = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, p * 128 * r);
    var x = new uint[words];
    var y = new uint[words];
    var v = new uint[words * n];
    for (var i = 0; i < p; ++i) {
      var block = b.AsSpan(i * 128 * r, 128 * r);
      for (var k = 0; k < words; ++k) x[k] = BinaryPrimitives.ReadUInt32LittleEndian(block[(k * 4)..]);
      RoMix(x, y, v, r, n);
      for (var k = 0; k < words; ++k) BinaryPrimitives.WriteUInt32LittleEndian(block[(k * 4)..], x[k]);
    }
    return Rfc2898DeriveBytes.Pbkdf2(password, b, 1, HashAlgorithmName.SHA256, length);
  }

  private static void RoMix(uint[] x, uint[] y, uint[] v, int r, int n) {
    var words = x.Length;
    for (var i = 0; i < n; ++i) {
      x.CopyTo(v, i * words);
      BlockMix(x, y, r);
    }
    var last = (2 * r - 1) * 16;
    var mask = (uint)(n - 1);
    for (var i = 0; i < n; ++i) {
      var j = (int)(x[last] & mask);
      var row = v.AsSpan(j * words, words);
      for (var k = 0; k < words; ++k) x[k] ^= row[k];
      BlockMix(x, y, r);
    }
  }

  /// <summary>BlockMix-Salsa20/8 of <paramref name="b"/> in place: the even outputs first, then the odd ones.</summary>
  private static void BlockMix(Span<uint> b, Span<uint> y, int r) {
    Span<uint> t = stackalloc uint[16];
    b.Slice((2 * r - 1) * 16, 16).CopyTo(t);
    for (var i = 0; i < 2 * r; ++i) {
      var block = b.Slice(i * 16, 16);
      for (var k = 0; k < 16; ++k) t[k] ^= block[k];
      Salsa8(t);
      // even blocks go to the first half, odd ones to the second
      t.CopyTo(y.Slice(((i & 1) == 0 ? i / 2 : r + i / 2) * 16, 16));
    }
    y[..(32 * r)].CopyTo(b);
  }

  /// <summary>The Salsa20/8 core: four double rounds on 16 local words, then added to the input.</summary>
  private static void Salsa8(Span<uint> s) {
    uint x0 = s[0], x1 = s[1], x2 = s[2], x3 = s[3], x4 = s[4], x5 = s[5], x6 = s[6], x7 = s[7];
    uint x8 = s[8], x9 = s[9], x10 = s[10], x11 = s[11], x12 = s[12], x13 = s[13], x14 = s[14], x15 = s[15];
    for (var i = 0; i < 4; ++i) {
      x4 ^= uint.RotateLeft(x0 + x12, 7); x8 ^= uint.RotateLeft(x4 + x0, 9);
      x12 ^= uint.RotateLeft(x8 + x4, 13); x0 ^= uint.RotateLeft(x12 + x8, 18);
      x9 ^= uint.RotateLeft(x5 + x1, 7); x13 ^= uint.RotateLeft(x9 + x5, 9);
      x1 ^= uint.RotateLeft(x13 + x9, 13); x5 ^= uint.RotateLeft(x1 + x13, 18);
      x14 ^= uint.RotateLeft(x10 + x6, 7); x2 ^= uint.RotateLeft(x14 + x10, 9);
      x6 ^= uint.RotateLeft(x2 + x14, 13); x10 ^= uint.RotateLeft(x6 + x2, 18);
      x3 ^= uint.RotateLeft(x15 + x11, 7); x7 ^= uint.RotateLeft(x3 + x15, 9);
      x11 ^= uint.RotateLeft(x7 + x3, 13); x15 ^= uint.RotateLeft(x11 + x7, 18);
      x1 ^= uint.RotateLeft(x0 + x3, 7); x2 ^= uint.RotateLeft(x1 + x0, 9);
      x3 ^= uint.RotateLeft(x2 + x1, 13); x0 ^= uint.RotateLeft(x3 + x2, 18);
      x6 ^= uint.RotateLeft(x5 + x4, 7); x7 ^= uint.RotateLeft(x6 + x5, 9);
      x4 ^= uint.RotateLeft(x7 + x6, 13); x5 ^= uint.RotateLeft(x4 + x7, 18);
      x11 ^= uint.RotateLeft(x10 + x9, 7); x8 ^= uint.RotateLeft(x11 + x10, 9);
      x9 ^= uint.RotateLeft(x8 + x11, 13); x10 ^= uint.RotateLeft(x9 + x8, 18);
      x12 ^= uint.RotateLeft(x15 + x14, 7); x13 ^= uint.RotateLeft(x12 + x15, 9);
      x14 ^= uint.RotateLeft(x13 + x12, 13); x15 ^= uint.RotateLeft(x14 + x13, 18);
    }
    s[0] += x0; s[1] += x1; s[2] += x2; s[3] += x3; s[4] += x4; s[5] += x5; s[6] += x6; s[7] += x7;
    s[8] += x8; s[9] += x9; s[10] += x10; s[11] += x11; s[12] += x12; s[13] += x13; s[14] += x14; s[15] += x15;
  }
}
