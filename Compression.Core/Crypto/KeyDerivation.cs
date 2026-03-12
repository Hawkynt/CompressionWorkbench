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

    for (long i = 0; i < numIterations; i++) {
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

}
