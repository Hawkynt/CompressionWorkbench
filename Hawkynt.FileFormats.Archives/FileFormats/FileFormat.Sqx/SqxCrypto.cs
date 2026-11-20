using System.Security.Cryptography;
using System.Text;
using Compression.Core.Crypto;

namespace FileFormat.Sqx;

/// <summary>
/// SQX encryption: file data with AES-128 in ECB mode (padded to whole 16-byte blocks, the padding
/// unspecified), archive headers with Blowfish in ECB mode on little-endian 32-bit halves; both
/// keyed with the 16-byte MD5 of the password.
/// </summary>
internal static class SqxCrypto {
  static SqxCrypto() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

  /// <summary>The keys a password may stand for: its UTF-8 bytes, and for a non-ASCII password also its Windows-1252 bytes.</summary>
  public static IReadOnlyList<byte[]> Keys(string password) {
    var keys = new List<byte[]> { MD5.HashData(Encoding.UTF8.GetBytes(password)) };
    if (password.Any(c => c > 0x7F))
      keys.Add(MD5.HashData(Encoding.GetEncoding(1252).GetBytes(password)));
    return keys;
  }

  public static byte[] DecryptData(ReadOnlySpan<byte> data, byte[] key) {
    using var aes = ManagedAes.Create();
    aes.Key = key;
    return aes.DecryptEcb(data, PaddingMode.None);
  }

  public static byte[] DecryptHeader(ReadOnlySpan<byte> data, byte[] key) {
    var output = data.ToArray();
    var bf = new Blowfish(key);
    for (var i = 0; i + 8 <= output.Length; i += 8) {
      var block = output.AsSpan(i, 8);
      block[..4].Reverse();
      block[4..].Reverse();
      bf.Decrypt(block);
      block[..4].Reverse();
      block[4..].Reverse();
    }
    return output;
  }
}
