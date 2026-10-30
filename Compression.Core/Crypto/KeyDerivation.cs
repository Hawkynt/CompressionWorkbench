using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Compression.Core.Crypto;

/// <summary>
/// Key derivation functions for password-based encryption.
/// </summary>
public static class KeyDerivation {
  /// <summary>
  /// Derives a key using PBKDF2 with HMAC-SHA256.
  /// Used by ZIP AES encryption (WinZip AE-1/AE-2).
  /// </summary>
  /// <param name="password">The password string.</param>
  /// <param name="salt">The random salt bytes.</param>
  /// <param name="iterations">Number of PBKDF2 iterations.</param>
  /// <param name="keyLength">The desired key length in bytes.</param>
  /// <returns>The derived key bytes.</returns>
  public static byte[] Pbkdf2Sha256(string password, ReadOnlySpan<byte> salt, int iterations, int keyLength) {
    ArgumentNullException.ThrowIfNull(password);
    ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(keyLength, 1);

    var passwordBytes = Encoding.UTF8.GetBytes(password);
    return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA256, keyLength);
  }

  /// <summary>
  /// Derives a key using PBKDF2 with HMAC-SHA1.
  /// Used by older ZIP encryption and WinZip AE-1.
  /// </summary>
  /// <param name="password">The password string.</param>
  /// <param name="salt">The random salt bytes.</param>
  /// <param name="iterations">Number of PBKDF2 iterations.</param>
  /// <param name="keyLength">The desired key length in bytes.</param>
  /// <returns>The derived key bytes.</returns>
  public static byte[] Pbkdf2Sha1(string password, ReadOnlySpan<byte> salt, int iterations, int keyLength) {
    ArgumentNullException.ThrowIfNull(password);
    ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(keyLength, 1);

    var passwordBytes = Encoding.UTF8.GetBytes(password);
    return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA1, keyLength);
  }

  /// <summary>
  /// Derives a key using the 7z AES key derivation (SHA-256 iterated).
  /// 7z uses: key = SHA256^(2^numCyclesPower)(salt + password_utf16le)
  /// </summary>
  /// <param name="password">The password string.</param>
  /// <param name="salt">The salt bytes.</param>
  /// <param name="numCyclesPower">The number of cycles power (0-24). Iterations = 2^numCyclesPower.</param>
  /// <returns>The 32-byte derived key.</returns>
  public static byte[] SevenZipDeriveKey(string password, ReadOnlySpan<byte> salt, int numCyclesPower) {
    ArgumentNullException.ThrowIfNull(password);
    ArgumentOutOfRangeException.ThrowIfLessThan(numCyclesPower, 0);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(numCyclesPower, 24);

    var passwordBytes = Encoding.Unicode.GetBytes(password); // UTF-16LE
    var numIterations = 1L << numCyclesPower;

    // Combine salt + password
    var combined = new byte[salt.Length + passwordBytes.Length];
    salt.CopyTo(combined);
    passwordBytes.CopyTo(combined.AsSpan(salt.Length));

    using var sha256 = SHA256.Create();

    // Iteratively hash: hash = SHA256(combined + counter_le64)
    // Actually 7z does: for i in 0..numIterations-1: hash_state.update(salt + password + i_as_le64)
    var hashInput = new byte[combined.Length + 8];
    combined.CopyTo(hashInput.AsSpan());

    sha256.Initialize();

    for (long i = 0; i < numIterations; ++i) {
      // Write iteration counter as little-endian 8 bytes
      BitConverter.TryWriteBytes(hashInput.AsSpan(combined.Length), i);

      sha256.TransformBlock(hashInput, 0, hashInput.Length, null, 0);
    }

    sha256.TransformFinalBlock([], 0, 0);
    return sha256.Hash!;
  }

  /// <summary>
  /// Derives a key using the RAR5 key derivation (PBKDF2-HMAC-SHA256 with specific parameters).
  /// </summary>
  /// <param name="password">The password string.</param>
  /// <param name="salt">The 16-byte salt.</param>
  /// <param name="iterations">Number of iterations (typically 2^15 = 32768).</param>
  /// <returns>The 32-byte derived key.</returns>
  public static byte[] Rar5DeriveKey(string password, ReadOnlySpan<byte> salt, int iterations) => Pbkdf2Sha256(password, salt, iterations, 32);

  /// <summary>
  /// Derives a 16-byte AES-128 key and 16-byte IV for RAR3/4 decryption.
  /// RAR3 iterates SHA-1 262144 times over (password_utf16le + salt + 3-byte counter); the IV takes
  /// byte 19 of every 16384th intermediate digest and the key is the first four digest words with
  /// their bytes reversed.
  /// </summary>
  /// <remarks>
  /// Converted from 7-Zip's <c>CPP/7zip/Crypto/RarAes.cpp</c> (<c>CDecoder::CalcKey</c>, Igor Pavlov,
  /// LGPL-2.1-or-later), including the quirk of RAR 3.x's own SHA-1, which overwrote the password
  /// buffer with expanded message words whenever one update spanned more than a whole 64-byte
  /// block (passwords longer than 28 characters with salt). At most 127 password characters count.
  /// </remarks>
  /// <param name="password">The password string.</param>
  /// <param name="salt">The 8-byte salt from the file header, or empty when the header carries none.</param>
  /// <returns>A tuple of (16-byte key, 16-byte IV).</returns>
  public static (byte[] Key, byte[] Iv) Rar3DeriveKey(string password, ReadOnlySpan<byte> salt) {
    ArgumentNullException.ThrowIfNull(password);
    const int maxPasswordChars = 127;
    const int blockSize = 64;

    var passwordBytes = Encoding.Unicode.GetBytes(password.Length > maxPasswordChars ? password[..maxPasswordChars] : password);
    var buffer = new byte[passwordBytes.Length + salt.Length + blockSize];
    passwordBytes.CopyTo(buffer, 0);
    salt.CopyTo(buffer.AsSpan(passwordBytes.Length));
    var rawSize = passwordBytes.Length + salt.Length;

    const int iterations = 0x40000; // 262144
    var iv = new byte[16];
    var sha1 = new Checksums.Sha1();
    var pos = 0u;
    for (var i = 0; i < iterations; ++i) {
      sha1.Update(buffer.AsSpan(0, rawSize));
      var endPos = (pos + (uint)rawSize) & ~(uint)(blockSize - 1);
      if (endPos > pos + blockSize) {
        var curPos = (pos & ~(uint)(blockSize - 1)) + blockSize;
        do {
          ExpandSha1Block(buffer.AsSpan((int)(curPos - pos), blockSize));
          curPos += blockSize;
        } while (curPos != endPos);
      }
      pos += (uint)rawSize;

      ReadOnlySpan<byte> counter = [(byte)i, (byte)(i >> 8), (byte)(i >> 16)];
      sha1.Update(counter);
      pos += 3;

      if (i % 0x4000 == 0) {
        var clone = sha1.Clone();
        clone.Finish();
        iv[i / 0x4000] = clone.Hash[19];
      }
    }

    sha1.Finish();
    var digest = sha1.Hash;
    var key = new byte[16];
    for (var w = 0; w < 4; ++w)
      for (var j = 0; j < 4; ++j)
        key[w * 4 + j] = digest[w * 4 + 3 - j];
    return (key, iv);
  }

  /// <summary>
  /// RAR 3.x's SHA-1 expanded the message schedule in place: the block's 16 words are replaced by
  /// words 64..79 of the expansion, stored little-endian.
  /// </summary>
  private static void ExpandSha1Block(Span<byte> block) {
    Span<uint> w = stackalloc uint[80];
    for (var i = 0; i < 16; ++i)
      w[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);
    for (var i = 16; i < 80; ++i)
      w[i] = BitOperations.RotateLeft(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
    for (var i = 0; i < 16; ++i)
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(block[(i * 4)..], w[64 + i]);
  }

}
