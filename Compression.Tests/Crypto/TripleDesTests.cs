using System.Security.Cryptography;
using Compression.Core.Crypto;

namespace Compression.Tests.Crypto;

/// <summary>
/// <see cref="TripleDes"/> against single-DES known answers (a bundle of three equal keys is single DES) and
/// against .NET's TripleDES as the oracle for random two- and three-key bundles, in both directions.
/// </summary>
[TestFixture]
public sealed class TripleDesTests {
  // J. Orlin Grabbe, "The DES Algorithm Illustrated"; FIPS 81 appendix B ("Now is t").
  [TestCase("133457799bbcdff1", "0123456789abcdef", "85e813540f0ab405")]
  [TestCase("0123456789abcdef", "4e6f772069732074", "3fa40e8a984d4815")]
  public void GivenASingleDesKnownAnswer_WhenAllThreeKeysAreThatKey_ThenTheCiphertextMatchesAndDecryptsBack(string key, string plain, string cipher) {
    var k = Convert.FromHexString(key);
    var des = new TripleDes([.. k, .. k, .. k]);
    var block = Convert.FromHexString(plain);
    des.Encrypt(block);
    Assert.That(Convert.ToHexStringLower(block), Is.EqualTo(cipher));
    des.Decrypt(block);
    Assert.That(Convert.ToHexStringLower(block), Is.EqualTo(plain));
  }

  [TestCase(16)]
  [TestCase(24)]
  public void GivenRandomKeyBundles_WhenTripleDesEncryptsBlocks_ThenItMatchesTheFrameworkAndDecryptsBack(int keyLength) {
    var random = new Random(4645 + keyLength);
    using var framework = TripleDES.Create();
    framework.Mode = CipherMode.ECB;
    framework.Padding = PaddingMode.None;
    for (var round = 0; round < 200; ++round) {
      var key = new byte[keyLength];
      random.NextBytes(key);
      try {
        // OpenSSL, .NET's provider on Linux, refuses two-key bundles: the oracle is handed k1 k2 k1, which is
        // what a two-key bundle means.
        framework.Key = keyLength == 16 ? [.. key, .. key.AsSpan(0, 8)] : key;
      } catch (CryptographicException) {
        continue; // a bundle .NET calls weak; TripleDes takes it, the oracle does not
      }
      var data = new byte[8 * random.Next(1, 9)];
      random.NextBytes(data);
      var ours = data.ToArray();
      var des = new TripleDes(key);
      for (var i = 0; i < ours.Length; i += 8)
        des.Encrypt(ours.AsSpan(i, 8));
      Assert.That(ours, Is.EqualTo(framework.EncryptEcb(data, PaddingMode.None)), $"{keyLength}-byte bundle, round {round}");
      for (var i = 0; i < ours.Length; i += 8)
        des.Decrypt(ours.AsSpan(i, 8));
      Assert.That(ours, Is.EqualTo(data), $"round trip, {keyLength}-byte bundle, round {round}");
    }
  }
}
