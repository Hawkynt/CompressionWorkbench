using System.Buffers.Binary;
using System.Security.Cryptography;
using Compression.Core.Crypto;

namespace FileFormat.Rar;

/// <summary>
/// RAR 5 password handling: key derivation per salt (cached, since each derivation runs
/// 2^15 HMAC rounds), the password check value, and the checksum tweak RAR applies to
/// encrypted files whose encryption record sets flag 0x0002.
/// </summary>
/// <remarks>
/// The check-value and checksum-tweak rules are converted from 7-Zip's
/// <c>CPP/7zip/Crypto/Rar5Aes.cpp</c> (<c>SetDecoderProps</c>, <c>Hmac_Convert_Crc32</c>,
/// <c>Hmac_Convert_32Bytes</c>; Igor Pavlov, LGPL-2.1-or-later).
/// </remarks>
internal sealed class Rar5Crypto(string password) {
  private readonly Dictionary<string, (byte[] Key, byte[] HashKey, byte[] Check)> _cache = [];

  public string Password { get; } = password;

  public (byte[] Key, byte[] HashKey, byte[] Check) Keys(byte[] salt, int kdfLog) {
    var id = Convert.ToHexString(salt) + ":" + kdfLog;
    if (!this._cache.TryGetValue(id, out var keys)) {
      keys = KeyDerivation.Rar5DeriveKeys(this.Password, salt, kdfLog);
      this._cache[id] = keys;
    }
    return keys;
  }

  /// <summary>
  /// Whether a stored 12-byte check field can be trusted (its 4-byte SHA-256 sum matches
  /// the 8-byte value), and whether the password matches it.
  /// </summary>
  public static bool? PasswordMatches(byte[]? storedCheck, byte[] computed8) {
    if (storedCheck is not { Length: 12 })
      return null;
    var sum = SHA256.HashData(storedCheck.AsSpan(0, 8));
    if (!sum.AsSpan(0, 4).SequenceEqual(storedCheck.AsSpan(8, 4)))
      return null;
    // RAR 5.21 and older wrote zeros into the check field of service records.
    if (storedCheck.AsSpan(0, 8).IndexOfAnyExcept((byte)0) < 0)
      return null;
    return storedCheck.AsSpan(0, 8).SequenceEqual(computed8);
  }

  /// <summary>The 12-byte check field RAR stores for <paramref name="computed8"/>.</summary>
  public static byte[] CheckField(byte[] computed8) {
    var field = new byte[12];
    computed8.CopyTo(field, 0);
    SHA256.HashData(computed8).AsSpan(0, 4).CopyTo(field.AsSpan(8));
    return field;
  }

  public static uint TweakCrc(byte[] hashKey, uint crc) {
    Span<byte> v = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(v, crc);
    var digest = HMACSHA256.HashData(hashKey, v);
    var result = 0u;
    for (var i = 0; i < 8; ++i)
      result ^= BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(i * 4));
    return result;
  }

  public static byte[] TweakDigest(byte[] hashKey, byte[] digest) => HMACSHA256.HashData(hashKey, digest);
}
