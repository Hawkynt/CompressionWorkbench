using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Tests.TahoeLafs;

[TestFixture]
public class TahoeLafsDetectionTests {

  // Build a minimal Tahoe-LAFS share-v1 bucket: 4-byte BE version + 4-byte BE data-size + 4-byte BE lease-count + payload.
  private static byte[] BuildMinimalShare(uint version = 1, int payloadLen = 64) {
    var image = new byte[12 + payloadLen];
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0, 4), version);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4, 4), (uint)payloadLen);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8, 4), 3);
    for (var i = 0; i < payloadLen; i++) image[12 + i] = (byte)(i ^ 0x5A);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties_AndMagic() {
    var d = new FileSystem.TahoeLafs.TahoeLafsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("TahoeLafs"));
    Assert.That(d.DisplayName, Does.Contain("Tahoe"));
    Assert.That(d.Extensions, Does.Contain(".tahoe-share"));
    Assert.That(d.Extensions, Does.Contain(".share"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x00, 0x00, 0x00, 0x01 }));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo(new byte[] { 0x00, 0x00, 0x00, 0x02 }));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Read_V1Share_SurfacesHeader() {
    using var ms = new MemoryStream(BuildMinimalShare(version: 1, payloadLen: 128));
    var r = new FileSystem.TahoeLafs.TahoeLafsReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Version, Is.EqualTo(1u));
    Assert.That(r.DataSize, Is.EqualTo(128u));
    Assert.That(r.LeaseCount, Is.EqualTo(3u));
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.tahoe-share"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("share.immutable.bin"));
  }

  [Test, Category("HappyPath")]
  public void Read_V2Share_NamedMutable() {
    using var ms = new MemoryStream(BuildMinimalShare(version: 2, payloadLen: 32));
    var r = new FileSystem.TahoeLafs.TahoeLafsReader(ms);
    Assert.That(r.Version, Is.EqualTo(2u));
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("share.mutable.bin"));
  }

  [Test, Category("Sad")]
  public void Read_BadVersion_Throws() {
    var img = BuildMinimalShare(version: 99);
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.TahoeLafs.TahoeLafsReader(ms));
  }
}
