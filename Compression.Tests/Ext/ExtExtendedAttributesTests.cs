using FileSystem.Ext;

namespace Compression.Tests.Ext;

[TestFixture]
public class ExtExtendedAttributesTests {

  private static MemoryStream BuildImage() {
    var writer = new ExtWriter();
    writer.AddFile("brick/data.txt", "payload"u8.ToArray());
    var bytes = writer.Build(
      blockSize: 1024,
      totalBlocks: 4096,
      version: ExtWriter.ExtVersion.Ext4,
      journal: false,
      volumeLabel: "xattr-test",
      inodeSize: 256);
    return new MemoryStream(bytes, writable: true);
  }

  [Test, Category("RoundTrip")]
  public void TrustedGfid_CreateUpdateRemove_RoundTrips() {
    using var image = BuildImage();
    byte[] first = [0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE, 1, 2, 3, 4, 5, 6, 7, 8];
    byte[] second = [8, 7, 6, 5, 4, 3, 2, 1, 0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10];

    ExtExtendedAttributes.Set(image, "brick/data.txt", "trusted.gfid", first);
    Assert.That(ExtExtendedAttributes.Read(image, "brick/data.txt")["trusted.gfid"], Is.EqualTo(first));

    ExtExtendedAttributes.Set(image, "brick/data.txt", "trusted.gfid", second);
    Assert.That(ExtExtendedAttributes.Read(image, "brick/data.txt")["trusted.gfid"], Is.EqualTo(second));

    Assert.That(ExtExtendedAttributes.Remove(image, "brick/data.txt", "trusted.gfid"), Is.True);
    Assert.That(ExtExtendedAttributes.Read(image, "brick/data.txt").ContainsKey("trusted.gfid"), Is.False);
    Assert.That(ExtExtendedAttributes.Remove(image, "brick/data.txt", "trusted.gfid"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void MultipleTrustedAttributes_RoundTripWithoutDisturbingPayload() {
    using var image = BuildImage();
    var gfid = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    var dht = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();

    ExtExtendedAttributes.Set(image, "brick/data.txt", "trusted.gfid", gfid);
    ExtExtendedAttributes.Set(image, "brick/data.txt", "trusted.glusterfs.dht", dht);

    var attributes = ExtExtendedAttributes.Read(image, "brick/data.txt");
    Assert.Multiple(() => {
      Assert.That(attributes["trusted.gfid"], Is.EqualTo(gfid));
      Assert.That(attributes["trusted.glusterfs.dht"], Is.EqualTo(dht));
    });

    image.Position = 0;
    using var reader = new ExtReader(image, leaveOpen: true);
    var entry = reader.Entries.Single(entry => entry.Name == "brick/data.txt");
    Assert.That(reader.Extract(entry), Is.EqualTo("payload"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void OversizedInlineSet_FailsWithoutChangingExistingAttribute() {
    using var image = BuildImage();
    var gfid = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    ExtExtendedAttributes.Set(image, "brick/data.txt", "trusted.gfid", gfid);

    Assert.Throws<NotSupportedException>(() =>
      ExtExtendedAttributes.Set(image, "brick/data.txt", "trusted.too-large", new byte[128]));

    Assert.That(ExtExtendedAttributes.Read(image, "brick/data.txt")["trusted.gfid"], Is.EqualTo(gfid));
  }
}
