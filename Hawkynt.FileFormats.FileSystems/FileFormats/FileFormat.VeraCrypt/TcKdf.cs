using System.Security.Cryptography;
using Compression.Core.Crypto;
using Hawkynt.Algorithms.Hashing;

namespace FileFormat.VeraCrypt;

/// <summary>
/// One header-key derivation TrueCrypt or VeraCrypt uses. The table, the iteration counts and the
/// PIM formulas are cryptsetup's (<c>lib/tcrypt/tcrypt.c</c>, LGPL-2.1-or-later).
/// </summary>
/// <param name="Prf">PRF name: <c>sha512</c>, <c>sha256</c>, <c>whirlpool</c>, <c>ripemd160</c>, <c>blake2s-256</c>, <c>stribog512</c>, <c>sha1</c>, or <c>argon2id</c>.</param>
/// <param name="Iterations">Iterations (Argon2: passes) without a PIM.</param>
/// <param name="VeraCrypt">A VeraCrypt derivation (magic <c>VERA</c>) rather than a TrueCrypt one.</param>
/// <param name="Legacy">Only TrueCrypt before 5.0 used it.</param>
/// <param name="PimConst">VeraCrypt: iterations = PimConst + PimMult × PIM.</param>
/// <param name="PimMult">See <paramref name="PimConst"/>.</param>
/// <param name="MemoryKiB">Argon2 memory without a PIM.</param>
public sealed record TcKdf(string Prf, int Iterations, bool VeraCrypt, bool Legacy, int PimConst, int PimMult, int MemoryKiB = 0) {
  public const int HeaderKeyLength = 192;

  /// <summary>Non-system volume derivations, cheapest-to-try first within each family.</summary>
  public static IReadOnlyList<TcKdf> All { get; } = [
    new("ripemd160", 2000, false, false, 0, 0),
    new("ripemd160", 1000, false, false, 0, 0),
    new("sha512", 1000, false, false, 0, 0),
    new("whirlpool", 1000, false, false, 0, 0),
    new("sha1", 2000, false, true, 0, 0),
    new("sha512", 500000, true, false, 15000, 1000),
    new("whirlpool", 500000, true, false, 15000, 1000),
    new("sha256", 500000, true, false, 15000, 1000),
    new("blake2s-256", 500000, true, false, 15000, 1000),
    new("ripemd160", 655331, true, false, 15000, 1000),
    new("stribog512", 500000, true, false, 15000, 1000),
    new("argon2id", 6, true, false, 0, 0, 425984),
  ];

  /// <summary>Derives the 192-byte header key from the (keyfile-mixed) password bytes.</summary>
  public byte[] Derive(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int pim = 0) {
    if (this.Prf == "argon2id") {
      var (passes, memory) = pim <= 0 ? (this.Iterations, this.MemoryKiB)
        : pim <= 31 ? ((pim - 1) / 3 + 3, 1024 * (64 + (pim - 1) * 32))
        : (pim - 18, 1024 * 1024);
      return Argon2.Hash(Argon2.Variant.Argon2id, password, salt, passes, memory, 1, HeaderKeyLength);
    }
    var iterations = pim > 0 && this.VeraCrypt ? this.PimConst + this.PimMult * pim : this.Iterations;
    return this.Prf switch {
      "sha512" => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, HeaderKeyLength),
      "sha256" => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HeaderKeyLength),
      "sha1" => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA1, HeaderKeyLength),
      "ripemd160" => Pbkdf2(static d => Ripemd160.Compute(d), 64, password, salt, iterations),
      "whirlpool" => Pbkdf2(static d => Whirlpool.Compute(d), 64, password, salt, iterations),
      "blake2s-256" => Pbkdf2(static d => Blake2s.Compute(d), 64, password, salt, iterations),
      "stribog512" => Pbkdf2(static d => Streebog512.Compute(d), 64, password, salt, iterations),
      _ => throw new NotSupportedException($"TrueCrypt/VeraCrypt PRF '{this.Prf}' is not supported."),
    };
  }

  /// <summary>PBKDF2 (RFC 8018) over HMAC with a hash that has no framework implementation.</summary>
  internal static byte[] Pbkdf2(Func<byte[], byte[]> hash, int blockSize, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations) {
    var key = password.Length > blockSize ? hash(password.ToArray()) : password.ToArray();
    var ipad = new byte[blockSize];
    var opad = new byte[blockSize];
    for (var i = 0; i < blockSize; ++i) {
      var k = i < key.Length ? key[i] : (byte)0;
      ipad[i] = (byte)(k ^ 0x36);
      opad[i] = (byte)(k ^ 0x5C);
    }
    var digestLength = hash([]).Length;
    var output = new byte[HeaderKeyLength];
    var inner = new byte[blockSize + Math.Max(salt.Length + 4, digestLength)];
    var outer = new byte[blockSize + digestLength];
    ipad.CopyTo(inner, 0);
    opad.CopyTo(outer, 0);
    for (int block = 1, done = 0; done < output.Length; ++block) {
      salt.CopyTo(inner.AsSpan(blockSize));
      System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(inner.AsSpan(blockSize + salt.Length), block);
      var u = Hmac(inner.AsSpan(0, blockSize + salt.Length + 4), outer, hash, blockSize);
      var t = (byte[])u.Clone();
      for (var it = 1; it < iterations; ++it) {
        u.CopyTo(inner, blockSize);
        u = Hmac(inner.AsSpan(0, blockSize + digestLength), outer, hash, blockSize);
        for (var k = 0; k < t.Length; ++k) t[k] ^= u[k];
      }
      var n = Math.Min(t.Length, output.Length - done);
      t.AsSpan(0, n).CopyTo(output.AsSpan(done));
      done += n;
    }
    CryptographicOperations.ZeroMemory(key);
    return output;
  }

  private static byte[] Hmac(ReadOnlySpan<byte> innerMessage, byte[] outer, Func<byte[], byte[]> hash, int blockSize) {
    var innerDigest = hash(innerMessage.ToArray());
    innerDigest.CopyTo(outer, blockSize);
    return hash(outer);
  }

  /// <summary>
  /// The password buffer the KDF sees: keyfiles are mixed into a 64-byte pool (128 bytes for a
  /// VeraCrypt password longer than 64), then the password is added byte by byte. With keyfiles
  /// the whole pool is used; without, only the password.
  /// </summary>
  public static byte[] MixPassword(ReadOnlySpan<byte> password, IReadOnlyList<byte[]> keyfiles, bool veraCrypt) {
    var poolLength = veraCrypt && password.Length > 64 ? 128 : 64;
    if (password.Length > poolLength)
      throw new ArgumentException($"TrueCrypt/VeraCrypt passwords are at most {poolLength} bytes.", nameof(password));
    var pool = new byte[poolLength];
    foreach (var file in keyfiles) {
      var crc = ~0u;
      var j = 0;
      foreach (var b in file.AsSpan(0, Math.Min(file.Length, 1 << 20))) {
        crc = Crc32Update(crc, b);
        pool[j++] += (byte)(crc >> 24);
        pool[j++] += (byte)(crc >> 16);
        pool[j++] += (byte)(crc >> 8);
        pool[j++] += (byte)crc;
        j %= poolLength;
      }
    }
    for (var i = 0; i < password.Length; ++i) pool[i] += password[i];
    return keyfiles.Count > 0 ? pool : pool.AsSpan(0, password.Length).ToArray();
  }

  /// <summary>One byte of the standard reflected CRC-32 without final inversion (zlib's running value).</summary>
  private static uint Crc32Update(uint crc, byte b) {
    crc ^= b;
    for (var k = 0; k < 8; ++k) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
    return crc;
  }
}
