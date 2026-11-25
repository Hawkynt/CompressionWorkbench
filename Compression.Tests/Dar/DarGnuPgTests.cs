#pragma warning disable CS1591
using System.Text;
using Compression.Core.Crypto.OpenPgp;
using Compression.Registry;
using Compression.Tests.Crypto;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// Archives encrypted for GnuPG recipients without dar: the reference vectors dar 2.7.13 wrote to
/// the test keys of <c>Crypto/OpenPgpVectors</c>, and our own archives round-tripped and edited.
/// dar judges ours in <see cref="DarGnuPgOracleTests"/>.
/// </summary>
[TestFixture]
public class DarGnuPgTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = DarFixtures.TempDirectory("gnupgunit");
    foreach (var name in new[] { "ecc-secret.asc", "rsa-secret.asc", "ecc-public.asc" })
      File.WriteAllBytes(Path.Combine(this._tmp, name), OpenPgpTests.Vector(name));
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private string Key(string file, string passphrase = "keypass") => Path.Combine(this._tmp, file) + "|" + passphrase;

  [TestCase("gnupg-ecc-aes256.1.dar", "ecc-secret.asc", 'a')]
  [TestCase("gnupg-rsa-camellia256.1.dar", "rsa-secret.asc", 'c')]
  [Category("HappyPath")]
  public void GivenDarArchiveForAGnuPgRecipient_WhenReadWithTheSecretKey_ThenTheFileComesBack(string vector, string key, char cipher) {
    using var set = DarSliceSet.Open(new MemoryStream(DarFixtures.Vector(vector)));
    var archive = DarArchive.Read(set.Archive, this.Key(key));
    Assert.That((archive.Trailer.HasCryptedKey, archive.Trailer.Cipher, archive.ArchiveKey!.Length), Is.EqualTo((true, cipher, 32)));
    Assert.That(archive.ReadData(archive.Entries.Single(e => e.Path == "hello.txt")), Is.EqualTo("hello dar\n"u8.ToArray()));
  }

  [Test, Category("Exception")]
  public void GivenDarArchiveForAGnuPgRecipient_WhenNoKeyIsGiven_ThenAKeyIsAskedFor() {
    using var set = DarSliceSet.Open(new MemoryStream(DarFixtures.Vector("gnupg-ecc-aes256.1.dar")));
    Assert.That(() => DarArchive.Read(set.Archive, null),
      Throws.InstanceOf<ArchivePasswordException>().With.Property(nameof(ArchivePasswordException.PasswordWasSupplied)).False);
  }

  [TestCase("ecc-secret.asc", "wrong", TestName = "GivenDarArchiveForAGnuPgRecipient_WhenTheKeysPassphraseIsWrong_ThenRefused")]
  [TestCase("rsa-secret.asc", "keypass", TestName = "GivenDarArchiveForAGnuPgRecipient_WhenAnotherKeyIsGiven_ThenRefused")]
  [TestCase("ecc-public.asc", "keypass", TestName = "GivenDarArchiveForAGnuPgRecipient_WhenOnlyThePublicKeyIsGiven_ThenRefused")]
  [TestCase("missing.asc", "keypass", TestName = "GivenDarArchiveForAGnuPgRecipient_WhenTheKeyFileIsMissing_ThenRefused")]
  [Category("Exception")]
  public void Refused(string key, string passphrase) {
    using var set = DarSliceSet.Open(new MemoryStream(DarFixtures.Vector("gnupg-ecc-aes256.1.dar")));
    Assert.That(() => DarArchive.Read(set.Archive, this.Key(key, passphrase)),
      Throws.InstanceOf<ArchivePasswordException>().With.Property(nameof(ArchivePasswordException.PasswordWasSupplied)).True);
  }

  [TestCase("aes256", 32)]
  [TestCase("blowfish", 56)]
  [Category("HappyPath")]
  public void GivenOurArchiveForARecipient_WhenWrittenEditedAndRead_ThenEverythingRoundTripsUnderTheSameKey(string method, int keyLength) {
    using var ms = new MemoryStream();
    new DarFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("a.txt", Encoding.UTF8.GetBytes(new string('a', 30000)))], new FormatCreateOptions {
      MethodName = "stored", EncryptionMethod = method,
      FormatSpecific = { [DarFormatDescriptor.GnuPgRecipientsOption] = Path.Combine(this._tmp, "ecc-public.asc") },
    });
    byte[] crypted;
    using (var set = DarSliceSet.Open(new MemoryStream(ms.ToArray()))) {
      var archive = DarArchive.Read(set.Archive, this.Key("ecc-secret.asc"));
      Assert.That(archive.ArchiveKey, Has.Length.EqualTo(keyLength));
      crypted = archive.Trailer.CryptedKey!;
      Assert.That(OpenPgpMessage.Decrypt(crypted, OpenPgpKey.Load(OpenPgpTests.Vector("ecc-secret.asc")), "keypass"), Is.EqualTo(archive.ArchiveKey));
    }
    new DarFormatDescriptor().Add(ms, [ArchiveInputInfo.InMemory("b.txt", "b\n"u8.ToArray())], new ArchiveMutationOptions { Password = this.Key("ecc-secret.asc") });
    using (var set = DarSliceSet.Open(new MemoryStream(ms.ToArray()))) {
      var archive = DarArchive.Read(set.Archive, this.Key("ecc-secret.asc"));
      Assert.That(archive.Trailer.CryptedKey, Is.EqualTo(crypted), "the edit keeps the recipients' message");
      Assert.That(archive.ReadData(archive.Entries.Single(e => e.Path == "a.txt")), Has.Length.EqualTo(30000));
      Assert.That(archive.ReadData(archive.Entries.Single(e => e.Path == "b.txt")), Is.EqualTo("b\n"u8.ToArray()));
    }
  }

  [TestCase("gnupg-signed-eddsa.1.dar", "ecc-secret.asc", "A076F346C8844D63F4065625D6F494848082CEB6")]
  [TestCase("gnupg-signed-rsa.1.dar", "ecc-secret.asc;rsa-public.asc", "02AAE52A50B9261967A9DDD09CB234AB9157E743")]
  [Category("HappyPath")]
  public void GivenDarSignedArchive_WhenReadWithTheSignersKey_ThenTheSignatureVerifies(string vector, string keys, string signer) {
    File.WriteAllBytes(Path.Combine(this._tmp, "rsa-public.asc"), OpenPgpTests.Vector("rsa-public.asc"));
    using var set = DarSliceSet.Open(new MemoryStream(DarFixtures.Vector(vector)));
    var password = string.Join(';', keys.Split(';').Select(k => Path.Combine(this._tmp, k))) + "|keypass";
    var archive = DarArchive.Read(set.Archive, password);
    Assert.That((archive.Trailer.Flags & 0x0200) != 0, Is.True, "flag 0x0200: signed");
    Assert.That(archive.Signers.Select(Convert.ToHexString), Is.EqualTo(new[] { signer }));
    Assert.That(archive.ReadData(archive.Entries.Single(e => e.Path == "hello.txt")), Is.EqualTo("hello dar\n"u8.ToArray()));
  }

  [Test, Category("Exception")]
  public void GivenDarSignedArchive_WhenTheSignersKeyIsNotGiven_ThenRefused() {
    using var set = DarSliceSet.Open(new MemoryStream(DarFixtures.Vector("gnupg-signed-rsa.1.dar")));
    Assert.That(() => DarArchive.Read(set.Archive, this.Key("ecc-secret.asc")),
      Throws.InstanceOf<ArchivePasswordException>().With.Message.Contains("9CB234AB9157E743"));
  }

  [TestCase("ecc-secret.asc")]
  [TestCase("rsa-secret.asc")]
  [Category("HappyPath")]
  public void GivenOurSignedArchive_WhenWrittenEditedAndRead_ThenTheSignatureVerifiesAndTheEditKeepsIt(string signer) {
    using var ms = new MemoryStream();
    new DarFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("a.txt", "a\n"u8.ToArray())], new FormatCreateOptions {
      EncryptionMethod = "aes256",
      FormatSpecific = {
        [DarFormatDescriptor.GnuPgRecipientsOption] = Path.Combine(this._tmp, "ecc-public.asc"),
        [DarFormatDescriptor.GnuPgSignerOption] = this.Key(signer),
      },
    });
    var password = Path.Combine(this._tmp, "ecc-secret.asc") + ";" + Path.Combine(this._tmp, signer) + "|keypass";
    new DarFormatDescriptor().Add(ms, [ArchiveInputInfo.InMemory("b.txt", "b\n"u8.ToArray())], new ArchiveMutationOptions { Password = password });
    using var set = DarSliceSet.Open(new MemoryStream(ms.ToArray()));
    var archive = DarArchive.Read(set.Archive, password);
    Assert.That((archive.Trailer.Flags & 0x0200) != 0, Is.True);
    Assert.That(archive.Signers.Single(), Is.EqualTo(OpenPgpKey.Load(OpenPgpTests.Vector(signer))[0].Fingerprint));
    Assert.That(archive.Entries.Select(e => e.Path), Is.EquivalentTo(new[] { "a.txt", "b.txt" }));
  }

  private void WriteOurSigned(string path, string signer) {
    using var fs = File.Create(path);
    new DarFormatDescriptor().Create(fs, [ArchiveInputInfo.InMemory("hello.txt", "hello dar\n"u8.ToArray())], new FormatCreateOptions {
      EncryptionMethod = "aes256",
      FormatSpecific = {
        [DarFormatDescriptor.GnuPgRecipientsOption] = Path.Combine(this._tmp, "ecc-public.asc"),
        [DarFormatDescriptor.GnuPgSignerOption] = this.Key(signer),
      },
    });
  }

  [Test, Category("Exception")]
  public void GivenSignedArchive_WhenEditedWithoutTheSignersSecretKey_ThenRefusedAndUntouched() {
    File.WriteAllBytes(Path.Combine(this._tmp, "rsa-public.asc"), OpenPgpTests.Vector("rsa-public.asc"));
    var path = Path.Combine(this._tmp, "signed.1.dar");
    this.WriteOurSigned(path, "rsa-secret.asc");
    var before = File.ReadAllBytes(path);
    using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      Assert.That(() => new DarFormatDescriptor().Add(fs, [ArchiveInputInfo.InMemory("b.txt", [1])],
          new ArchiveMutationOptions { Password = Path.Combine(this._tmp, "ecc-secret.asc") + ";" + Path.Combine(this._tmp, "rsa-public.asc") + "|keypass" }),
        Throws.InstanceOf<NotSupportedException>().With.Message.Contains("02AAE52A50B9261967A9DDD09CB234AB9157E743"));
    Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
  }

  [Test, Category("HappyPath")]
  public void GivenSignedArchive_WhenEditedWithTheSignersSecretKey_ThenTheNewCatalogueIsSignedAgain() {
    var path = Path.Combine(this._tmp, "signed.1.dar");
    this.WriteOurSigned(path, "rsa-secret.asc");
    var password = Path.Combine(this._tmp, "ecc-secret.asc") + ";" + Path.Combine(this._tmp, "rsa-secret.asc") + "|keypass";
    using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      new DarFormatDescriptor().Add(fs, [ArchiveInputInfo.InMemory("b.txt", "b\n"u8.ToArray())], new ArchiveMutationOptions { Password = password });
    using var set = DarSliceSet.Open(new MemoryStream(File.ReadAllBytes(path)));
    var archive = DarArchive.Read(set.Archive, password);
    Assert.That(archive.GnuPg, Is.Not.Null, "the signed catalogue hash verified");
    Assert.That(archive.Entries.Select(e => e.Path), Is.EquivalentTo(new[] { "hello.txt", "b.txt" }));
  }

  [Test, Category("Exception")]
  public void GivenSignerWithAWrongPassphrase_WhenCreated_ThenRefused()
    => Assert.That(() => new DarFormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a.txt", [1])], new FormatCreateOptions {
      FormatSpecific = {
        [DarFormatDescriptor.GnuPgRecipientsOption] = Path.Combine(this._tmp, "ecc-public.asc"),
        [DarFormatDescriptor.GnuPgSignerOption] = this.Key("ecc-secret.asc", "wrong"),
      },
    }), Throws.ArgumentException);

  [TestCase("scrambling", "ecc-public.asc")]
  [TestCase("aes256", "ecc-public.asc;missing.asc")]
  [Category("Exception")]
  public void GivenBadGnuPgCreateOptions_WhenCreated_ThenRefused(string method, string recipients)
    => Assert.That(() => new DarFormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a.txt", [1])], new FormatCreateOptions {
      EncryptionMethod = method,
      FormatSpecific = { [DarFormatDescriptor.GnuPgRecipientsOption] = string.Join(';', recipients.Split(';').Select(r => Path.Combine(this._tmp, r))) },
    }), Throws.InstanceOf<ArgumentException>().Or.InstanceOf<FileNotFoundException>());
}
