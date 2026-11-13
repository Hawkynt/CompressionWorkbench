using Compression.Core.Checksums;
using Compression.Core.Crypto;

namespace FileFormat.StuffIt;

/// <summary>
/// StuffIt 5's password encryption. The archive key is the first 5 bytes of MD5(password). The
/// archive header stores the first 5 bytes of MD5(archive key) as a check value. Every encrypted
/// fork carries its own 5-byte key and is RC4-encrypted, after compression, under the 10-byte key
/// archive key ‖ fork key.
/// </summary>
/// <remarks>
/// The check value follows the optional header parts and is announced by archive flag 0x80. The
/// entry flag 0x20 marks an encrypted entry, whose key-length bytes then hold 5. Converted from
/// XADMaster's XADStuffIt5Parser (LGPL-2.1-or-later). The password is taken in MacRoman, the
/// archives' own encoding.
/// </remarks>
internal static class StuffIt5Crypto {
  public const int KeyLength = 5;
  public const byte ArchiveFlagEncrypted = 0x80;
  public const byte EntryFlagEncrypted = 0x20;

  /// <summary>The password's bytes in MacRoman; characters outside it become '?'.</summary>
  public static byte[] PasswordBytes(string password) {
    var bytes = new byte[password.Length];
    for (var i = 0; i < password.Length; ++i) {
      var c = password[i];
      var high = StuffItReader.MacRomanHigh.IndexOf(c);
      bytes[i] = c < 0x80 ? (byte)c : high >= 0 ? (byte)(0x80 + high) : (byte)'?';
    }
    return bytes;
  }

  /// <summary>The 5-byte archive key of <paramref name="password"/>.</summary>
  public static byte[] KeyOf(string password) => Md5.Compute(PasswordBytes(password)).AsSpan(0, KeyLength).ToArray();

  /// <summary>The check value the archive header stores for <paramref name="archiveKey"/>.</summary>
  public static byte[] CheckValue(ReadOnlySpan<byte> archiveKey) => Md5.Compute(archiveKey).AsSpan(0, KeyLength).ToArray();

  /// <summary>The archive key, or <see langword="null"/> when <paramref name="password"/> does not match <paramref name="checkValue"/>.</summary>
  public static byte[]? ArchiveKey(string password, ReadOnlySpan<byte> checkValue) {
    var key = KeyOf(password);
    return CheckValue(key).AsSpan().SequenceEqual(checkValue) ? key : null;
  }

  /// <summary>Encrypts or decrypts a fork in place: RC4 under archive key ‖ fork key.</summary>
  public static void Decrypt(Span<byte> data, ReadOnlySpan<byte> archiveKey, ReadOnlySpan<byte> forkKey) {
    Span<byte> key = stackalloc byte[archiveKey.Length + forkKey.Length];
    archiveKey.CopyTo(key);
    forkKey.CopyTo(key[archiveKey.Length..]);
    new Rc4(key).Transform(data);
  }
}
