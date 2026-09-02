using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.OrangeFs;

[TestFixture]
public class OrangeFsDetectionTests {

  private static byte[] BuildDbpf(string tag, uint version = 1, uint dsType = 2, int payloadLen = 64) {
    var tagBytes = Encoding.ASCII.GetBytes(tag);
    Assert.That(tagBytes, Has.Length.EqualTo(4));
    var image = new byte[16 + payloadLen];
    Array.Copy(tagBytes, 0, image, 0, 4);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4, 4), version);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8, 4), dsType);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12, 4), (uint)payloadLen);
    for (var i = 0; i < payloadLen; i++) image[16 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties_AndMagic() {
    var d = new FileSystem.OrangeFs.OrangeFsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("OrangeFs"));
    Assert.That(d.DisplayName, Does.Contain("OrangeFS"));
    Assert.That(d.Extensions, Does.Contain(".orangefs"));
    Assert.That(d.Extensions, Does.Contain(".pvfs"));
    Assert.That(d.Extensions, Does.Contain(".bstream"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("PVFS"u8.ToArray()));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo("OGFP"u8.ToArray()));
    // The DBPF writer landed, so the descriptor is creatable. What the payload
    // means still needs the cluster's fs.conf — see OrangeFsStubBehaviorTests
    // for the opaque-entry shape and the Description that says so.
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Read_PvfsTag_Classic() {
    using var ms = new MemoryStream(BuildDbpf("PVFS", version: 3, dsType: 7, payloadLen: 128));
    var r = new FileSystem.OrangeFs.OrangeFsReader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Tag, Is.EqualTo("PVFS"));
    Assert.That(r.IsOrangeFs, Is.False);
    Assert.That(r.Version, Is.EqualTo(3u));
    Assert.That(r.DatastreamType, Is.EqualTo(7u));
    Assert.That(r.ObjectSize, Is.EqualTo(128u));
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.pvfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("object.bin"));
  }

  [Test, Category("HappyPath")]
  public void Read_OgfpTag_OrangeFsNative() {
    using var ms = new MemoryStream(BuildDbpf("OGFP", version: 4, dsType: 9, payloadLen: 256));
    var r = new FileSystem.OrangeFs.OrangeFsReader(ms);
    Assert.That(r.Tag, Is.EqualTo("OGFP"));
    Assert.That(r.IsOrangeFs, Is.True);
    Assert.That(r.Entries.Any(e => e.Name == "FULL.orangefs"), Is.True);
  }

  [Test, Category("Sad")]
  public void Read_BadTag_Throws() {
    var img = new byte[32];
    Encoding.ASCII.GetBytes("XXXX").CopyTo(img.AsSpan(0, 4));
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.OrangeFs.OrangeFsReader(ms));
  }
}
