using System.Security.Cryptography;

namespace Compression.Core.Crypto;

/// <summary>
/// AES-256 encryption and decryption wrapper supporting CBC and CTR modes.
/// Uses the system-provided AES implementation.
/// </summary>
public static class AesCryptor {
  /// <summary>
  /// Encrypts data using AES-256-CBC with PKCS7 padding.
  /// </summary>
  /// <param name="data">The plaintext data.</param>
  /// <param name="key">The 256-bit (32 byte) encryption key.</param>
  /// <param name="iv">The 128-bit (16 byte) initialization vector.</param>
  /// <returns>The encrypted data.</returns>
  public static byte[] EncryptCbc(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv) {
    ValidateKeyAndIv(key, iv);

    using var aes = Aes.Create();
    aes.Key = key.ToArray();
    aes.IV = iv.ToArray();
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.PKCS7;

    using var encryptor = aes.CreateEncryptor();
    return encryptor.TransformFinalBlock(data.ToArray(), 0, data.Length);
  }

  /// <summary>
  /// Decrypts data using AES-256-CBC with PKCS7 padding.
  /// </summary>
  /// <param name="data">The encrypted data.</param>
  /// <param name="key">The 256-bit (32 byte) encryption key.</param>
  /// <param name="iv">The 128-bit (16 byte) initialization vector.</param>
  /// <returns>The decrypted data.</returns>
  public static byte[] DecryptCbc(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv) {
    ValidateKeyAndIv(key, iv);

    using var aes = Aes.Create();
    aes.Key = key.ToArray();
    aes.IV = iv.ToArray();
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.PKCS7;

    using var decryptor = aes.CreateDecryptor();
    return decryptor.TransformFinalBlock(data.ToArray(), 0, data.Length);
  }

  /// <summary>
  /// Encrypts data using AES-256-CBC with no padding.
  /// Input length must be a multiple of 16 bytes.
  /// </summary>
  public static byte[] EncryptCbcNoPadding(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv) {
    ValidateKeyAndIv(key, iv);
    if (data.Length % 16 != 0)
      throw new ArgumentException("Data length must be a multiple of 16 bytes for no-padding mode.", nameof(data));

    using var aes = Aes.Create();
    aes.Key = key.ToArray();
    aes.IV = iv.ToArray();
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.None;

    using var encryptor = aes.CreateEncryptor();
    return encryptor.TransformFinalBlock(data.ToArray(), 0, data.Length);
  }

  /// <summary>
  /// Decrypts data using AES-256-CBC with no padding.
  /// Input length must be a multiple of 16 bytes.
  /// </summary>
  public static byte[] DecryptCbcNoPadding(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv) {
    ValidateKeyAndIv(key, iv);
    if (data.Length % 16 != 0)
      throw new ArgumentException("Data length must be a multiple of 16 bytes for no-padding mode.", nameof(data));

    using var aes = Aes.Create();
    aes.Key = key.ToArray();
    aes.IV = iv.ToArray();
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.None;

    using var decryptor = aes.CreateDecryptor();
    return decryptor.TransformFinalBlock(data.ToArray(), 0, data.Length);
  }

  /// <summary>
  /// Encrypts or decrypts data using AES-256-CTR mode.
  /// CTR mode is symmetric — the same operation encrypts and decrypts.
  /// </summary>
  /// <param name="data">The input data (plaintext for encryption, ciphertext for decryption).</param>
  /// <param name="key">The 256-bit (32 byte) encryption key.</param>
  /// <param name="nonce">The 128-bit (16 byte) nonce/IV for the counter.</param>
  /// <returns>The output data.</returns>
  public static byte[] TransformCtr(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce) {
    if (key.Length != 32)
      throw new ArgumentException("Key must be 32 bytes (256 bits).", nameof(key));
    if (nonce.Length != 16)
      throw new ArgumentException("Nonce must be 16 bytes (128 bits).", nameof(nonce));

    if (data.Length == 0)
      return [];

    var result = new byte[data.Length];

    using var aes = Aes.Create();
    aes.Key = key.ToArray();
    aes.Mode = CipherMode.ECB;
    aes.Padding = PaddingMode.None;

    using var encryptor = aes.CreateEncryptor();

    var counter = new byte[16];
    nonce.CopyTo(counter);

    var keystreamBlock = new byte[16];
    var processed = 0;

    while (processed < data.Length) {
      // Encrypt counter block to get keystream
      encryptor.TransformBlock(counter, 0, 16, keystreamBlock, 0);

      // XOR keystream with data
      var remaining = data.Length - processed;
      var blockLen = Math.Min(16, remaining);

      for (var i = 0; i < blockLen; i++)
        result[processed + i] = (byte)(data[processed + i] ^ keystreamBlock[i]);

      processed += blockLen;

      // Increment counter (big-endian increment of the last 8 bytes)
      IncrementCounter(counter);
    }

    return result;
  }

  private static void IncrementCounter(byte[] counter) {
    for (var i = 15; i >= 0; i--)
      if (++counter[i] != 0)
        break;
  }

  private static void ValidateKeyAndIv(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv) {
    if (key.Length != 32)
      throw new ArgumentException("Key must be 32 bytes (256 bits).", nameof(key));
    if (iv.Length != 16)
      throw new ArgumentException("IV must be 16 bytes (128 bits).", nameof(iv));
  }
}
