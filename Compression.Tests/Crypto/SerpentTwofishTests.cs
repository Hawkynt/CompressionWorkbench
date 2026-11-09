using Compression.Core.Crypto;

namespace Compression.Tests.Crypto;

/// <summary>
/// Serpent and Twofish against published known-answer vectors: the Serpent vectors of the
/// LibTomCrypt self-test (little-endian byte order, as FreeArc and NESSIE use it) and the Twofish
/// vectors of the algorithm's paper, in both directions, plus random round trips for every key size.
/// </summary>
[TestFixture]
public sealed class SerpentTwofishTests {
  [TestCase("00000000000000000000000000000000", "d29d576fcea3a3a7ed9099f29273d78e", "b2288b968ae8b08648d1ce9606fd992d")]
  [TestCase("00000000000000000000000000000000", "d29d576fcea3a3a7ed9099f26d8c2871", "563a8403ff5309d62370b1dcf5a11edd")]
  [TestCase("ffeeddccbbaa99887766554433221100", "1032547698badcfeefcdab8967452301", "d5baa00a4bb9d8a7c981c8dc90d89d92")]
  [TestCase("000000000000000000000000000000000000000000000000", "d29d576fceaba3a7ed9899f2927bd78e", "130e353e1037c22405e8faefb2c3c3e9")]
  [TestCase("8899aabbccddeeffffeeddccbbaa99887766554433221100", "1032547698badcfeefcdab8967452301", "da860842b720802bf404a4c71034879a")]
  [TestCase("0000000000000000000000000000000000000000000000000000000000000000", "92074732d84e1841a013a0034c52bf50", "81c4eb7b8ad9a8d0f2aa5d7bd626b560")]
  [TestCase("00112233445566778899aabbccddeeffffeeddccbbaa99887766554433221100", "1032547698badcfeefcdab8967452301", "93df9a3cafe387bd999eebe393a17fca")]
  public void GivenAKnownAnswerVector_WhenSerpentEncrypts_ThenTheCiphertextMatches(string key, string plain, string cipher) {
    var block = Convert.FromHexString(plain);
    new Serpent(Convert.FromHexString(key)).Encrypt(block);
    Assert.That(Convert.ToHexStringLower(block), Is.EqualTo(cipher));
  }

  [TestCase("00000000000000000000000000000000", "00000000000000000000000000000000", "9f589f5cf6122c32b6bfec2f2ae8c35a")]
  [TestCase("0123456789abcdeffedcba98765432100011223344556677", "00000000000000000000000000000000", "cfd1d2e5a9be9cdf501f13b892bd2248")]
  [TestCase("0123456789abcdeffedcba987654321000112233445566778899aabbccddeeff", "00000000000000000000000000000000", "37527be0052334b89f0cfccae87cfa20")]
  public void GivenAKnownAnswerVector_WhenTwofishEncrypts_ThenTheCiphertextMatches(string key, string plain, string cipher) {
    var block = Convert.FromHexString(plain);
    new Twofish(Convert.FromHexString(key)).Encrypt(block);
    Assert.That(Convert.ToHexStringLower(block), Is.EqualTo(cipher));
  }

  [TestCase("00000000000000000000000000000000", "d29d576fcea3a3a7ed9099f29273d78e", "b2288b968ae8b08648d1ce9606fd992d")]
  [TestCase("00000000000000000000000000000000", "d29d576fcea3a3a7ed9099f26d8c2871", "563a8403ff5309d62370b1dcf5a11edd")]
  [TestCase("ffeeddccbbaa99887766554433221100", "1032547698badcfeefcdab8967452301", "d5baa00a4bb9d8a7c981c8dc90d89d92")]
  [TestCase("000000000000000000000000000000000000000000000000", "d29d576fceaba3a7ed9899f2927bd78e", "130e353e1037c22405e8faefb2c3c3e9")]
  [TestCase("8899aabbccddeeffffeeddccbbaa99887766554433221100", "1032547698badcfeefcdab8967452301", "da860842b720802bf404a4c71034879a")]
  [TestCase("0000000000000000000000000000000000000000000000000000000000000000", "92074732d84e1841a013a0034c52bf50", "81c4eb7b8ad9a8d0f2aa5d7bd626b560")]
  [TestCase("00112233445566778899aabbccddeeffffeeddccbbaa99887766554433221100", "1032547698badcfeefcdab8967452301", "93df9a3cafe387bd999eebe393a17fca")]
  public void GivenAKnownAnswerVector_WhenSerpentDecrypts_ThenThePlaintextMatches(string key, string plain, string cipher) {
    var block = Convert.FromHexString(cipher);
    new Serpent(Convert.FromHexString(key)).Decrypt(block);
    Assert.That(Convert.ToHexStringLower(block), Is.EqualTo(plain));
  }

  [TestCase("00000000000000000000000000000000", "00000000000000000000000000000000", "9f589f5cf6122c32b6bfec2f2ae8c35a")]
  [TestCase("0123456789abcdeffedcba98765432100011223344556677", "00000000000000000000000000000000", "cfd1d2e5a9be9cdf501f13b892bd2248")]
  [TestCase("0123456789abcdeffedcba987654321000112233445566778899aabbccddeeff", "00000000000000000000000000000000", "37527be0052334b89f0cfccae87cfa20")]
  public void GivenAKnownAnswerVector_WhenTwofishDecrypts_ThenThePlaintextMatches(string key, string plain, string cipher) {
    var block = Convert.FromHexString(cipher);
    new Twofish(Convert.FromHexString(key)).Decrypt(block);
    Assert.That(Convert.ToHexStringLower(block), Is.EqualTo(plain));
  }

  [Test]
  public void GivenRandomKeysAndBlocks_WhenSerpentEncryptsAndDecrypts_ThenTheBlockIsRestored() {
    var random = new Random(3713);
    for (var keyLength = 1; keyLength <= 32; ++keyLength)
      BlockCipherRoundTrip.Assert(random, keyLength, Serpent.BlockSize, key => {
        var cipher = new Serpent(key);
        return (cipher.Encrypt, cipher.Decrypt);
      });
  }

  [TestCase(16)]
  [TestCase(24)]
  [TestCase(32)]
  public void GivenRandomKeysAndBlocks_WhenTwofishEncryptsAndDecrypts_ThenTheBlockIsRestored(int keyLength)
    => BlockCipherRoundTrip.Assert(new Random(keyLength), keyLength, Twofish.BlockSize, key => {
      var cipher = new Twofish(key);
      return (cipher.Encrypt, cipher.Decrypt);
    });

  [TestCase(0)]
  [TestCase(33)]
  public void GivenASerpentKeyOutOfRange_WhenConstructed_ThenItIsRejected(int length)
    => Assert.Throws<ArgumentException>(() => _ = new Serpent(new byte[length]));

  [TestCase(8)]
  [TestCase(20)]
  [TestCase(40)]
  public void GivenATwofishKeyOfAnotherLength_WhenConstructed_ThenItIsRejected(int length)
    => Assert.Throws<ArgumentException>(() => _ = new Twofish(new byte[length]));

  [Test]
  public void GivenAWrongBlockLength_WhenEncrypted_ThenItIsRejected() {
    Assert.Throws<ArgumentException>(() => new Serpent(new byte[16]).Encrypt(new byte[15]));
    Assert.Throws<ArgumentException>(() => new Twofish(new byte[16]).Encrypt(new byte[17]));
    Assert.Throws<ArgumentException>(() => new Serpent(new byte[16]).Decrypt(new byte[17]));
    Assert.Throws<ArgumentException>(() => new Twofish(new byte[16]).Decrypt(new byte[15]));
  }
}
