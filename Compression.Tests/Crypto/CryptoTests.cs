using Compression.Core.Crypto;

namespace Compression.Tests.Crypto;

[TestFixture]
public class CryptoTests {
  // AES-256-CBC Tests

  [Test]
  public void AesCbc_RoundTrip() {
    byte[] key = new byte[32];
    byte[] iv = new byte[16];
    var rng = new Random(42);
    rng.NextBytes(key);
    rng.NextBytes(iv);

    byte[] data = "Hello, AES-256-CBC!"u8.ToArray();
    byte[] encrypted = AesCryptor.EncryptCbc(data, key, iv);
    byte[] decrypted = AesCryptor.DecryptCbc(encrypted, key, iv);
    Assert.That(decrypted, Is.EqualTo(data));
  }

  [Test]
  public void AesCbc_NoPadding_RoundTrip() {
    byte[] key = new byte[32];
    byte[] iv = new byte[16];
    var rng = new Random(43);
    rng.NextBytes(key);
    rng.NextBytes(iv);

    byte[] data = new byte[32]; // Must be multiple of 16
    rng.NextBytes(data);
    byte[] encrypted = AesCryptor.EncryptCbcNoPadding(data, key, iv);
    byte[] decrypted = AesCryptor.DecryptCbcNoPadding(encrypted, key, iv);
    Assert.That(decrypted, Is.EqualTo(data));
  }

  [Test]
  public void AesCbc_EncryptedDiffersFromPlaintext() {
    byte[] key = new byte[32];
    byte[] iv = new byte[16];
    var rng = new Random(44);
    rng.NextBytes(key);
    rng.NextBytes(iv);

    byte[] data = "This should be encrypted."u8.ToArray();
    byte[] encrypted = AesCryptor.EncryptCbc(data, key, iv);
    Assert.That(encrypted, Is.Not.EqualTo(data));
  }

  [Test]
  public void AesCbc_WrongKey_Fails() {
    byte[] key1 = new byte[32];
    byte[] key2 = new byte[32];
    byte[] iv = new byte[16];
    var rng = new Random(45);
    rng.NextBytes(key1);
    rng.NextBytes(key2);
    rng.NextBytes(iv);

    byte[] data = "Secret data."u8.ToArray();
    byte[] encrypted = AesCryptor.EncryptCbc(data, key1, iv);
    Assert.Throws<System.Security.Cryptography.CryptographicException>(
      () => AesCryptor.DecryptCbc(encrypted, key2, iv));
  }

  [Test]
  public void AesCbc_InvalidKeyLength_Throws() {
    Assert.Throws<ArgumentException>(() =>
      AesCryptor.EncryptCbc([1, 2, 3], new byte[16], new byte[16]));
  }

  // AES-256-CTR Tests

  [Test]
  public void AesCtr_RoundTrip() {
    byte[] key = new byte[32];
    byte[] nonce = new byte[16];
    var rng = new Random(46);
    rng.NextBytes(key);
    rng.NextBytes(nonce);

    byte[] data = "Hello, AES-256-CTR mode!"u8.ToArray();
    byte[] encrypted = AesCryptor.TransformCtr(data, key, nonce);
    byte[] decrypted = AesCryptor.TransformCtr(encrypted, key, nonce);
    Assert.That(decrypted, Is.EqualTo(data));
  }

  [Test]
  public void AesCtr_IsSymmetric() {
    byte[] key = new byte[32];
    byte[] nonce = new byte[16];
    var rng = new Random(47);
    rng.NextBytes(key);
    rng.NextBytes(nonce);

    byte[] data = new byte[100];
    rng.NextBytes(data);

    // Encrypt twice should give back original
    byte[] pass1 = AesCryptor.TransformCtr(data, key, nonce);
    byte[] pass2 = AesCryptor.TransformCtr(pass1, key, nonce);
    Assert.That(pass2, Is.EqualTo(data));
  }

  [Test]
  public void AesCtr_EmptyData() {
    byte[] key = new byte[32];
    byte[] nonce = new byte[16];
    Assert.That(AesCryptor.TransformCtr([], key, nonce), Is.Empty);
  }

  // PBKDF2 Tests

  [Test]
  public void Pbkdf2Sha256_DeterministicOutput() {
    byte[] salt = [1, 2, 3, 4, 5, 6, 7, 8];
    byte[] key1 = KeyDerivation.Pbkdf2Sha256("password", salt, 1000, 32);
    byte[] key2 = KeyDerivation.Pbkdf2Sha256("password", salt, 1000, 32);
    Assert.That(key1, Is.EqualTo(key2));
  }

  [Test]
  public void Pbkdf2Sha256_DifferentPasswords_DifferentKeys() {
    byte[] salt = [1, 2, 3, 4];
    byte[] key1 = KeyDerivation.Pbkdf2Sha256("password1", salt, 1000, 32);
    byte[] key2 = KeyDerivation.Pbkdf2Sha256("password2", salt, 1000, 32);
    Assert.That(key1, Is.Not.EqualTo(key2));
  }

  [Test]
  public void Pbkdf2Sha256_DifferentSalts_DifferentKeys() {
    byte[] salt1 = [1, 2, 3, 4];
    byte[] salt2 = [5, 6, 7, 8];
    byte[] key1 = KeyDerivation.Pbkdf2Sha256("password", salt1, 1000, 32);
    byte[] key2 = KeyDerivation.Pbkdf2Sha256("password", salt2, 1000, 32);
    Assert.That(key1, Is.Not.EqualTo(key2));
  }

  [Test]
  public void Pbkdf2Sha1_DeterministicOutput() {
    byte[] salt = [10, 20, 30, 40];
    byte[] key1 = KeyDerivation.Pbkdf2Sha1("test", salt, 500, 32);
    byte[] key2 = KeyDerivation.Pbkdf2Sha1("test", salt, 500, 32);
    Assert.That(key1, Is.EqualTo(key2));
  }

  // 7z Key Derivation Tests

  [Test]
  public void SevenZipDeriveKey_ProducesKey() {
    byte[] salt = [0xAA, 0xBB, 0xCC, 0xDD];
    byte[] key = KeyDerivation.SevenZipDeriveKey("password", salt, 6); // 2^6 = 64 iterations
    Assert.That(key.Length, Is.EqualTo(32));
    Assert.That(key, Is.Not.EqualTo(new byte[32])); // Not all zeros
  }

  [Test]
  public void SevenZipDeriveKey_Deterministic() {
    byte[] salt = [1, 2, 3, 4];
    byte[] key1 = KeyDerivation.SevenZipDeriveKey("test", salt, 6);
    byte[] key2 = KeyDerivation.SevenZipDeriveKey("test", salt, 6);
    Assert.That(key1, Is.EqualTo(key2));
  }

  [Test]
  public void SevenZipDeriveKey_DifferentPassword_DifferentKey() {
    byte[] salt = [1, 2];
    byte[] key1 = KeyDerivation.SevenZipDeriveKey("pass1", salt, 6);
    byte[] key2 = KeyDerivation.SevenZipDeriveKey("pass2", salt, 6);
    Assert.That(key1, Is.Not.EqualTo(key2));
  }

  // RAR5 Key Derivation Test
  [Test]
  public void Rar5DeriveKey_ProducesKey() {
    byte[] salt = new byte[16];
    new Random(48).NextBytes(salt);
    byte[] key = KeyDerivation.Rar5DeriveKey("password", salt, 32768);
    Assert.That(key.Length, Is.EqualTo(32));
  }

  // Integration: AES + Key Derivation

  [Test]
  public void AesCbc_WithDerivedKey_RoundTrip() {
    byte[] salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    byte[] key = KeyDerivation.Pbkdf2Sha256("my-password", salt, 1000, 32);
    byte[] iv = new byte[16];
    new Random(99).NextBytes(iv);

    byte[] data = "Encrypted with a password-derived key."u8.ToArray();
    byte[] encrypted = AesCryptor.EncryptCbc(data, key, iv);
    byte[] decrypted = AesCryptor.DecryptCbc(encrypted, key, iv);
    Assert.That(decrypted, Is.EqualTo(data));
  }
}
