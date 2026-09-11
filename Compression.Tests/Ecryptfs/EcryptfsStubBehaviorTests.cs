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
