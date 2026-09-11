using FileSystem.Xfs;

namespace Compression.Tests.Xfs;

[TestFixture]
public class XfsExtendedAttributesTests {

  private static MemoryStream BuildImage() {
    var writer = new XfsWriter();
    writer.AddFile("brick/data.txt", "payload"u8.ToArray());
    var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;
    return image;
  }

  [Test, Category("RoundTrip")]
  public void TrustedGfid_CreateUpdateRemove_RoundTrips() {
    using var image = BuildImage();
    byte[] first = [0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE, 1, 2, 3, 4, 5, 6, 7, 8];
    byte[] second = [8, 7, 6, 5, 4, 3, 2, 1, 0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10];

    XfsExtendedAttributes.Set(image, "brick/data.txt", "trusted.gfid", first);
    Assert.That(XfsExtendedAttributes.Read(image, "brick/data.txt")["trusted.gfid"], Is.EqualTo(first));

    XfsExtendedAttributes.Set(image, "brick/data.txt", "trusted.gfid", second);
    Assert.That(XfsExtendedAttributes.Read(image, "brick/data.txt")["trusted.gfid"], Is.EqualTo(second));

    Assert.That(XfsExtendedAttributes.Remove(image, "brick/data.txt", "trusted.gfid"), Is.True);
    Assert.That(XfsExtendedAttributes.Read(image, "brick/data.txt").ContainsKey("trusted.gfid"), Is.False);
    Assert.That(XfsExtendedAttributes.Remove(image, "brick/data.txt", "trusted.gfid"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void TrustedGfid_PreservesFilePayload() {
    using var image = BuildImage();
    var gfid = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

    XfsExtendedAttributes.Set(image, "brick/data.txt", "trusted.gfid", gfid);

    image.Position = 0;
    using var reader = new XfsReader(image, leaveOpen: true);
    var entry = reader.Entries.Single(entry => entry.Name == "brick/data.txt");
    Assert.Multiple(() => {
      Assert.That(reader.Extract(entry), Is.EqualTo("payload"u8.ToArray()));
      Assert.That(XfsExtendedAttributes.Read(image, "brick/data.txt")["trusted.gfid"], Is.EqualTo(gfid));
    });
  }

  [Test, Category("ErrorHandling")]
  public void OversizedShortFormSet_FailsWithoutChangingExistingAttribute() {
    using var image = BuildImage();
    var gfid = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    XfsExtendedAttributes.Set(image, "brick/data.txt", "trusted.gfid", gfid);

    Assert.Throws<NotSupportedException>(() =>
      XfsExtendedAttributes.Set(image, "brick/data.txt", "trusted.too-large", new byte[128]));

    Assert.That(XfsExtendedAttributes.Read(image, "brick/data.txt")["trusted.gfid"], Is.EqualTo(gfid));
  }
}
