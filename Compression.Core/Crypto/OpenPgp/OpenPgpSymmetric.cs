using System.Security.Cryptography;

namespace Compression.Core.Crypto.OpenPgp;

/// <summary>A keyed block cipher as OpenPGP uses it: block size and in-place single-block transforms.</summary>
public sealed record OpenPgpBlockCipher(int BlockSize, Action<Span<byte>> Encrypt, Action<Span<byte>> Decrypt);

/// <summary>
/// OpenPGP's symmetric algorithms (RFC 9580 section 9.3) and its CFB mode (section 5.7 and
/// RFC 4880 section 13.9, without the old resynchronisation step).
/// </summary>
public static class OpenPgpSymmetric {
  /// <summary>The key length of symmetric algorithm <paramref name="algorithm"/>.</summary>
  /// <exception cref="NotSupportedException">The algorithm is unknown or not implemented.</exception>
  public static int KeyLength(int algorithm) => algorithm switch {
    2 => 24,        // TripleDES
    3 or 4 => 16,   // CAST5, Blowfish
    7 or 11 => 16,  // AES-128, Camellia-128
    8 or 12 => 24,  // AES-192, Camellia-192
    9 or 10 or 13 => 32, // AES-256, Twofish, Camellia-256
    _ => throw new NotSupportedException($"OpenPGP symmetric algorithm {algorithm} is not supported."),
  };

  /// <summary>The block cipher <paramref name="algorithm"/> keyed with <paramref name="key"/>.</summary>
  public static OpenPgpBlockCipher Create(int algorithm, ReadOnlySpan<byte> key) {
    if (key.Length != KeyLength(algorithm))
      throw new CryptographicException($"OpenPGP algorithm {algorithm} takes a {KeyLength(algorithm)}-byte key, not {key.Length}.");
    switch (algorithm) {
      case 2: { var c = new TripleDes(key); return new(8, c.Encrypt, c.Decrypt); }
      case 3: { var c = new Cast5(key); return new(8, c.Encrypt, c.Decrypt); }
      case 4: { var c = new Blowfish(key); return new(8, c.Encrypt, c.Decrypt); }
      case 7 or 8 or 9: { var c = new AesBlockCipher(key); return new(16, b => c.Encrypt(b), b => c.Decrypt(b)); }
      case 10: { var c = new Twofish(key); return new(16, c.Encrypt, c.Decrypt); }
      default: { var c = new Camellia(key); return new(16, c.Encrypt, c.Decrypt); }
    }
  }

  /// <summary>CFB-encrypts <paramref name="data"/> in place, starting from <paramref name="iv"/>.</summary>
  public static void CfbEncrypt(OpenPgpBlockCipher cipher, ReadOnlySpan<byte> iv, Span<byte> data) {
    Span<byte> register = stackalloc byte[cipher.BlockSize];
    iv.CopyTo(register);
    for (var at = 0; at < data.Length; at += cipher.BlockSize) {
      cipher.Encrypt(register);
      var n = Math.Min(cipher.BlockSize, data.Length - at);
      for (var i = 0; i < n; ++i) {
        data[at + i] ^= register[i];
        register[i] = data[at + i];
      }
    }
  }

  /// <summary>CFB-decrypts <paramref name="data"/> in place, starting from <paramref name="iv"/>.</summary>
  public static void CfbDecrypt(OpenPgpBlockCipher cipher, ReadOnlySpan<byte> iv, Span<byte> data) {
    Span<byte> register = stackalloc byte[cipher.BlockSize];
    iv.CopyTo(register);
    for (var at = 0; at < data.Length; at += cipher.BlockSize) {
      cipher.Encrypt(register);
      var n = Math.Min(cipher.BlockSize, data.Length - at);
      for (var i = 0; i < n; ++i) {
        var c = data[at + i];
        data[at + i] ^= register[i];
        register[i] = c;
      }
    }
  }

  /// <summary>The hash of OpenPGP hash algorithm <paramref name="algorithm"/> (section 9.5).</summary>
  public static IncrementalHash Hash(int algorithm) => algorithm switch {
    1 => IncrementalHash.CreateHash(HashAlgorithmName.MD5),
    2 => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
    8 => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
    9 => IncrementalHash.CreateHash(HashAlgorithmName.SHA384),
    10 => IncrementalHash.CreateHash(HashAlgorithmName.SHA512),
    _ => throw new NotSupportedException($"OpenPGP hash algorithm {algorithm} is not supported."),
  };

  /// <summary>
  /// The string-to-key derivation (section 3.7): simple (0), salted (1) or iterated and salted
  /// (3), as many hash contexts as the key needs, each preloaded with one more zero byte.
  /// </summary>
  public static byte[] StringToKey(int type, int hash, ReadOnlySpan<byte> salt, int countByte, ReadOnlySpan<byte> passphrase, int keyLength) {
    if (type is not (0 or 1 or 3))
      throw new NotSupportedException($"OpenPGP S2K type {type} is not supported.");
    var count = type == 3 ? (long)(16 + (countByte & 15)) << ((countByte >> 4) + 6) : 0;
    var input = new byte[salt.Length + passphrase.Length];
    if (type != 0)
      salt.CopyTo(input);
    else
      input = passphrase.ToArray();
    if (type != 0)
      passphrase.CopyTo(input.AsSpan(salt.Length));
    var key = new byte[keyLength];
    var made = 0;
    for (var context = 0; made < keyLength; ++context) {
      using var h = Hash(hash);
      for (var z = 0; z < context; ++z)
        h.AppendData([0]);
      if (type == 3) {
        var total = Math.Max(count, input.Length);
        for (var fed = 0L; fed < total;) {
          var n = (int)Math.Min(input.Length, total - fed);
          h.AppendData(input.AsSpan(0, n));
          fed += n;
        }
      } else
        h.AppendData(input);
      var digest = h.GetHashAndReset();
      var take = Math.Min(digest.Length, keyLength - made);
      digest.AsSpan(0, take).CopyTo(key.AsSpan(made));
      made += take;
    }
    return key;
  }
}
