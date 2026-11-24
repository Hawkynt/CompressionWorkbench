using System.Buffers.Binary;
using System.Text;
using Compression.Core.Crypto;

namespace Compression.Tests.Crypto;

/// <summary>
/// ChaCha20 and Poly1305 against the RFC 8439 test vectors, composed into the RFC's AEAD and
/// compared with the platform's ChaCha20-Poly1305 as an independent oracle, plus argument checks.
/// </summary>
[TestFixture]
public sealed class ChaCha20Poly1305PrimitiveTests {
  private static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", "").Replace(":", ""));
  private static byte[] Sequence(int start, int count) => Enumerable.Range(start, count).Select(i => (byte)i).ToArray();

  [Test, Category("HappyPath")]
  public void GivenRfc8439Section232_WhenOneBlockIsComputed_ThenItMatches() {
    var block = new byte[64];
    ChaCha20.Block(Sequence(0, 32), 1, Hex("000000090000004a00000000"), block);
    Assert.That(Convert.ToHexStringLower(block), Is.EqualTo(
      "10f1e7e4d13b5915500fdd1fa32071c4c7d1f4c733c068030422aa9ac3d46c4e" +
      "d2826446079faa0914c2d705d98b02a2b5129cd1de164eb9cbd083e8a2503c4e"));
  }

  [Test, Category("HappyPath")]
  public void GivenRfc8439Section242_WhenTheSunscreenTextIsEncrypted_ThenTheCiphertextMatches() {
    var data = Encoding.ASCII.GetBytes(
      "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
    ChaCha20.Transform(Sequence(0, 32), 1, Hex("000000000000004a00000000"), data);
    Assert.That(Convert.ToHexStringLower(data), Is.EqualTo(
      "6e2e359a2568f98041ba0728dd0d6981e97e7aec1d4360c20a27afccfd9fae0b" +
      "f91b65c5524733ab8f593dabcd62b3571639d624e65152ab8f530c359f0861d8" +
      "07ca0dbf500d6a6156a38e088a22b65e52bc514d16ccf806818ce91ab7793736" +
      "5af90bbf74a35be6b40b8eedf2785e42874d"));
  }

  [Test, Category("HappyPath")]
  public void GivenTheCounterAndNonceAsOneBlock_WhenTransformed_ThenItEqualsTheSeparateForm() {
    var key = Sequence(7, 32);
    var nonce = Sequence(100, 12);
    var a = Sequence(0, 200);
    var b = (byte[])a.Clone();
    ChaCha20.Transform(key, 0xDEADBEEF, nonce, a);
    var iv = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(iv, 0xDEADBEEF);
    nonce.CopyTo(iv, 4);
    ChaCha20.Transform(key, iv, b);
    Assert.That(b, Is.EqualTo(a));
  }

  [Test, Category("EdgeCase")]
  public void GivenTheLastCounterValue_WhenTwoBlocksAreTransformed_ThenTheCounterWrapsToZero() {
    var key = Sequence(1, 32);
    var nonce = Sequence(9, 12);
    var data = new byte[128];
    ChaCha20.Transform(key, uint.MaxValue, nonce, data);
    var second = new byte[64];
    ChaCha20.Block(key, 0, nonce, second);
    Assert.That(data.AsSpan(64).ToArray(), Is.EqualTo(second));
  }

  [Test, Category("HappyPath")]
  public void GivenRfc8439Section252_WhenTheForumTextIsAuthenticated_ThenTheTagMatches() {
    var tag = Poly1305.ComputeTag(
      Hex("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b"),
      Encoding.ASCII.GetBytes("Cryptographic Forum Research Group"));
    Assert.That(Convert.ToHexStringLower(tag), Is.EqualTo("a8061dc1305136c6c22b8baf0c0127a9"));
  }

  [Test, Category("HappyPath")]
  public void GivenRfc8439Section262_WhenTheOneTimeKeyIsGenerated_ThenItMatches() {
    var block = new byte[64];
    ChaCha20.Block(Sequence(0x80, 32), 0, Hex("000000000001020304050607"), block);
    Assert.That(Convert.ToHexStringLower(block.AsSpan(0, 32)), Is.EqualTo(
      "8ad5a08b905f81cc815040274ab29471a833b637e3fd0da508dbb8e2fdd1a646"));
  }

  /// <summary>RFC 8439 A.3: a zero key gives a zero tag; and the two vectors that exercise the final reduction.</summary>
  [TestCase("0000000000000000000000000000000000000000000000000000000000000000",
    "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
    "00000000000000000000000000000000")]
  [TestCase("0200000000000000000000000000000000000000000000000000000000000000",
    "ffffffffffffffffffffffffffffffff", "03000000000000000000000000000000")]
  [TestCase("02000000000000000000000000000000ffffffffffffffffffffffffffffffff",
    "02000000000000000000000000000000", "03000000000000000000000000000000")]
  [Category("Boundary")]
  public void GivenRfc8439AppendixA3_WhenAuthenticated_ThenTheTagMatches(string key, string message, string expected)
    => Assert.That(Convert.ToHexStringLower(Poly1305.ComputeTag(Hex(key), Hex(message))), Is.EqualTo(expected));

  [Test, Category("HappyPath")]
  public void GivenAMessageFedInPieces_WhenFinished_ThenTheTagEqualsTheOneShotTag() {
    var key = Sequence(3, 32);
    var message = Sequence(0, 251);
    var expected = Poly1305.ComputeTag(key, message);
    foreach (var step in new[] { 1, 3, 15, 16, 17, 100 }) {
      var mac = new Poly1305(key);
      for (var at = 0; at < message.Length; at += step)
        mac.Update(message.AsSpan(at, Math.Min(step, message.Length - at)));
      var tag = new byte[16];
      mac.Final(tag);
      Assert.That(tag, Is.EqualTo(expected), $"step {step}");
    }
  }

  /// <summary>
  /// The RFC 8439 AEAD built from these two primitives must give the platform's ciphertext and tag,
  /// for messages of every length class: empty, short, whole blocks and odd tails.
  /// </summary>
  [TestCase(0, 0)]
  [TestCase(1, 0)]
  [TestCase(15, 12)]
  [TestCase(16, 16)]
  [TestCase(64, 1)]
  [TestCase(65, 33)]
  [TestCase(1000, 7)]
  [TestCase(4096, 0)]
  [Category("ThirdPartyProof")]
  public void GivenRandomInputs_WhenComposedIntoTheAead_ThenThePlatformAgrees(int length, int aadLength) {
    if (!System.Security.Cryptography.ChaCha20Poly1305.IsSupported)
      Assert.Ignore("The platform has no ChaCha20-Poly1305 to compare with.");
    var random = new Random(length * 31 + aadLength);
    var key = new byte[32]; random.NextBytes(key);
    var nonce = new byte[12]; random.NextBytes(nonce);
    var plain = new byte[length]; random.NextBytes(plain);
    var aad = new byte[aadLength]; random.NextBytes(aad);

    var expectedCipher = new byte[length];
    var expectedTag = new byte[16];
    using (var platform = new System.Security.Cryptography.ChaCha20Poly1305(key))
      platform.Encrypt(nonce, plain, expectedCipher, expectedTag, aad);

    var cipher = (byte[])plain.Clone();
    ChaCha20.Transform(key, 1, nonce, cipher);
    var oneTime = new byte[64];
    ChaCha20.Block(key, 0, nonce, oneTime);
    var mac = new Poly1305(oneTime.AsSpan(0, 32));
    mac.Update(aad);
    mac.Update(new byte[(16 - aad.Length % 16) % 16]);
    mac.Update(cipher);
    mac.Update(new byte[(16 - cipher.Length % 16) % 16]);
    var lengths = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(lengths, (ulong)aad.Length);
    BinaryPrimitives.WriteUInt64LittleEndian(lengths.AsSpan(8), (ulong)cipher.Length);
    mac.Update(lengths);
    var tag = new byte[16];
    mac.Final(tag);

    Assert.Multiple(() => {
      Assert.That(cipher, Is.EqualTo(expectedCipher), "ciphertext");
      Assert.That(tag, Is.EqualTo(expectedTag), "tag");
    });
  }

  [TestCase(31)]
  [TestCase(33)]
  [Category("Exception")]
  public void GivenAKeyOfTheWrongLength_WhenUsed_ThenItIsRefused(int length) {
    Assert.Throws<ArgumentException>(() => ChaCha20.Transform(new byte[length], 0, new byte[12], new byte[1]));
    Assert.Throws<ArgumentException>(() => _ = new Poly1305(new byte[length]));
  }

  [TestCase(8)]
  [TestCase(16)]
  [Category("Exception")]
  public void GivenANonceOfTheWrongLength_WhenUsed_ThenItIsRefused(int length)
    => Assert.Throws<ArgumentException>(() => ChaCha20.Transform(new byte[32], 0, new byte[length], new byte[1]));
}
