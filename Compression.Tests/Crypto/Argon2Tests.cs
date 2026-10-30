using Compression.Core.Crypto;

namespace Compression.Tests.Crypto;

/// <summary>RFC 9106 §5 test vectors: P = 32×0x01, S = 16×0x02, K = 8×0x03, X = 12×0x04, t = 3, m = 32 KiB, p = 4, T = 32.</summary>
[TestFixture]
public class Argon2Tests {
  private static byte[] Fill(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

  [TestCase(Argon2.Variant.Argon2d, "512b391b6f1162975371d30919734294f868e3be3984f3c1a13a4db9fabe4acb")]
  [TestCase(Argon2.Variant.Argon2i, "c814d9d1dc7f37aa13f0d77f2494bda1c8de6b016dd388d29952a4c4672b6ce8")]
  [TestCase(Argon2.Variant.Argon2id, "0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659")]
  public void GivenTheRfc9106Vector_WhenHashed_ThenTheTagMatches(Argon2.Variant variant, string expected) {
    var tag = Argon2.Hash(variant, Fill(32, 1), Fill(16, 2), iterations: 3, memoryKiB: 32, lanes: 4, tagLength: 32,
      secret: Fill(8, 3), associated: Fill(12, 4));
    Assert.That(Convert.ToHexStringLower(tag), Is.EqualTo(expected));
  }

  [Test]
  public void GivenALongTag_WhenHashed_ThenItIsDeterministicAndFullLength() {
    var a = Argon2.Hash(Argon2.Variant.Argon2id, "pw"u8, "saltsalt"u8, 2, 64, 1, 100);
    var b = Argon2.Hash(Argon2.Variant.Argon2id, "pw"u8, "saltsalt"u8, 2, 64, 1, 100);
    Assert.That(a, Has.Length.EqualTo(100));
    Assert.That(a, Is.EqualTo(b));
  }

  [Test]
  public void GivenTooLittleMemory_WhenHashed_ThenRefused()
    => Assert.Throws<ArgumentOutOfRangeException>(() => Argon2.Hash(Argon2.Variant.Argon2id, "pw"u8, "saltsalt"u8, 1, 7, 1, 32));
}
