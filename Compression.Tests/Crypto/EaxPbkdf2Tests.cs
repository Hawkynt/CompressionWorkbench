using System.Security.Cryptography;
using Compression.Core.Crypto;

namespace Compression.Tests.Crypto;

/// <summary>
/// EAX against the test vectors of Bellare, Rogaway and Wagner ("The EAX Mode of Operation",
/// FSE 2004, appendix), and the generic PBKDF2 against RFC 6070 and the platform's PBKDF2.
/// </summary>
[TestFixture]
public class EaxPbkdf2Tests {

  // key, nonce, header, message, ciphertext ‖ tag — from the EAX paper's AES-128 vectors
  [TestCase("233952DEE4D5ED5F9B9C6D6FF80FF478", "62EC67F9C3A4A407FCB2A8C49031A8B3", "6BFB914FD07EAE6B", "",
    "E037830E8389F27B025A2D6527E79D01")]
  [TestCase("91945D3F4DCBEE0BF45EF52255F095A4", "BECAF043B0A23D843194BA972C66DEBD", "FA3BFD4806EB53FA", "F7FB",
    "19DD5C4C9331049D0BDAB0277408F67967E5")]
  [TestCase("01F74AD64077F2E704C0F60ADA3DD523", "70C3DB4F0D26368400A10ED05D2BFF5E", "234A3463C1264AC6", "1A47CB4933",
    "D851D5BAE03A59F238A23E39199DC9266626C40F80")]
  [TestCase("D07CF6CBB7F313BDDE66B727AFD3C5E8", "8408DFFF3C1A2B1292DC199E46B7D617", "33CCE2EABFF5A79D", "481C9E39B1",
    "632A9D131AD4C168A4225D8E1FF755939974A7BEDE")]
  [TestCase("35B6D0580005BBC12B0587124557D2C2", "FDB6B06676EEDC5C61D74276E1F8E816", "AEB96EAEBE2970E9", "40D0C07DA5E4",
    "071DFE16C675CB0677E536F73AFE6A14B74EE49844DD")]
  [TestCase("BD8E6E11475E60B268784C38C62FEB22", "6EAC5C93072D8E8513F750935E46DA1B", "D4482D1CA78DCE0F", "4DE3B35C3FC039245BD1FB7D",
    "835BB4F15D743E350E728414ABB8644FD6CCB86947C5E10590210A4F")]
  [TestCase("7C77D6E813BED5AC98BAA417477A2E7D", "1A8C98DCD73D38393B2BF1569DEEFC19", "65D2017990D62528", "8B0A79306C9CE7ED99DAE4F87F8DD61636",
    "02083E3979DA014812F59F11D52630DA30137327D10649B0AA6E1C181DB617D7F2")]
  [TestCase("5FFF20CAFAB119CA2FC73549E20F5B0D", "DDE59B97D722156D4D9AFF2BC7559826", "54B9F04E6A09189A", "1BDA122BCE8A8DBAF1877D962B8592DD2D56",
    "2EC47B2C4954A489AFC7BA4897EDCDAE8CC33B60450599BD02C96382902AEF7F832A")]
  [TestCase("A4A4782BCFFD3EC5E7EF6D8C34A56123", "B781FCF2F75FA5A8DE97A9CA48E522EC", "899A175897561D7E", "6CF36720872B8513F6EAB1A8A44438D5EF11",
    "0DE18FD0FDD91E7AF19F1D8EE8733938B1E8E7F6D2231618102FDB7FE55FF1991700")]
  [Category("HappyPath")]
  public void GivenPaperVector_WhenEncrypted_ThenCiphertextAndTagMatch(string key, string nonce, string header, string message, string expected) {
    var data = Convert.FromHexString(message);
    var eax = Eax.WithAes(Convert.FromHexString(key), Convert.FromHexString(nonce), Convert.FromHexString(header));
    eax.Encrypt(data);
    Assert.That(Convert.ToHexString([.. data, .. eax.Tag()]), Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void GivenPaperVector_WhenDecrypted_ThenMessageAndTagMatch() {
    var data = Convert.FromHexString("0DE18FD0FDD91E7AF19F1D8EE8733938B1E8");
    var eax = Eax.WithAes(Convert.FromHexString("A4A4782BCFFD3EC5E7EF6D8C34A56123"), Convert.FromHexString("B781FCF2F75FA5A8DE97A9CA48E522EC"), Convert.FromHexString("899A175897561D7E"));
    eax.Decrypt(data);
    Assert.That(Convert.ToHexString(data), Is.EqualTo("6CF36720872B8513F6EAB1A8A44438D5EF11"));
    Assert.That(Convert.ToHexString(eax.Tag()), Is.EqualTo("E7F6D2231618102FDB7FE55FF1991700"));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(15)]
  [TestCase(16)]
  [TestCase(17)]
  [TestCase(1023)]
  [TestCase(5000)]
  [Category("EdgeCase")]
  public void GivenMessageInRandomPieces_WhenEncrypted_ThenResultEqualsOneCall(int length) {
    var rng = new Random(length);
    var key = new byte[32];
    var nonce = new byte[32];
    rng.NextBytes(key);
    rng.NextBytes(nonce);
    var message = new byte[length];
    rng.NextBytes(message);

    var whole = (byte[])message.Clone();
    var a = Eax.WithSerpent(key, nonce);
    a.Encrypt(whole);
    var wholeTag = a.Tag();

    var pieces = (byte[])message.Clone();
    var b = Eax.WithSerpent(key, nonce);
    for (var at = 0; at < length;) {
      var n = Math.Min(rng.Next(0, 40), length - at);
      b.Encrypt(pieces.AsSpan(at, n));
      at += n;
    }
    Assert.That(pieces, Is.EqualTo(whole));
    Assert.That(b.Tag(), Is.EqualTo(wholeTag));

    var c = Eax.WithSerpent(key, nonce);
    c.Decrypt(pieces);
    Assert.That(pieces, Is.EqualTo(message));
    Assert.That(c.Tag(), Is.EqualTo(wholeTag));
  }

  [Test, Category("EdgeCase")]
  public void GivenTwofishAndAesUnderTheSameKey_WhenEncrypted_ThenTheyDiffer() {
    var key = new byte[16];
    var a = new byte[32];
    var b = new byte[32];
    Eax.WithAes(key, key).Encrypt(a);
    Eax.WithTwofish(key, key).Encrypt(b);
    Assert.That(a, Is.Not.EqualTo(b));
  }

  // RFC 6070: PBKDF2-HMAC-SHA1
  [TestCase("password", "salt", 1, "0C60C80F961F0E71F3A9B524AF6012062FE037A6")]
  [TestCase("password", "salt", 2, "EA6C014DC72D6F8CCD1ED92ACE1D41F0D8DE8957")]
  [TestCase("password", "salt", 4096, "4B007901B765489ABEAD49D926F721D065A429C1")]
  [TestCase("passwordPASSWORDpassword", "saltSALTsaltSALTsaltSALTsaltSALTsalt", 4096, "3D2EEC4FE41C849B80C8D83662C0E44A8B291A964CF2F07038")]
  [Category("HappyPath")]
  public void GivenRfc6070Vector_WhenDerived_ThenItMatches(string password, string salt, int iterations, string expected) {
    var length = expected.Length / 2;
    var key = KeyDerivation.Pbkdf2(SHA1.HashData, 64, System.Text.Encoding.ASCII.GetBytes(password), System.Text.Encoding.ASCII.GetBytes(salt), iterations, length);
    Assert.That(Convert.ToHexString(key), Is.EqualTo(expected));
  }

  [TestCase(1, 1)]
  [TestCase(3, 32)]
  [TestCase(100, 33)]
  [TestCase(10, 66)]
  [Category("EdgeCase")]
  public void GivenPasswordLongerThanABlock_WhenDerivedWithSha256_ThenItMatchesThePlatform(int iterations, int length) {
    var password = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
    byte[] salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
    Assert.That(KeyDerivation.Pbkdf2(SHA256.HashData, 64, password, salt, iterations, length),
      Is.EqualTo(Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, length)));
  }

  [TestCase(1, 66)]
  [TestCase(50, 130)]
  [Category("HappyPath")]
  public void GivenSha3_512_WhenDerivedWithItsRateAsBlockSize_ThenItMatchesThePlatform(int iterations, int length) {
    if (!SHA3_512.IsSupported) Assert.Ignore("The platform has no SHA3-512.");
    var password = Enumerable.Range(0, 80).Select(i => (byte)(i * 3)).ToArray(); // longer than the 72-byte rate
    byte[] salt = [9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2];
    Assert.That(KeyDerivation.Pbkdf2(static d => Hawkynt.Algorithms.Hashing.Sha3.Compute512(d), 72, password, salt, iterations, length),
      Is.EqualTo(Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA3_512, length)));
  }

  [Test, Category("Exception")]
  public void GivenZeroIterations_WhenDerived_ThenRefused()
    => Assert.That(() => KeyDerivation.Pbkdf2(SHA1.HashData, 64, [1], [2], 0, 20), Throws.InstanceOf<ArgumentOutOfRangeException>());
}
