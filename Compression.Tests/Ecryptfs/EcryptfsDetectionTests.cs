using Compression.Registry;
using FileSystem.Ecryptfs;

namespace Compression.Tests.Ecryptfs;

[TestFixture]
public class EcryptfsDetectionTests {

  private static byte[] CreateImage(byte[] content, string password = "foo", string? encryptionMethod = null) {
    var descriptor = new EcryptfsFormatDescriptor();
    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(
      image,
      [ArchiveInputInfo.InMemory("payload.bin", content)],
      new FormatCreateOptions { Password = password, EncryptionMethod = encryptionMethod });
    return image.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesPasswordAwareWorm_WithoutFakeMagic() {
    var descriptor = new EcryptfsFormatDescriptor();
    Assert.That(descriptor.Id, Is.EqualTo("Ecryptfs"));
    Assert.That(descriptor.DisplayName, Is.EqualTo("eCryptfs"));
    Assert.That(descriptor.Extensions, Does.Contain(".ecryptfs"));
    Assert.That(descriptor.MagicSignatures, Is.Empty,
      "The eCryptfs marker is a relation between two random words, not fixed bytes.");
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsPassword), Is.True);
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      "The generic mutation API carries no passphrase, so plaintext replacement cannot be exposed honestly.");
    Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
    Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
    Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
  }

  [Test, Category("HappyPath")]
  public void Read_CreatedLowerFile_SurfacesKernelHeaderGeometry() {
    var content = Enumerable.Range(0, 12345).Select(i => (byte)(i * 37)).ToArray();
    using var image = new MemoryStream(CreateImage(content), writable: false);
    using var reader = new EcryptfsReader(image);

    Assert.That(reader.Marker, Is.EqualTo(0x3C81B7F5u));
    Assert.That(reader.DecryptedSize, Is.EqualTo(12345ul));
    Assert.That(reader.FileVersion, Is.EqualTo(3));
    Assert.That(reader.Flags & 0x00000002u, Is.Not.Zero);
    Assert.That(reader.ExtentSize, Is.EqualTo(4096u));
    Assert.That(reader.HeaderExtentCount, Is.EqualTo(2));
    Assert.That(reader.MetadataSize, Is.EqualTo(8192));
    Assert.That(reader.CanonicalLength, Is.EqualTo(8192 + 4 * 4096));
    Assert.That(reader.CipherDescription, Is.EqualTo("AES-128-CBC"));
    Assert.That(reader.Entries.Select(e => e.Name), Is.EqualTo(new[] { "content.bin" }));
  }

  [Test, Category("Interop")]
  public void PassphraseSignature_MatchesEcryptfsUtilsKnownAnswerVector() {
    // ecryptfs-utils tests/userspace/verify-passphrase-sig.sh:
    // pass="foo", default salt 0011223344556677 => 253ca7e88811d184.
    using var image = new MemoryStream(CreateImage([1, 2, 3], password: "foo"), writable: false);
    using var reader = new EcryptfsReader(image);
    Assert.That(reader.PassphraseSignature, Is.EqualTo("253ca7e88811d184"));
  }

  [Test, Category("Sad")]
  public void Read_BrokenMarkerRelation_Throws() {
    var bytes = CreateImage([1, 2, 3]);
    bytes[12] ^= 0x80;
    using var image = new MemoryStream(bytes, writable: false);
    Assert.Throws<InvalidDataException>(() => _ = new EcryptfsReader(image));
  }

  [Test, Category("Sad")]
  public void Read_TruncatedCiphertext_Throws() {
    var bytes = CreateImage(new byte[4097]);
    Array.Resize(ref bytes, bytes.Length - 1);
    using var image = new MemoryStream(bytes, writable: false);
    Assert.Throws<InvalidDataException>(() => _ = new EcryptfsReader(image));
  }
}
