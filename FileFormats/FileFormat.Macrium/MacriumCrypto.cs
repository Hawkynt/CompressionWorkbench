#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.Macrium;

/// <summary>
/// AES-CBC + PBKDF2-HMAC-SHA256 + per-block-ESSIV-IV cryptography for Macrium
/// Reflect X images. Implemented strictly per the MIT-licensed vendor spec at
/// <see href="https://github.com/macrium/mrimgx_file_layout/blob/main/docs/ENCRYPTION.md"/>.
///
/// <para>
/// <b>Key derivation</b> (<see cref="DeriveKey"/>):
/// </para>
/// <list type="number">
///   <item><description>The 8-byte <c>imageid</c> (raw bytes, NOT hex-text) is hashed via SHA-256 to produce a 32-byte salt.</description></item>
///   <item><description><c>PBKDF2-HMAC-SHA256(password, salt, iterations)</c> with the iteration count from the JSON metadata (default 600 000) produces a 32-byte derived key — always 32 bytes regardless of the AES variant (128/192/256). For AES-128 / AES-192 only the first 16 / 24 bytes are used by the cipher.</description></item>
/// </list>
///
/// <para>
/// <b>Password validation</b> (<see cref="ComputeHmac"/>):
/// </para>
/// <para>
/// The vendor stores <c>HMAC-SHA256(key, "")</c> — i.e. an empty-message HMAC
/// keyed by the derived key — in the JSON header. The reader compares this
/// against the stored <c>_encryption.hmac</c> string; a mismatch means the
/// password is wrong.
/// </para>
///
/// <para>
/// <b>Per-block IV (ESSIV)</b> (<see cref="DeriveBlockIv"/>):
/// </para>
/// <list type="number">
///   <item><description>Pack a 16-byte plaintext block: <c>imageid[8] | (uint16 LE) disk_number | (uint16 LE) partition_number | (uint32 LE) block_index</c>.</description></item>
///   <item><description>Hash the derived key with SHA-256 to produce a 32-byte tweak key.</description></item>
///   <item><description>AES-256-ECB encrypt the 16-byte plaintext under the tweak key. The 16-byte ciphertext IS the IV.</description></item>
/// </list>
///
/// <para>
/// <b>Per-block encryption / decryption</b> (<see cref="EncryptBlock"/> /
/// <see cref="DecryptBlock"/>): standard AES-CBC, with PKCS#7 padding so the
/// ciphertext length is always a multiple of 16. The Macrium writer adds the
/// padding implicitly; on read the reader strips it after decryption.
/// </para>
/// </summary>
public static class MacriumCrypto {

  /// <summary>Default PBKDF2 iteration count per spec: 600 000. Reviewed periodically by the vendor.</summary>
  public const int DefaultPbkdf2Iterations = 600_000;

  /// <summary>AES block size in bytes. AES is always 128-bit-block regardless of key size.</summary>
  public const int AesBlockSize = 16;

  /// <summary>Imageid is always 8 raw bytes (rendered as 16 hex chars in JSON).</summary>
  public const int ImageIdSize = 8;

  /// <summary>Derived key is always 32 bytes per spec (truncated for AES-128 / AES-192 by the cipher init).</summary>
  public const int DerivedKeyLength = 32;

  /// <summary>
  /// Derives the 32-byte master key from a password using the Macrium Reflect X
  /// scheme: <c>PBKDF2-HMAC-SHA256(password, SHA256(imageid), iterations, 32)</c>.
  /// </summary>
  /// <param name="password">The user-supplied password.</param>
  /// <param name="imageId">The 8-byte raw imageid.</param>
  /// <param name="iterations">PBKDF2 iteration count (default 600 000 per spec).</param>
  /// <returns>The 32-byte derived key.</returns>
  public static byte[] DeriveKey(string password, ReadOnlySpan<byte> imageId, int iterations = DefaultPbkdf2Iterations) {
    ArgumentNullException.ThrowIfNull(password);
    if (imageId.Length != ImageIdSize)
      throw new ArgumentException($"imageId must be exactly {ImageIdSize} bytes (got {imageId.Length}).", nameof(imageId));
    ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

    Span<byte> salt = stackalloc byte[32];
    SHA256.HashData(imageId, salt);

    var passwordBytes = Encoding.UTF8.GetBytes(password);
    return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA256, DerivedKeyLength);
  }

  /// <summary>
  /// Computes the validation HMAC for a derived key. Macrium stores this as a
  /// hex string in <c>_encryption.hmac</c>; the reader compares with constant
  /// time to validate the password.
  /// </summary>
  /// <param name="derivedKey">The 32-byte PBKDF2 output from <see cref="DeriveKey"/>.</param>
  /// <returns>32 bytes of HMAC-SHA256 output over an empty message.</returns>
  /// <remarks>
  /// The vendor C++ uses <c>EVP_PKEY_new_mac_key(EVP_PKEY_HMAC, key)</c> +
  /// <c>EVP_DigestSignInit</c> + <c>EVP_DigestSignFinal</c> with no
  /// <c>EVP_DigestSignUpdate</c> call, which is HMAC-SHA256 with a zero-length
  /// message. <see cref="HMACSHA256.HashData(byte[], byte[])"/> with an empty
  /// data array gives the byte-identical result.
  /// </remarks>
  public static byte[] ComputeHmac(ReadOnlySpan<byte> derivedKey) {
    if (derivedKey.Length != DerivedKeyLength)
      throw new ArgumentException($"derivedKey must be {DerivedKeyLength} bytes.", nameof(derivedKey));
    Span<byte> hmac = stackalloc byte[32];
    HMACSHA256.HashData(derivedKey, ReadOnlySpan<byte>.Empty, hmac);
    return hmac.ToArray();
  }

  /// <summary>
  /// Validates a password against the stored HMAC. Constant-time comparison.
  /// </summary>
  public static bool ValidateHmac(ReadOnlySpan<byte> derivedKey, ReadOnlySpan<byte> expectedHmac) {
    if (expectedHmac.Length != 32) return false;
    Span<byte> hmac = stackalloc byte[32];
    HMACSHA256.HashData(derivedKey, ReadOnlySpan<byte>.Empty, hmac);
    return CryptographicOperations.FixedTimeEquals(hmac, expectedHmac);
  }

  /// <summary>
  /// Derives the AES-CBC IV for a single data block using ESSIV — encrypted
  /// salt-sector IV. See <see cref="MacriumCrypto"/> remarks for the exact
  /// byte layout.
  /// </summary>
  /// <param name="derivedKey">The PBKDF2-derived 32-byte master key.</param>
  /// <param name="imageId">The 8-byte imageid for this backup set.</param>
  /// <param name="diskNumber">Disk number (16-bit; spec stores 2 bytes).</param>
  /// <param name="partitionNumber">Partition number (16-bit).</param>
  /// <param name="blockIndex">Block index within the partition (32-bit).</param>
  /// <returns>A 16-byte IV unique per (image, disk, partition, block).</returns>
  public static byte[] DeriveBlockIv(
      ReadOnlySpan<byte> derivedKey,
      ReadOnlySpan<byte> imageId,
      int diskNumber,
      int partitionNumber,
      int blockIndex) {
    if (derivedKey.Length != DerivedKeyLength)
      throw new ArgumentException($"derivedKey must be {DerivedKeyLength} bytes.", nameof(derivedKey));
    if (imageId.Length != ImageIdSize)
      throw new ArgumentException($"imageId must be {ImageIdSize} bytes.", nameof(imageId));

    // Pack the 16-byte plaintext "data" block exactly as the vendor C++ does:
    //   memcpy(data, imageid, 8);
    //   memcpy(data + 8,  &disk_number,      2);
    //   memcpy(data + 10, &partition_number, 2);
    //   memcpy(data + 12, &block_index,      4);
    Span<byte> plain = stackalloc byte[AesBlockSize];
    imageId.CopyTo(plain[..8]);
    BinaryPrimitives.WriteUInt16LittleEndian(plain.Slice(8, 2), (ushort)diskNumber);
    BinaryPrimitives.WriteUInt16LittleEndian(plain.Slice(10, 2), (ushort)partitionNumber);
    BinaryPrimitives.WriteInt32LittleEndian(plain.Slice(12, 4), blockIndex);

    // Tweak key = SHA-256(derived key) — always 32 bytes => AES-256-ECB.
    Span<byte> tweakKey = stackalloc byte[32];
    SHA256.HashData(derivedKey, tweakKey);

    using var aes = Aes.Create();
    aes.Key = tweakKey.ToArray();
    aes.Mode = CipherMode.ECB;
    aes.Padding = PaddingMode.None;

    var iv = new byte[AesBlockSize];
    using var enc = aes.CreateEncryptor();
    enc.TransformBlock(plain.ToArray(), 0, AesBlockSize, iv, 0);
    return iv;
  }

  /// <summary>
  /// AES-CBC encrypts a plaintext payload with PKCS#7 padding. Key length
  /// selects the AES variant: 16=AES-128, 24=AES-192, 32=AES-256.
  /// </summary>
  /// <param name="plaintext">The raw plaintext bytes.</param>
  /// <param name="key">The AES key (16, 24, or 32 bytes — usually a truncation of the 32-byte derived master).</param>
  /// <param name="iv">The 16-byte CBC IV, typically from <see cref="DeriveBlockIv"/>.</param>
  /// <returns>The ciphertext (always a multiple of 16 bytes).</returns>
  public static byte[] EncryptBlock(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv) {
    EnsureValidKeyLength(key);
    if (iv.Length != AesBlockSize)
      throw new ArgumentException($"iv must be {AesBlockSize} bytes.", nameof(iv));

    using var aes = Aes.Create();
    aes.Key = key.ToArray();
    aes.IV = iv.ToArray();
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.PKCS7;

    using var enc = aes.CreateEncryptor();
    return enc.TransformFinalBlock(plaintext.ToArray(), 0, plaintext.Length);
  }

  /// <summary>
  /// AES-CBC decrypts a ciphertext payload, stripping PKCS#7 padding. Key
  /// length selects the AES variant.
  /// </summary>
  public static byte[] DecryptBlock(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv) {
    EnsureValidKeyLength(key);
    if (iv.Length != AesBlockSize)
      throw new ArgumentException($"iv must be {AesBlockSize} bytes.", nameof(iv));
    if (ciphertext.Length == 0 || ciphertext.Length % AesBlockSize != 0)
      throw new ArgumentException($"ciphertext length must be a positive multiple of {AesBlockSize}.", nameof(ciphertext));

    using var aes = Aes.Create();
    aes.Key = key.ToArray();
    aes.IV = iv.ToArray();
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.PKCS7;

    using var dec = aes.CreateDecryptor();
    return dec.TransformFinalBlock(ciphertext.ToArray(), 0, ciphertext.Length);
  }

  /// <summary>
  /// Parses a hex string (with or without leading whitespace) into bytes. Used
  /// for <c>imageid</c> (always 16 hex chars => 8 bytes) and the
  /// <c>_encryption.hmac</c> field (always 64 hex chars => 32 bytes).
  /// </summary>
  public static byte[] HexToBytes(string hex) {
    ArgumentNullException.ThrowIfNull(hex);
    var trimmed = hex.Trim();
    if (trimmed.Length % 2 != 0)
      throw new ArgumentException($"hex string length must be even (got {trimmed.Length}).", nameof(hex));
    var result = new byte[trimmed.Length / 2];
    for (var i = 0; i < result.Length; ++i) {
      var hi = HexNibble(trimmed[i * 2]);
      var lo = HexNibble(trimmed[i * 2 + 1]);
      result[i] = (byte)((hi << 4) | lo);
    }
    return result;
  }

  /// <summary>Returns lowercase hex without separators — matches Macrium's JSON formatting.</summary>
  public static string BytesToHex(ReadOnlySpan<byte> data) {
    var sb = new StringBuilder(data.Length * 2);
    foreach (var b in data) sb.Append(b.ToString("x2"));
    return sb.ToString();
  }

  private static int HexNibble(char c) => c switch {
    >= '0' and <= '9' => c - '0',
    >= 'a' and <= 'f' => c - 'a' + 10,
    >= 'A' and <= 'F' => c - 'A' + 10,
    _ => throw new ArgumentException($"invalid hex digit '{c}'.")
  };

  private static void EnsureValidKeyLength(ReadOnlySpan<byte> key) {
    if (key.Length is not (16 or 24 or 32))
      throw new ArgumentException("AES key must be 16, 24, or 32 bytes.", nameof(key));
  }
}
