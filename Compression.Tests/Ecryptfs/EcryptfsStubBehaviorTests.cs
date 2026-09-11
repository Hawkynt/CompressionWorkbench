#pragma warning disable CS1591
using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.Ecryptfs;

namespace Compression.Tests.Ecryptfs;

[TestFixture]
public class EcryptfsStubBehaviorTests {

  private static byte[] CreateImage(byte[] content, string password = "correct horse", string? method = null) {
    var descriptor = new EcryptfsFormatDescriptor();
    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(
      image,
      [ArchiveInputInfo.InMemory("payload.bin", content)],
      new FormatCreateOptions { Password = password, EncryptionMethod = method });
    return image.ToArray();
  }

  [TestCase("aes128")]
  [TestCase("aes192")]
  [TestCase("aes256")]
  [Category("Interop")]
  public void CreateThenExtract_RoundTripsAesPassphraseProfiles(string method) {
    var content = Enumerable.Range(0, 9001).Select(i => (byte)(i * 73 + 11)).ToArray();
    var descriptor = new EcryptfsFormatDescriptor();
    using var image = new MemoryStream(CreateImage(content, method: method), writable: false);

    var entries = descriptor.List(image, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("content.bin"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(content.Length));
    Assert.That(entries[0].IsEncrypted, Is.True);

    image.Position = 0;
    using var reader = new EcryptfsReader(image);
    Assert.That(reader.ExtractContent("correct horse"), Is.EqualTo(content));
  }

  [Test, Category("RoundTrip")]
  public void CredentialAwareAdd_ReplacesPayload_AndPreservesCipherProfile() {
    const string password = "correct horse";
    var replacement = Enumerable.Range(0, 12001).Select(i => (byte)(i * 41 + 3)).ToArray();
    var descriptor = new EcryptfsFormatDescriptor();
    var modifier = (IArchiveModifiable)descriptor;
    // Expandable: MemoryStream(byte[]) is fixed-capacity, and the replacement payload here is
    // larger than the original, so the Add has to grow the image.
    using var image = new MemoryStream();
    var seed = CreateImage([1, 2, 3, 4, 5], password, "aes256");
    image.Write(seed, 0, seed.Length);

    image.Position = 0;
    string? signatureBefore;
    using (var before = new EcryptfsReader(image)) {
      signatureBefore = before.PassphraseSignature;
      Assert.That(before.CipherDescription, Is.EqualTo("AES-256-CBC"));
    }

    image.Position = 0;
    modifier.Add(
      image,
      [ArchiveInputInfo.InMemory("content.bin", replacement)],
      new ArchiveMutationOptions { Password = password });

    image.Position = 0;
    using var reader = new EcryptfsReader(image);
    Assert.That(reader.CipherDescription, Is.EqualTo("AES-256-CBC"));
    Assert.That(reader.PassphraseSignature, Is.EqualTo(signatureBefore));
    Assert.That(reader.ExtractContent(password), Is.EqualTo(replacement));
  }

  [Test, Category("Sad")]
  public void CredentialAwareAdd_WrongPassphrase_LeavesOriginalByteIdentical() {
    var descriptor = new EcryptfsFormatDescriptor();
    var modifier = (IArchiveModifiable)descriptor;
    using var image = new MemoryStream(CreateImage(Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray()));
    var before = image.ToArray();

    image.Position = 0;
    Assert.Throws<CryptographicException>(() => modifier.Add(
      image,
      [ArchiveInputInfo.InMemory("content.bin", [9, 8, 7, 6])],
      new ArchiveMutationOptions { Password = "wrong horse" }));

    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("Sad")]
  public void CredentialFreeAdd_IsRejectedInsteadOfGuessingPassword() {
    var modifier = (IArchiveModifiable)new EcryptfsFormatDescriptor();
    using var image = new MemoryStream(CreateImage([1, 2, 3]));
    Assert.Throws<InvalidOperationException>(() => modifier.Add(
      image,
      [ArchiveInputInfo.InMemory("content.bin", [4, 5, 6])]));
  }

  [Test, Category("HappyPath")]
  public void Remove_ContentBin_LeavesValidEmptyLowerFile() {
    var descriptor = new EcryptfsFormatDescriptor();
    var modifier = (IArchiveModifiable)descriptor;
    using var image = new MemoryStream(CreateImage(Enumerable.Repeat((byte)0x6D, 5000).ToArray()));

    modifier.Remove(image, ["content.bin"]);

    Assert.That(image.Length, Is.EqualTo(8192));
    image.Position = 0;
    Assert.That(descriptor.List(image, null), Is.Empty);
    using var reader = new EcryptfsReader(image);
    reader.ValidatePassword("correct horse");
  }

  [Test, Category("Sad")]
  public void Extract_WrongPassphrase_IsRejectedBeforePayloadDecryption() {
    using var image = new MemoryStream(CreateImage([1, 2, 3, 4]), writable: false);
    using var reader = new EcryptfsReader(image);
    Assert.Throws<CryptographicException>(() => reader.ExtractContent("wrong horse"));
  }

  [Test, Category("HappyPath")]
  public void WipeAndShrink_RemoveOnlyProvenDeadBytes() {
    var content = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
    var descriptor = new EcryptfsFormatDescriptor();
    var original = CreateImage(content);
    using var image = new MemoryStream();
    image.Write(original);
    image.Write(Enumerable.Repeat((byte)0xA5, 257).ToArray());
    image.Position = 0;

    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(image);
    Assert.That(wiped, Is.GreaterThanOrEqualTo(257));
    Assert.That(image.ToArray().AsSpan(original.Length, 257).ToArray(), Is.All.Zero);

    image.Position = 0;
    using (var reader = new EcryptfsReader(image))
      Assert.That(reader.ExtractContent("correct horse"), Is.EqualTo(content));

    image.Position = 0;
    using var shrunk = new MemoryStream();
    ((IArchiveShrinkable)descriptor).Shrink(image, shrunk);
    Assert.That(shrunk.Length, Is.EqualTo(original.Length));
    using var shrunkReader = new EcryptfsReader(shrunk);
    Assert.That(shrunkReader.ExtractContent("correct horse"), Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Purge_LeavesValidListableEmptyLowerFile() {
    var descriptor = new EcryptfsFormatDescriptor();
    using var image = new MemoryStream(CreateImage(Enumerable.Repeat((byte)0x5A, 5000).ToArray()));

    ((IArchivePurgeable)descriptor).Purge(image);

    Assert.That(image.Length, Is.EqualTo(8192));
    image.Position = 0;
    Assert.That(descriptor.List(image, null), Is.Empty);
    image.Position = 0;
    using var reader = new EcryptfsReader(image);
    Assert.That(reader.DecryptedSize, Is.Zero);
    Assert.That(reader.CanonicalLength, Is.EqualTo(8192));
  }

  [Test, Category("Sad")]
  public void Create_RequiresPassphrase_AndSingleRegularInput() {
    var descriptor = (IArchiveCreatable)new EcryptfsFormatDescriptor();
    using var output = new MemoryStream();
    Assert.Throws<ArgumentException>(() => descriptor.Create(
      output,
      [ArchiveInputInfo.InMemory("a", [1])],
      new FormatCreateOptions()));

    Assert.Throws<NotSupportedException>(() => descriptor.Create(
      output,
      [ArchiveInputInfo.InMemory("a", [1]), ArchiveInputInfo.InMemory("b", [2])],
      new FormatCreateOptions { Password = "pw" }));
  }
}
