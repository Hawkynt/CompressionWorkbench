using System.Security.Cryptography;
using Compression.Core.Checksums;
using Compression.Core.Crypto;

namespace FileFormat.Zip;

/// <summary>
/// ZIP encryption method selection.
/// </summary>
public enum ZipEncryptionMethod {
  /// <summary>No encryption.</summary>
  None,

  /// <summary>WinZip AES-256 encryption (AE-2).</summary>
  Aes256,

  /// <summary>Traditional PKZIP encryption (weak, for compatibility).</summary>
  PkzipTraditional
}

/// <summary>
/// WinZip AES encryption/decryption for ZIP entries (AE-1/AE-2).
/// Uses PBKDF2-HMAC-SHA1 for key derivation, AES-CTR for encryption,
/// and HMAC-SHA1 (truncated to 10 bytes) for authentication.
/// </summary>
internal static class ZipAesEncryption {
  /// <summary>WinZip AES extra field tag.</summary>
  public const ushort ExtraFieldTag = 0x9901;

  /// <summary>AE-2 version (CRC not stored, authentication via HMAC only).</summary>
  public const ushort AeVersion = 2;

  /// <summary>AES-256 key strength identifier.</summary>
  public const byte AesStrength256 = 3;

  /// <summary>Salt length for AES-256.</summary>
  public const int SaltLength = 16;

  /// <summary>Password verification value length.</summary>
  public const int PasswordVerifyLength = 2;

  /// <summary>Authentication code length (HMAC-SHA1 truncated).</summary>
  public const int AuthCodeLength = 10;

  /// <summary>PBKDF2 iteration count.</summary>
  private const int Pbkdf2Iterations = 1000;

  /// <summary>
  /// Encrypts data using WinZip AES-256.
  /// </summary>
  /// <param name="data">The (already compressed) data to encrypt.</param>
  /// <param name="password">The password.</param>
  /// <returns>Encrypted payload: salt(16) + passwordVerify(2) + ciphertext + authCode(10).</returns>
  public static byte[] Encrypt(byte[] data, string password) {
    var salt = RandomNumberGenerator.GetBytes(SaltLength);

    // PBKDF2-HMAC-SHA1: derive 66 bytes = AES key(32) + HMAC key(32) + verify(2)
    var derived = KeyDerivation.Pbkdf2Sha1(password, salt, Pbkdf2Iterations, 66);
    var aesKey = derived[..32];
    var hmacKey = derived[32..64];
    var passwordVerify = derived[64..66];

    // Encrypt with AES-CTR (WinZip uses LE counter starting at 1)
    var ciphertext = AesCtrWinZip(data, aesKey);

    // Compute HMAC-SHA1 over ciphertext, truncated to 10 bytes
    var authCode = ComputeHmacSha1(hmacKey, ciphertext);

    // Build output: salt + verify + ciphertext + authCode
    var result = new byte[SaltLength + PasswordVerifyLength + ciphertext.Length + AuthCodeLength];
    salt.CopyTo(result.AsSpan());
    passwordVerify.CopyTo(result.AsSpan(SaltLength));
    ciphertext.CopyTo(result.AsSpan(SaltLength + PasswordVerifyLength));
    authCode.AsSpan(0, AuthCodeLength).CopyTo(result.AsSpan(SaltLength + PasswordVerifyLength + ciphertext.Length));
    return result;
  }

  /// <summary>
  /// Decrypts data using WinZip AES-256.
  /// </summary>
  /// <param name="encryptedPayload">The full encrypted payload from the ZIP entry.</param>
  /// <param name="password">The password.</param>
  /// <returns>The decrypted (still compressed) data.</returns>
  public static byte[] Decrypt(byte[] encryptedPayload, string password) {
    if (encryptedPayload.Length < SaltLength + PasswordVerifyLength + AuthCodeLength)
      throw new InvalidDataException("Encrypted data too short for WinZip AES.");

    var salt = encryptedPayload[..SaltLength];

    var derived = KeyDerivation.Pbkdf2Sha1(password, salt, Pbkdf2Iterations, 66);
    var aesKey = derived[..32];
    var hmacKey = derived[32..64];
    var expectedVerify = derived[64..66];

    // Verify password
    var actualVerify = encryptedPayload[SaltLength..(SaltLength + PasswordVerifyLength)];
    if (!actualVerify.AsSpan().SequenceEqual(expectedVerify))
      throw new InvalidDataException("Wrong password (verification bytes mismatch).");

    var ciphertextStart = SaltLength + PasswordVerifyLength;
    var ciphertextLen = encryptedPayload.Length - ciphertextStart - AuthCodeLength;
    var ciphertext = encryptedPayload[ciphertextStart..(ciphertextStart + ciphertextLen)];

    // Verify HMAC
    var expectedAuth = encryptedPayload[(ciphertextStart + ciphertextLen)..];
    var actualAuth = ComputeHmacSha1(hmacKey, ciphertext);
    if (!expectedAuth.AsSpan().SequenceEqual(actualAuth.AsSpan(0, AuthCodeLength)))
      throw new InvalidDataException("Authentication code mismatch (data corrupted or wrong password).");

    return AesCtrWinZip(ciphertext, aesKey);
  }

  /// <summary>
  /// Builds the WinZip AES extra field (0x9901).
  /// </summary>
  public static byte[] BuildExtraField(ZipCompressionMethod actualMethod) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(ExtraFieldTag);   // tag
    w.Write((ushort)7);       // data size
    w.Write(AeVersion);       // AE version
    w.Write((byte)'A');       // vendor ID
    w.Write((byte)'E');
    w.Write(AesStrength256);  // AES strength (3 = 256-bit)
    w.Write((ushort)actualMethod); // actual compression method
    return ms.ToArray();
  }

  /// <summary>
  /// Parses the WinZip AES extra field to extract the actual compression method.
  /// </summary>
  public static ZipCompressionMethod ParseExtraField(byte[]? extraField) {
    if (extraField == null)
      return ZipCompressionMethod.Store;

    var pos = 0;
    while (pos + 4 <= extraField.Length) {
      var tag = BitConverter.ToUInt16(extraField, pos);
      var size = BitConverter.ToUInt16(extraField, pos + 2);
      pos += 4;

      if (tag == ExtraFieldTag && size >= 7 && pos + size <= extraField.Length) {
        // Skip version(2) + vendor(2) + strength(1), read actual method(2)
        return (ZipCompressionMethod)BitConverter.ToUInt16(extraField, pos + 5);
      }
      pos += size;
    }

    return ZipCompressionMethod.Store;
  }

  /// <summary>
  /// AES-CTR with WinZip's little-endian counter starting at 1.
  /// WinZip increments the counter in little-endian byte order.
  /// </summary>
  private static byte[] AesCtrWinZip(byte[] data, byte[] key) {
    if (data.Length == 0)
      return [];

    var result = new byte[data.Length];
    using var aes = System.Security.Cryptography.Aes.Create();
    aes.Key = key;
    aes.Mode = System.Security.Cryptography.CipherMode.ECB;
    aes.Padding = System.Security.Cryptography.PaddingMode.None;
    using var encryptor = aes.CreateEncryptor();

    var counter = new byte[16];
    counter[0] = 1; // LE counter starts at 1
    var keystreamBlock = new byte[16];
    var processed = 0;

    while (processed < data.Length) {
      encryptor.TransformBlock(counter, 0, 16, keystreamBlock, 0);

      var blockLen = Math.Min(16, data.Length - processed);
      for (var i = 0; i < blockLen; ++i)
        result[processed + i] = (byte)(data[processed + i] ^ keystreamBlock[i]);
      processed += blockLen;

      // Increment counter in little-endian order
      for (var i = 0; i < 16; ++i)
        if (++counter[i] != 0)
          break;
    }

    return result;
  }

  /// <summary>
  /// Computes HMAC-SHA1 and returns the full 20-byte digest.
  /// </summary>
  private static byte[] ComputeHmacSha1(byte[] key, byte[] data) {
    using var hmac = new HMACSHA1(key);
    return hmac.ComputeHash(data);
  }
}

/// <summary>
/// Traditional PKZIP encryption (weak XOR-based cipher).
/// </summary>
internal static class ZipTraditionalEncryption {
  /// <summary>Encryption header size.</summary>
  public const int HeaderSize = 12;

  /// <summary>
  /// Encrypts data using traditional PKZIP encryption.
  /// </summary>
  /// <param name="data">The (already compressed) data.</param>
  /// <param name="password">The password.</param>
  /// <param name="crc">The CRC-32 of the original uncompressed data (high byte used for verification).</param>
  /// <returns>The encrypted data with 12-byte header prepended.</returns>
  public static byte[] Encrypt(byte[] data, string password, uint crc) {
    uint key0 = 0x12345678, key1 = 0x23456789, key2 = 0x34567890;

    // Initialize keys with password
    foreach (var c in password)
      UpdateKeys(ref key0, ref key1, ref key2, (byte)c);

    var result = new byte[HeaderSize + data.Length];

    // Generate random encryption header (12 bytes)
    var header = RandomNumberGenerator.GetBytes(HeaderSize);
    // Last byte of header must be high byte of CRC for verification
    header[HeaderSize - 1] = (byte)(crc >> 24);

    // Encrypt header
    for (var i = 0; i < HeaderSize; ++i) {
      var keyByte = DecryptByte(key2);
      result[i] = (byte)(header[i] ^ keyByte);
      UpdateKeys(ref key0, ref key1, ref key2, header[i]);
    }

    // Encrypt data
    for (var i = 0; i < data.Length; ++i) {
      var keyByte = DecryptByte(key2);
      result[HeaderSize + i] = (byte)(data[i] ^ keyByte);
      UpdateKeys(ref key0, ref key1, ref key2, data[i]);
    }

    return result;
  }

  /// <summary>
  /// Decrypts data using traditional PKZIP encryption.
  /// </summary>
  /// <param name="encryptedPayload">The encrypted data including the 12-byte header.</param>
  /// <param name="password">The password.</param>
  /// <param name="crc">The expected CRC-32 (for header verification).</param>
  /// <returns>The decrypted data (without the 12-byte header).</returns>
  public static byte[] Decrypt(byte[] encryptedPayload, string password, uint crc) {
    if (encryptedPayload.Length < HeaderSize)
      throw new InvalidDataException("Encrypted data too short for PKZIP encryption header.");

    uint key0 = 0x12345678, key1 = 0x23456789, key2 = 0x34567890;

    // Initialize keys with password
    foreach (var c in password)
      UpdateKeys(ref key0, ref key1, ref key2, (byte)c);

    // Decrypt header
    byte lastHeaderByte = 0;
    for (var i = 0; i < HeaderSize; ++i) {
      var keyByte = DecryptByte(key2);
      var plain = (byte)(encryptedPayload[i] ^ keyByte);
      UpdateKeys(ref key0, ref key1, ref key2, plain);
      if (i == HeaderSize - 1)
        lastHeaderByte = plain;
    }

    // Verify: last header byte should match high byte of CRC
    if (lastHeaderByte != (byte)(crc >> 24))
      throw new InvalidDataException("Wrong password (PKZIP header check byte mismatch).");

    // Decrypt data
    var result = new byte[encryptedPayload.Length - HeaderSize];
    for (var i = 0; i < result.Length; ++i) {
      var keyByte = DecryptByte(key2);
      result[i] = (byte)(encryptedPayload[HeaderSize + i] ^ keyByte);
      UpdateKeys(ref key0, ref key1, ref key2, result[i]);
    }

    return result;
  }

  private static byte DecryptByte(uint key2) {
    var temp = (ushort)((key2 & 0xFFFF) | 2);
    return (byte)((temp * (temp ^ 1)) >> 8);
  }

  private static void UpdateKeys(ref uint key0, ref uint key1, ref uint key2, byte b) {
    key0 = Crc32Update(key0, b);
    key1 = key1 + (key0 & 0xFF);
    key1 = key1 * 134775813 + 1;
    key2 = Crc32Update(key2, (byte)(key1 >> 24));
  }

  /// <summary>
  /// Precomputed CRC-32 IEEE lookup table for the PKZIP key update function.
  /// </summary>
  private static readonly uint[] CrcTable = BuildCrcTable();

  private static uint[] BuildCrcTable() {
    var table = new uint[256];
    for (uint i = 0; i < 256; ++i) {
      var crc = i;
      for (var j = 0; j < 8; ++j)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
      table[i] = crc;
    }
    return table;
  }

  private static uint Crc32Update(uint crc, byte b) =>
    CrcTable[((crc ^ b) & 0xFF)] ^ (crc >> 8);
}
