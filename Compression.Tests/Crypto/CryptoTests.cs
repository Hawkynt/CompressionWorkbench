using Compression.Core.Crypto;

namespace Compression.Tests.Crypto;

[TestFixture]
public class CryptoTests {
  // AES-256-CBC Tests

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void AesCbc_RoundTrip() {
    var key = new byte[32];
    var iv = new byte[16];
    var rng = new Random(42);
    rng.NextBytes(key);
    rng.NextBytes(iv);

    var data = "Hello, AES-256-CBC!"u8.ToArray();
    var encrypted = AesCryptor.EncryptCbc(data, key, iv);
    var decrypted = AesCryptor.DecryptCbc(encrypted, key, iv);
    Assert.That(decrypted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void AesCbc_NoPadding_RoundTrip() {
    var key = new byte[32];
    var iv = new byte[16];
    var rng = new Random(43);
    rng.NextBytes(key);
    rng.NextBytes(iv);

    var data = new byte[32]; // Must be multiple of 16
    rng.NextBytes(data);
    var encrypted = AesCryptor.EncryptCbcNoPadding(data, key, iv);
    var decrypted = AesCryptor.DecryptCbcNoPadding(encrypted, key, iv);
    Assert.That(decrypted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void AesCbc_EncryptedDiffersFromPlaintext() {
    var key = new byte[32];
    var iv = new byte[16];
    var rng = new Random(44);
    rng.NextBytes(key);
    rng.NextBytes(iv);

    var data = "This should be encrypted."u8.ToArray();
    var encrypted = AesCryptor.EncryptCbc(data, key, iv);
    Assert.That(encrypted, Is.Not.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void AesCbc_WrongKey_Fails() {
    var key1 = new byte[32];
    var key2 = new byte[32];
    var iv = new byte[16];
    var rng = new Random(45);
    rng.NextBytes(key1);
    rng.NextBytes(key2);
    rng.NextBytes(iv);

    var data = "Secret data."u8.ToArray();
    var encrypted = AesCryptor.EncryptCbc(data, key1, iv);
    Assert.Throws<System.Security.Cryptography.CryptographicException>(
      () => AesCryptor.DecryptCbc(encrypted, key2, iv));
  }

  [Category("Exception")]
  [Test]
  public void AesCbc_InvalidKeyLength_Throws() {
    Assert.Throws<ArgumentException>(() =>
      AesCryptor.EncryptCbc([1, 2, 3], new byte[16], new byte[16]));
  }

  // AES-256-CTR Tests

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void AesCtr_RoundTrip() {
    var key = new byte[32];
    var nonce = new byte[16];
    var rng = new Random(46);
    rng.NextBytes(key);
    rng.NextBytes(nonce);

    var data = "Hello, AES-256-CTR mode!"u8.ToArray();
    var encrypted = AesCryptor.TransformCtr(data, key, nonce);
    var decrypted = AesCryptor.TransformCtr(encrypted, key, nonce);
    Assert.That(decrypted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void AesCtr_IsSymmetric() {
    var key = new byte[32];
    var nonce = new byte[16];
    var rng = new Random(47);
    rng.NextBytes(key);
    rng.NextBytes(nonce);

    var data = new byte[100];
    rng.NextBytes(data);

    // Encrypt twice should give back original
    var pass1 = AesCryptor.TransformCtr(data, key, nonce);
    var pass2 = AesCryptor.TransformCtr(pass1, key, nonce);
    Assert.That(pass2, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Test]
  public void AesCtr_EmptyData() {
    var key = new byte[32];
    var nonce = new byte[16];
    Assert.That(AesCryptor.TransformCtr([], key, nonce), Is.Empty);
  }

  // PBKDF2 Tests

  [Category("HappyPath")]
  [Test]
  public void Pbkdf2Sha256_DeterministicOutput() {
    byte[] salt = [1, 2, 3, 4, 5, 6, 7, 8];
    var key1 = KeyDerivation.Pbkdf2Sha256("password", salt, 1000, 32);
    var key2 = KeyDerivation.Pbkdf2Sha256("password", salt, 1000, 32);
    Assert.That(key1, Is.EqualTo(key2));
  }

  [Category("HappyPath")]
  [Test]
  public void Pbkdf2Sha256_DifferentPasswords_DifferentKeys() {
    byte[] salt = [1, 2, 3, 4];
    var key1 = KeyDerivation.Pbkdf2Sha256("password1", salt, 1000, 32);
    var key2 = KeyDerivation.Pbkdf2Sha256("password2", salt, 1000, 32);
    Assert.That(key1, Is.Not.EqualTo(key2));
  }

  [Category("HappyPath")]
  [Test]
  public void Pbkdf2Sha256_DifferentSalts_DifferentKeys() {
    byte[] salt1 = [1, 2, 3, 4];
    byte[] salt2 = [5, 6, 7, 8];
    var key1 = KeyDerivation.Pbkdf2Sha256("password", salt1, 1000, 32);
    var key2 = KeyDerivation.Pbkdf2Sha256("password", salt2, 1000, 32);
    Assert.That(key1, Is.Not.EqualTo(key2));
  }

  [Category("HappyPath")]
  [Test]
  public void Pbkdf2Sha1_DeterministicOutput() {
    byte[] salt = [10, 20, 30, 40];
    var key1 = KeyDerivation.Pbkdf2Sha1("test", salt, 500, 32);
    var key2 = KeyDerivation.Pbkdf2Sha1("test", salt, 500, 32);
    Assert.That(key1, Is.EqualTo(key2));
  }

  // 7z Key Derivation Tests

  [Category("HappyPath")]
  [Test]
  public void SevenZipDeriveKey_ProducesKey() {
    byte[] salt = [0xAA, 0xBB, 0xCC, 0xDD];
    var key = KeyDerivation.SevenZipDeriveKey("password", salt, 6); // 2^6 = 64 iterations
    Assert.That(key.Length, Is.EqualTo(32));
    Assert.That(key, Is.Not.EqualTo(new byte[32])); // Not all zeros
  }

  [Category("HappyPath")]
  [Test]
  public void SevenZipDeriveKey_Deterministic() {
    byte[] salt = [1, 2, 3, 4];
    var key1 = KeyDerivation.SevenZipDeriveKey("test", salt, 6);
    var key2 = KeyDerivation.SevenZipDeriveKey("test", salt, 6);
    Assert.That(key1, Is.EqualTo(key2));
  }

  [Category("HappyPath")]
  [Test]
  public void SevenZipDeriveKey_DifferentPassword_DifferentKey() {
    byte[] salt = [1, 2];
    var key1 = KeyDerivation.SevenZipDeriveKey("pass1", salt, 6);
    var key2 = KeyDerivation.SevenZipDeriveKey("pass2", salt, 6);
    Assert.That(key1, Is.Not.EqualTo(key2));
  }

  // RAR5 Key Derivation Test
  [Category("HappyPath")]
  [Test]
  public void Rar5DeriveKey_ProducesKey() {
    var salt = new byte[16];
    new Random(48).NextBytes(salt);
    var key = KeyDerivation.Rar5DeriveKey("password", salt, 32768);
    Assert.That(key.Length, Is.EqualTo(32));
  }

  // Integration: AES + Key Derivation

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void AesCbc_WithDerivedKey_RoundTrip() {
    byte[] salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    var key = KeyDerivation.Pbkdf2Sha256("my-password", salt, 1000, 32);
    var iv = new byte[16];
    new Random(99).NextBytes(iv);

    var data = "Encrypted with a password-derived key."u8.ToArray();
    var encrypted = AesCryptor.EncryptCbc(data, key, iv);
    var decrypted = AesCryptor.DecryptCbc(encrypted, key, iv);
    Assert.That(decrypted, Is.EqualTo(data));
  }
}
