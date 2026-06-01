using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Tests.Ecryptfs;

[TestFixture]
public class EcryptfsDetectionTests {

  // Build a minimal eCryptfs file: marker 0x3C81B7F5 + 8-byte BE decrypted size + 4-byte flags + 4-byte extent-size,
  // padded out to one extent so the reader can carve the opaque ciphertext blob.
  private static byte[] BuildMinimalFile(ulong decryptedSize = 4096, uint flags = 0, uint extentSize = 4096, int cipherLen = 4096) {
    var image = new byte[Math.Max((int)extentSize, 64) + cipherLen];
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0, 4), 0x3C81B7F5u);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(4, 8), decryptedSize);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(12, 4), flags);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(16, 4), extentSize);
    for (var i = 0; i < cipherLen; i++) image[Math.Max((int)extentSize, 64) + i] = (byte)((i * 7) ^ 0xA5);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties_AndMagic() {
    var d = new FileSystem.Ecryptfs.EcryptfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ecryptfs"));
    Assert.That(d.DisplayName, Is.EqualTo("eCryptfs"));
    Assert.That(d.Extensions, Does.Contain(".ecryptfs"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x3C, 0x81, 0xB7, 0xF5 }));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Read_MinimalFile_SurfacesHeader() {
    using var ms = new MemoryStream(BuildMinimalFile(decryptedSize: 12345, flags: 0x10, extentSize: 4096, cipherLen: 8192));
    var r = new FileSystem.Ecryptfs.EcryptfsReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Marker, Is.EqualTo(0x3C81B7F5u));
    Assert.That(r.DecryptedSize, Is.EqualTo(12345ul));
    Assert.That(r.Flags, Is.EqualTo(0x10u));
    Assert.That(r.ExtentSize, Is.EqualTo(4096u));

    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.ecryptfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("ciphertext.bin"));
  }

  [Test, Category("Sad")]
  public void Read_BadMarker_Throws() {
    var img = new byte[64];
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Ecryptfs.EcryptfsReader(ms));
  }
}
