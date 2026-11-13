using System.Buffers.Binary;
using System.Numerics;

namespace FileFormat.StuffIt;

/// <summary>
/// Classic StuffIt's password encryption ("DES" in StuffIt's menus).
/// </summary>
/// <remarks>
/// <para>
/// Keys are managed with a DES variant. It skips the initial and final permutations, rotates
/// the halves instead, and uses its own nibble-based key schedule. The forks themselves use a
/// simple 64-bit feedback XOR, not DES. Key derivation runs in four steps:
/// <list type="number">
///   <item><b>Archive key.</b> Start from 0123456789ABCDEF, XOR in the first 8 bytes of the
///   password (zero-padded, each masked to 7 bits) and encrypt the result under the schedule of
///   the starting key.</item>
///   <item><b>Archive IV.</b> Decrypt the 8-byte <c>MKey</c> resource (id 0) of the archive's
///   resource fork under the archive key.</item>
///   <item><b>Password check.</b> Encrypt the IV's first four bytes followed by 00 00 00 04; the
///   last four bytes of the result must equal the IV's last four.</item>
///   <item><b>Fork key.</b> The last 16 bytes of each encrypted fork are its entry key. The fork
///   key is DES⁻¹(archive key, first half) XOR archive IV; the fork IV is DES⁻¹(fork key,
///   second half).</item>
/// </list>
/// </para>
/// <para>
/// The fork cipher keeps A ‖ B = fork key, C ‖ D = fork IV. Each 8-byte block (L, R) decrypts to
/// (L ⊕ A ⊕ C, R ⊕ B ⊕ D), then C ← D and D ← ROR1(L ⊕ R ⊕ A ⊕ B ⊕ D). The plaintext is padded
/// to a whole block; the entry header records the padding (byte 104 for the resource fork, 105
/// for the data fork).
/// </para>
/// <para>Converted from XADMaster's XADStuffItDESHandle and XADStuffItParser (LGPL-2.1-or-later); the
/// combined S-box/P-box table is copied verbatim.</para>
/// </remarks>
internal static class StuffItClassicCrypto {
  public const int EntryKeyLength = 16;
  public const int BlockSize = 8;
  public const uint MKeyType = 0x4D4B6579; // 'MKey'

  private static readonly byte[] InitialArchiveKey = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];

  // ── key management cipher ────────────────────────────────────────────────

  private static uint Nibble(ReadOnlySpan<byte> key, int n) => (uint)(key[(n & 0x0F) >> 1] >> (((n ^ 1) & 1) << 2)) & 0x0F;

  private static uint[] Schedule(ReadOnlySpan<byte> key) {
    var subkeys = new uint[32];
    for (var i = 0; i < 16; ++i) {
      var k1 = (Nibble(key, i) >> 2) | (Nibble(key, i + 13) << 2);
      k1 |= ((Nibble(key, i + 11) >> 2) | (Nibble(key, i + 6) << 2)) << 8;
      k1 |= ((Nibble(key, i + 3) >> 2) | (Nibble(key, i + 10) << 2)) << 16;
      k1 |= ((Nibble(key, i + 8) >> 2) | (Nibble(key, i + 1) << 2)) << 24;
      var k0 = (Nibble(key, i + 9) | (Nibble(key, i) << 4)) & 0x3F;
      k0 |= ((Nibble(key, i + 2) | (Nibble(key, i + 11) << 4)) & 0x3F) << 8;
      k0 |= ((Nibble(key, i + 14) | (Nibble(key, i + 3) << 4)) & 0x3F) << 16;
      k0 |= ((Nibble(key, i + 5) | (Nibble(key, i + 8) << 4)) & 0x3F) << 24;
      // The round function works on bit-reversed words, so the subkeys are bit-reversed too.
      subkeys[2 * i] = ReverseBits(k1);
      subkeys[2 * i + 1] = ReverseBits(k0);
    }
    return subkeys;
  }

  private static uint ReverseBits(uint value) {
    var result = 0u;
    for (var i = 0; i < 32; ++i) {
      result = (result << 1) | (value & 1);
      value >>= 1;
    }
    return result;
  }

  private static uint Round(uint right, uint k0, uint k1) {
    var u = right ^ k0;
    var t = BitOperations.RotateRight(right, 4) ^ k1;
    return SpTrans[0][(u >> 2) & 0x3F] ^ SpTrans[2][(u >> 10) & 0x3F] ^ SpTrans[4][(u >> 18) & 0x3F] ^ SpTrans[6][(u >> 26) & 0x3F]
         ^ SpTrans[1][(t >> 2) & 0x3F] ^ SpTrans[3][(t >> 10) & 0x3F] ^ SpTrans[5][(t >> 18) & 0x3F] ^ SpTrans[7][(t >> 26) & 0x3F];
  }

  /// <summary>Encrypts (or, with <paramref name="encrypt"/> false, decrypts) one block in place.</summary>
  private static void Crypt(Span<byte> block, ReadOnlySpan<byte> key, bool encrypt) {
    var ks = Schedule(key);
    var left = BitOperations.RotateRight(ReverseBits(BinaryPrimitives.ReadUInt32BigEndian(block)), 29);
    var right = BitOperations.RotateRight(ReverseBits(BinaryPrimitives.ReadUInt32BigEndian(block[4..])), 29);
    if (encrypt) {
      for (var i = 0; i < 16; i += 2) {
        left ^= Round(right, ks[2 * i], ks[2 * i + 1]);
        right ^= Round(left, ks[2 * i + 2], ks[2 * i + 3]);
      }
    } else {
      for (var i = 15; i > 0; i -= 2) {
        left ^= Round(right, ks[2 * i], ks[2 * i + 1]);
        right ^= Round(left, ks[2 * i - 2], ks[2 * i - 1]);
      }
    }
    BinaryPrimitives.WriteUInt32BigEndian(block, ReverseBits(BitOperations.RotateRight(right, 3)));
    BinaryPrimitives.WriteUInt32BigEndian(block[4..], ReverseBits(BitOperations.RotateRight(left, 3)));
  }

  internal static byte[] Encrypt(ReadOnlySpan<byte> block, ReadOnlySpan<byte> key) {
    var b = block.ToArray();
    Crypt(b, key, true);
    return b;
  }

  internal static byte[] Decrypt(ReadOnlySpan<byte> block, ReadOnlySpan<byte> key) {
    var b = block.ToArray();
    Crypt(b, key, false);
    return b;
  }

  // ── key derivation ───────────────────────────────────────────────────────

  /// <summary>The archive key of <paramref name="password"/> (bytes in the archive's encoding).</summary>
  public static byte[] ArchiveKey(ReadOnlySpan<byte> password) {
    // Only the first 8 bytes of the password count. StuffIt Deluxe 4.5's archive (8-character
    // password) and The Unarchiver 1.10 (passwords of 1 to 25 characters, round-tripped) agree on a
    // single block; the loop at the converted XADMaster revision would add a second, all-zero
    // block to an 8-character password, which that archive refutes. How StuffIt itself treats
    // longer passwords has not been observed.
    var key = (byte[])InitialArchiveKey.Clone();
    const int blocks = 1;
    Span<byte> block = stackalloc byte[BlockSize];
    for (var x = 0; x < blocks; ++x) {
      block.Clear();
      var offset = x * BlockSize;
      if (offset < password.Length) password.Slice(offset, Math.Min(BlockSize, password.Length - offset)).CopyTo(block);
      for (var i = 0; i < BlockSize; ++i) key[i] ^= (byte)(block[i] & 0x7F);
      Crypt(key, InitialArchiveKey, true);
    }
    return key;
  }

  /// <summary>The archive IV, or <see langword="null"/> when the password does not match <paramref name="mKey"/>.</summary>
  public static byte[]? ArchiveIv(ReadOnlySpan<byte> archiveKey, ReadOnlySpan<byte> mKey) {
    if (mKey.Length != BlockSize) throw new InvalidDataException("The StuffIt MKey resource is not 8 bytes long.");
    var iv = Decrypt(mKey, archiveKey);
    Span<byte> verify = [iv[0], iv[1], iv[2], iv[3], 0, 0, 0, 4];
    var check = Encrypt(verify, archiveKey);
    return check.AsSpan(4).SequenceEqual(iv.AsSpan(4)) ? iv : null;
  }

  /// <summary>An MKey resource for a fresh archive IV under <paramref name="archiveKey"/>, and that IV.</summary>
  public static (byte[] MKey, byte[] Iv) NewMKey(ReadOnlySpan<byte> archiveKey) {
    var seed = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4);
    Span<byte> verify = [seed[0], seed[1], seed[2], seed[3], 0, 0, 0, 4];
    var check = Encrypt(verify, archiveKey);
    byte[] iv = [seed[0], seed[1], seed[2], seed[3], check[4], check[5], check[6], check[7]];
    return (Encrypt(iv, archiveKey), iv);
  }

  /// <summary>The 16-byte fork key ‖ fork IV for an entry key.</summary>
  public static byte[] ForkKey(ReadOnlySpan<byte> archiveKey, ReadOnlySpan<byte> archiveIv, ReadOnlySpan<byte> entryKey) {
    var key = Decrypt(entryKey[..BlockSize], archiveKey);
    for (var i = 0; i < BlockSize; ++i) key[i] ^= archiveIv[i];
    var iv = Decrypt(entryKey.Slice(BlockSize, BlockSize), key);
    return [.. key, .. iv];
  }

  /// <summary>A fresh fork key ‖ fork IV and the entry key that carries it.</summary>
  public static (byte[] ForkKey, byte[] EntryKey) NewForkKey(ReadOnlySpan<byte> archiveKey, ReadOnlySpan<byte> archiveIv) {
    var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(BlockSize);
    var iv = System.Security.Cryptography.RandomNumberGenerator.GetBytes(BlockSize);
    var masked = (byte[])key.Clone();
    for (var i = 0; i < BlockSize; ++i) masked[i] ^= archiveIv[i];
    byte[] entryKey = [.. Encrypt(masked, archiveKey), .. Encrypt(iv, key)];
    return ([.. key, .. iv], entryKey);
  }

  // ── fork cipher ──────────────────────────────────────────────────────────

  /// <summary>Decrypts whole blocks in place under a 16-byte fork key ‖ IV.</summary>
  public static void DecryptFork(Span<byte> data, ReadOnlySpan<byte> forkKey) => Fork(data, forkKey, decrypt: true);

  /// <summary>Encrypts whole blocks in place under a 16-byte fork key ‖ IV.</summary>
  public static void EncryptFork(Span<byte> data, ReadOnlySpan<byte> forkKey) => Fork(data, forkKey, decrypt: false);

  private static void Fork(Span<byte> data, ReadOnlySpan<byte> forkKey, bool decrypt) {
    if (data.Length % BlockSize != 0) throw new InvalidDataException("An encrypted StuffIt fork is not a whole number of 8-byte blocks.");
    var a = BinaryPrimitives.ReadUInt32BigEndian(forkKey);
    var b = BinaryPrimitives.ReadUInt32BigEndian(forkKey[4..]);
    var c = BinaryPrimitives.ReadUInt32BigEndian(forkKey[8..]);
    var d = BinaryPrimitives.ReadUInt32BigEndian(forkKey[12..]);
    for (var o = 0; o < data.Length; o += BlockSize) {
      var block = data.Slice(o, BlockSize);
      var x = BinaryPrimitives.ReadUInt32BigEndian(block);
      var y = BinaryPrimitives.ReadUInt32BigEndian(block[4..]);
      // the feedback always uses the cipher words
      uint left, right;
      if (decrypt) {
        (left, right) = (x, y);
        BinaryPrimitives.WriteUInt32BigEndian(block, x ^ a ^ c);
        BinaryPrimitives.WriteUInt32BigEndian(block[4..], y ^ b ^ d);
      } else {
        left = x ^ a ^ c;
        right = y ^ b ^ d;
        BinaryPrimitives.WriteUInt32BigEndian(block, left);
        BinaryPrimitives.WriteUInt32BigEndian(block[4..], right);
      }
      var nextD = BitOperations.RotateRight(left ^ right ^ a ^ b ^ d, 1);
      c = d;
      d = nextD;
    }
  }

  // ── tables (verbatim from XADStuffItDESHandle.m) ─────────────────────────

  private static readonly uint[][] SpTrans = [
    [
      0x02080800, 0x00080000, 0x02000002, 0x02080802, 0x02000000, 0x00080802, 0x00080002, 0x02000002,
      0x00080802, 0x02080800, 0x02080000, 0x00000802, 0x02000802, 0x02000000, 0x00000000, 0x00080002,
      0x00080000, 0x00000002, 0x02000800, 0x00080800, 0x02080802, 0x02080000, 0x00000802, 0x02000800,
      0x00000002, 0x00000800, 0x00080800, 0x02080002, 0x00000800, 0x02000802, 0x02080002, 0x00000000,
      0x00000000, 0x02080802, 0x02000800, 0x00080002, 0x02080800, 0x00080000, 0x00000802, 0x02000800,
      0x02080002, 0x00000800, 0x00080800, 0x02000002, 0x00080802, 0x00000002, 0x02000002, 0x02080000,
      0x02080802, 0x00080800, 0x02080000, 0x02000802, 0x02000000, 0x00000802, 0x00080002, 0x00000000,
      0x00080000, 0x02000000, 0x02000802, 0x02080800, 0x00000002, 0x02080002, 0x00000800, 0x00080802,
    ],
    [
      0x40108010, 0x00000000, 0x00108000, 0x40100000, 0x40000010, 0x00008010, 0x40008000, 0x00108000,
      0x00008000, 0x40100010, 0x00000010, 0x40008000, 0x00100010, 0x40108000, 0x40100000, 0x00000010,
      0x00100000, 0x40008010, 0x40100010, 0x00008000, 0x00108010, 0x40000000, 0x00000000, 0x00100010,
      0x40008010, 0x00108010, 0x40108000, 0x40000010, 0x40000000, 0x00100000, 0x00008010, 0x40108010,
      0x00100010, 0x40108000, 0x40008000, 0x00108010, 0x40108010, 0x00100010, 0x40000010, 0x00000000,
      0x40000000, 0x00008010, 0x00100000, 0x40100010, 0x00008000, 0x40000000, 0x00108010, 0x40008010,
      0x40108000, 0x00008000, 0x00000000, 0x40000010, 0x00000010, 0x40108010, 0x00108000, 0x40100000,
      0x40100010, 0x00100000, 0x00008010, 0x40008000, 0x40008010, 0x00000010, 0x40100000, 0x00108000,
    ],
    [
      0x04000001, 0x04040100, 0x00000100, 0x04000101, 0x00040001, 0x04000000, 0x04000101, 0x00040100,
      0x04000100, 0x00040000, 0x04040000, 0x00000001, 0x04040101, 0x00000101, 0x00000001, 0x04040001,
      0x00000000, 0x00040001, 0x04040100, 0x00000100, 0x00000101, 0x04040101, 0x00040000, 0x04000001,
      0x04040001, 0x04000100, 0x00040101, 0x04040000, 0x00040100, 0x00000000, 0x04000000, 0x00040101,
      0x04040100, 0x00000100, 0x00000001, 0x00040000, 0x00000101, 0x00040001, 0x04040000, 0x04000101,
      0x00000000, 0x04040100, 0x00040100, 0x04040001, 0x00040001, 0x04000000, 0x04040101, 0x00000001,
      0x00040101, 0x04000001, 0x04000000, 0x04040101, 0x00040000, 0x04000100, 0x04000101, 0x00040100,
      0x04000100, 0x00000000, 0x04040001, 0x00000101, 0x04000001, 0x00040101, 0x00000100, 0x04040000,
    ],
    [
      0x00401008, 0x10001000, 0x00000008, 0x10401008, 0x00000000, 0x10400000, 0x10001008, 0x00400008,
      0x10401000, 0x10000008, 0x10000000, 0x00001008, 0x10000008, 0x00401008, 0x00400000, 0x10000000,
      0x10400008, 0x00401000, 0x00001000, 0x00000008, 0x00401000, 0x10001008, 0x10400000, 0x00001000,
      0x00001008, 0x00000000, 0x00400008, 0x10401000, 0x10001000, 0x10400008, 0x10401008, 0x00400000,
      0x10400008, 0x00001008, 0x00400000, 0x10000008, 0x00401000, 0x10001000, 0x00000008, 0x10400000,
      0x10001008, 0x00000000, 0x00001000, 0x00400008, 0x00000000, 0x10400008, 0x10401000, 0x00001000,
      0x10000000, 0x10401008, 0x00401008, 0x00400000, 0x10401008, 0x00000008, 0x10001000, 0x00401008,
      0x00400008, 0x00401000, 0x10400000, 0x10001008, 0x00001008, 0x10000000, 0x10000008, 0x10401000,
    ],
    [
      0x08000000, 0x00010000, 0x00000400, 0x08010420, 0x08010020, 0x08000400, 0x00010420, 0x08010000,
      0x00010000, 0x00000020, 0x08000020, 0x00010400, 0x08000420, 0x08010020, 0x08010400, 0x00000000,
      0x00010400, 0x08000000, 0x00010020, 0x00000420, 0x08000400, 0x00010420, 0x00000000, 0x08000020,
      0x00000020, 0x08000420, 0x08010420, 0x00010020, 0x08010000, 0x00000400, 0x00000420, 0x08010400,
      0x08010400, 0x08000420, 0x00010020, 0x08010000, 0x00010000, 0x00000020, 0x08000020, 0x08000400,
      0x08000000, 0x00010400, 0x08010420, 0x00000000, 0x00010420, 0x08000000, 0x00000400, 0x00010020,
      0x08000420, 0x00000400, 0x00000000, 0x08010420, 0x08010020, 0x08010400, 0x00000420, 0x00010000,
      0x00010400, 0x08010020, 0x08000400, 0x00000420, 0x00000020, 0x00010420, 0x08010000, 0x08000020,
    ],
    [
      0x80000040, 0x00200040, 0x00000000, 0x80202000, 0x00200040, 0x00002000, 0x80002040, 0x00200000,
      0x00002040, 0x80202040, 0x00202000, 0x80000000, 0x80002000, 0x80000040, 0x80200000, 0x00202040,
      0x00200000, 0x80002040, 0x80200040, 0x00000000, 0x00002000, 0x00000040, 0x80202000, 0x80200040,
      0x80202040, 0x80200000, 0x80000000, 0x00002040, 0x00000040, 0x00202000, 0x00202040, 0x80002000,
      0x00002040, 0x80000000, 0x80002000, 0x00202040, 0x80202000, 0x00200040, 0x00000000, 0x80002000,
      0x80000000, 0x00002000, 0x80200040, 0x00200000, 0x00200040, 0x80202040, 0x00202000, 0x00000040,
      0x80202040, 0x00202000, 0x00200000, 0x80002040, 0x80000040, 0x80200000, 0x00202040, 0x00000000,
      0x00002000, 0x80000040, 0x80002040, 0x80202000, 0x80200000, 0x00002040, 0x00000040, 0x80200040,
    ],
    [
      0x00004000, 0x00000200, 0x01000200, 0x01000004, 0x01004204, 0x00004004, 0x00004200, 0x00000000,
      0x01000000, 0x01000204, 0x00000204, 0x01004000, 0x00000004, 0x01004200, 0x01004000, 0x00000204,
      0x01000204, 0x00004000, 0x00004004, 0x01004204, 0x00000000, 0x01000200, 0x01000004, 0x00004200,
      0x01004004, 0x00004204, 0x01004200, 0x00000004, 0x00004204, 0x01004004, 0x00000200, 0x01000000,
      0x00004204, 0x01004000, 0x01004004, 0x00000204, 0x00004000, 0x00000200, 0x01000000, 0x01004004,
      0x01000204, 0x00004204, 0x00004200, 0x00000000, 0x00000200, 0x01000004, 0x00000004, 0x01000200,
      0x00000000, 0x01000204, 0x01000200, 0x00004200, 0x00000204, 0x00004000, 0x01004204, 0x01000000,
      0x01004200, 0x00000004, 0x00004004, 0x01004204, 0x01000004, 0x01004200, 0x01004000, 0x00004004,
    ],
    [
      0x20800080, 0x20820000, 0x00020080, 0x00000000, 0x20020000, 0x00800080, 0x20800000, 0x20820080,
      0x00000080, 0x20000000, 0x00820000, 0x00020080, 0x00820080, 0x20020080, 0x20000080, 0x20800000,
      0x00020000, 0x00820080, 0x00800080, 0x20020000, 0x20820080, 0x20000080, 0x00000000, 0x00820000,
      0x20000000, 0x00800000, 0x20020080, 0x20800080, 0x00800000, 0x00020000, 0x20820000, 0x00000080,
      0x00800000, 0x00020000, 0x20000080, 0x20820080, 0x00020080, 0x20000000, 0x00000000, 0x00820000,
      0x20800080, 0x20020080, 0x20020000, 0x00800080, 0x20820000, 0x00000080, 0x00800080, 0x20020000,
      0x20820080, 0x00800000, 0x20800000, 0x20000080, 0x00820000, 0x00020080, 0x20020080, 0x20800000,
      0x00000080, 0x20820000, 0x00820080, 0x00000000, 0x20000000, 0x20800080, 0x00020000, 0x00820080,
    ],
  ];
}
