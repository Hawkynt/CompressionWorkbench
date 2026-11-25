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

  [TestCase("scrambling", "ecc-public.asc")]
  [TestCase("aes256", "ecc-public.asc;missing.asc")]
  [Category("Exception")]
  public void GivenBadGnuPgCreateOptions_WhenCreated_ThenRefused(string method, string recipients)
    => Assert.That(() => new DarFormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a.txt", [1])], new FormatCreateOptions {
      EncryptionMethod = method,
      FormatSpecific = { [DarFormatDescriptor.GnuPgRecipientsOption] = string.Join(';', recipients.Split(';').Select(r => Path.Combine(this._tmp, r))) },
    }), Throws.InstanceOf<ArgumentException>().Or.InstanceOf<FileNotFoundException>());
}
