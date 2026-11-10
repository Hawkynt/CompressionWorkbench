using Compression.Core.Crypto;

namespace Compression.Tests.Crypto;

/// <summary>
/// Camellia, Kuznyechik and CAST-128 against the known-answer vectors of their RFCs (RFC 3713
/// Appendix A, RFC 7801 section 5.5/5.6, RFC 2144 Appendix B), in both directions, plus random
/// round trips for every key size.
/// </summary>
[TestFixture]
public sealed class CamelliaKuznyechikCast5Tests {
  [TestCase("0123456789abcdeffedcba9876543210", "0123456789abcdeffedcba9876543210", "67673138549669730857065648eabe43")]
  [TestCase("0123456789abcdeffedcba98765432100011223344556677", "0123456789abcdeffedcba9876543210", "b4993401b3e996f84ee5cee7d79b09b9")]
  [TestCase("0123456789abcdeffedcba987654321000112233445566778899aabbccddeeff", "0123456789abcdeffedcba9876543210", "9acc237dff16d76c20ef7c919e3a7509")]
  public void GivenAnRfc3713Vector_WhenCamelliaEncryptsAndDecrypts_ThenBothDirectionsMatch(string key, string plain, string cipher) {
    var camellia = new Camellia(Convert.FromHexString(key));
    var block = Convert.FromHexString(plain);
    camellia.Encrypt(block);
    Assert.That(Convert.ToHexStringLower(block), Is.EqualTo(cipher));
    camellia.Decrypt(block);
    Assert.That(Convert.ToHexStringLower(block), Is.EqualTo(plain));
  }

  [Test]
  public void GivenTheRfc7801Vector_WhenKuznyechikEncryptsAndDecrypts_ThenBothDirectionsMatch() {
    var kuznyechik = new Kuznyechik(Convert.FromHexString("8899aabbccddeeff0011223344556677fedcba98765432100123456789abcdef"));
    var block = Convert.FromHexString("1122334455667700ffeeddccbbaa9988");
    kuznyechik.Encrypt(block);
    Assert.That(Convert.ToHexStringLower(block), Is.EqualTo("7f679d90bebc24305a468d42b9d4edcd"));
    kuznyechik.Decrypt(block);
    Assert.That(Convert.ToHexStringLower(block), Is.EqualTo("1122334455667700ffeeddccbbaa9988"));
  }

  [TestCase("0123456712345678234567893456789a", "0123456789abcdef", "238b4fe5847e44b2")]
  [TestCase("01234567123456782345", "0123456789abcdef", "eb6a711a2c02271b")]
  [TestCase("0123456712", "0123456789abcdef", "7ac816d16e9b302e")]
  public void GivenAnRfc2144Vector_WhenCast5EncryptsAndDecrypts_ThenBothDirectionsMatch(string key, string plain, string cipher) {
    var cast = new Cast5(Convert.FromHexString(key));
    var block = Convert.FromHexString(plain);
    cast.Encrypt(block);
    Assert.That(Convert.ToHexStringLower(block), Is.EqualTo(cipher));
    cast.Decrypt(block);
    Assert.That(Convert.ToHexStringLower(block), Is.EqualTo(plain));
  }

  /// <summary>RFC 2144 Appendix B.2: a million rounds of keys and blocks feeding each other.</summary>
  [Test]
  public void GivenTheRfc2144MaintenanceTest_WhenRun_ThenBothVectorsMatch() {
    var a = Convert.FromHexString("0123456712345678234567893456789a");
    var b = Convert.FromHexString("0123456712345678234567893456789a");
    for (var i = 0; i < 1_000_000; ++i) {
      var byB = new Cast5(b);
      byB.Encrypt(a.AsSpan(0, 8));
      byB.Encrypt(a.AsSpan(8, 8));
      var byA = new Cast5(a);
      byA.Encrypt(b.AsSpan(0, 8));
      byA.Encrypt(b.AsSpan(8, 8));
    }
    Assert.That(Convert.ToHexStringLower(a), Is.EqualTo("eea9d0a249fd3ba6b3436fb89d6dca92"));
    Assert.That(Convert.ToHexStringLower(b), Is.EqualTo("b2c95eb00c31ad7180ac05b8e83d696e"));
  }

  [TestCase(16)]
  [TestCase(24)]
  [TestCase(32)]
  public void GivenRandomKeysAndBlocks_WhenCamelliaEncryptsAndDecrypts_ThenTheBlockIsRestored(int keyLength)
    => BlockCipherRoundTrip.Assert(new Random(keyLength), keyLength, Camellia.BlockSize, key => {
      var cipher = new Camellia(key);
      return (cipher.Encrypt, cipher.Decrypt);
    });

  [Test]
  public void GivenRandomKeysAndBlocks_WhenKuznyechikEncryptsAndDecrypts_ThenTheBlockIsRestored()
    => BlockCipherRoundTrip.Assert(new Random(7801), Kuznyechik.KeySize, Kuznyechik.BlockSize, key => {
      var cipher = new Kuznyechik(key);
      return (cipher.Encrypt, cipher.Decrypt);
    });

  [Test]
  public void GivenRandomKeysAndBlocks_WhenCast5EncryptsAndDecrypts_ThenTheBlockIsRestored() {
    var random = new Random(2144);
    for (var keyLength = Cast5.MinKeySize; keyLength <= Cast5.MaxKeySize; ++keyLength)
      BlockCipherRoundTrip.Assert(random, keyLength, Cast5.BlockSize, key => {
        var cipher = new Cast5(key);
        return (cipher.Encrypt, cipher.Decrypt);
      });
  }

  [TestCase(0)]
  [TestCase(15)]
  [TestCase(20)]
  [TestCase(33)]
  public void GivenACamelliaKeyOfAnotherLength_WhenConstructed_ThenItIsRejected(int length)
    => Assert.Throws<ArgumentException>(() => _ = new Camellia(new byte[length]));

  [TestCase(16)]
  [TestCase(31)]
  [TestCase(33)]
  public void GivenAKuznyechikKeyOfAnotherLength_WhenConstructed_ThenItIsRejected(int length)
    => Assert.Throws<ArgumentException>(() => _ = new Kuznyechik(new byte[length]));

  [TestCase(0)]
  [TestCase(4)]
  [TestCase(17)]
  public void GivenACast5KeyOutOfRange_WhenConstructed_ThenItIsRejected(int length)
    => Assert.Throws<ArgumentException>(() => _ = new Cast5(new byte[length]));

  [Test]
  public void GivenAWrongBlockLength_WhenProcessed_ThenItIsRejected() {
    Assert.Throws<ArgumentException>(() => new Camellia(new byte[16]).Encrypt(new byte[15]));
    Assert.Throws<ArgumentException>(() => new Camellia(new byte[16]).Decrypt(new byte[17]));
    Assert.Throws<ArgumentException>(() => new Kuznyechik(new byte[32]).Encrypt(new byte[8]));
    Assert.Throws<ArgumentException>(() => new Kuznyechik(new byte[32]).Decrypt(new byte[17]));
    Assert.Throws<ArgumentException>(() => new Cast5(new byte[16]).Encrypt(new byte[16]));
    Assert.Throws<ArgumentException>(() => new Cast5(new byte[16]).Decrypt(new byte[7]));
  }
}
