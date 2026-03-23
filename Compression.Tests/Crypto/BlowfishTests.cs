using Compression.Core.Crypto;

namespace Compression.Tests.Crypto;

[TestFixture]
public class BlowfishTests {
  // Eric Young's test vectors
  [Category("ThemVsUs")]
  [Test]
  public void Encrypt_ZeroKeyZeroPlain() {
    var bf = new Blowfish(new byte[8]);
    byte[] block = [0, 0, 0, 0, 0, 0, 0, 0];
    bf.Encrypt(block);
    Assert.That(block, Is.EqualTo(new byte[] { 0x4E, 0xF9, 0x97, 0x45, 0x61, 0x98, 0xDD, 0x78 }));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Decrypt_RoundTrip() {
    var bf = new Blowfish(new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF });
    byte[] original = [0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10];
    var block = (byte[])original.Clone();
    bf.Encrypt(block);
    Assert.That(block, Is.Not.EqualTo(original));
    bf.Decrypt(block);
    Assert.That(block, Is.EqualTo(original));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Encrypt_KnownVector() {
    // Key: 0123456789ABCDEF, Plain: 1111111111111111 -> Cipher: 61F9C3802281B096
    var bf = new Blowfish(new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF });
    byte[] block = [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11];
    bf.Encrypt(block);
    Assert.That(block, Is.EqualTo(new byte[] { 0x61, 0xF9, 0xC3, 0x80, 0x22, 0x81, 0xB0, 0x96 }));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Cbc_RoundTrip() {
    var bf = new Blowfish("TestKey!"u8);
    byte[] iv = [1, 2, 3, 4, 5, 6, 7, 8];
    var data = "Hello, Blowfish CBC mode!"u8.ToArray();
    var encrypted = bf.EncryptCbc(data, iv);
    var decrypted = bf.DecryptCbc(encrypted, iv);
    Assert.That(decrypted, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void Cbc_BlockAligned_RoundTrip() {
    var bf = new Blowfish("SecretKy"u8);
    var iv = new byte[8];
    var data = new byte[64];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 7);
    var encrypted = bf.EncryptCbc(data, iv);
    var decrypted = bf.DecryptCbc(encrypted, iv);
    Assert.That(decrypted, Is.EqualTo(data));
  }
}
