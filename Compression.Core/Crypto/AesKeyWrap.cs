using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Compression.Core.Crypto;

/// <summary>
/// The AES key wrap algorithm of RFC 3394 (section 2.2, index-based form) with its default
/// initial value <c>A6A6A6A6A6A6A6A6</c>: key data of 8n bytes (n ≥ 2) becomes 8(n + 1) bytes.
/// </summary>
public static class AesKeyWrap {
  private const ulong DefaultIv = 0xA6A6A6A6A6A6A6A6UL;

  /// <summary>Wraps <paramref name="keyData"/> under the 16-, 24- or 32-byte <paramref name="kek"/>.</summary>
  /// <exception cref="ArgumentException">The key data is not a multiple of 8 bytes of at least 16.</exception>
  public static byte[] Wrap(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> keyData) {
    if (keyData.Length < 16 || keyData.Length % 8 != 0)
      throw new ArgumentException("AES key wrap takes a multiple of 8 bytes, at least 16.", nameof(keyData));
    var aes = new AesBlockCipher(kek);
    var n = keyData.Length / 8;
    var output = new byte[keyData.Length + 8];
    keyData.CopyTo(output.AsSpan(8));
    var a = DefaultIv;
    Span<byte> b = stackalloc byte[16];
    for (var j = 0; j < 6; ++j)
      for (var i = 1; i <= n; ++i) {
        BinaryPrimitives.WriteUInt64BigEndian(b, a);
        output.AsSpan(8 * i, 8).CopyTo(b[8..]);
        aes.Encrypt(b);
        a = BinaryPrimitives.ReadUInt64BigEndian(b) ^ (ulong)(n * j + i);
        b[8..].CopyTo(output.AsSpan(8 * i, 8));
      }
    BinaryPrimitives.WriteUInt64BigEndian(output, a);
    return output;
  }

  /// <summary>Unwraps <paramref name="wrapped"/> under <paramref name="kek"/>.</summary>
  /// <exception cref="CryptographicException">The integrity check fails: wrong key or damaged data.</exception>
  public static byte[] Unwrap(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrapped) {
    if (wrapped.Length < 24 || wrapped.Length % 8 != 0)
      throw new CryptographicException("AES-wrapped key data is a multiple of 8 bytes, at least 24.");
    var aes = new AesBlockCipher(kek);
    var n = wrapped.Length / 8 - 1;
    var output = wrapped[8..].ToArray();
    var a = BinaryPrimitives.ReadUInt64BigEndian(wrapped);
    Span<byte> b = stackalloc byte[16];
    for (var j = 5; j >= 0; --j)
      for (var i = n; i >= 1; --i) {
        BinaryPrimitives.WriteUInt64BigEndian(b, a ^ (ulong)(n * j + i));
        output.AsSpan(8 * (i - 1), 8).CopyTo(b[8..]);
        aes.Decrypt(b);
        a = BinaryPrimitives.ReadUInt64BigEndian(b);
        b[8..].CopyTo(output.AsSpan(8 * (i - 1), 8));
      }
    if (a != DefaultIv)
      throw new CryptographicException("AES key unwrap failed its integrity check (wrong key or damaged data).");
    return output;
  }
}
