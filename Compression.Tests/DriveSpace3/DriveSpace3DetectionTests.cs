using System.Text;
using Compression.Registry;

namespace Compression.Tests.DriveSpace3;

[TestFixture]
public class DriveSpace3DetectionTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.DriveSpace3.DriveSpace3FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("DriveSpace3"));
    Assert.That(d.DisplayName, Is.EqualTo("DriveSpace 3 CVF"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".cvf"));
    Assert.That(d.Extensions, Is.Empty); // shared with DoubleSpace by magic
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("MS_DSP3"u8.ToArray()));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(3));
    Assert.That(d.Methods.Select(m => m.Name),
      Is.EquivalentTo(new[] { "stored", "ms-lzh", "ms-lzh+", "ms-lzh++" }));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True,
      "DriveSpace 3 is WORM-tier — CanCreate must be advertised once the writer is wired.");
  }

  [Test, Category("HappyPath")]
  public void Reader_AcceptsMsDsp3Signature() {
    var w = new FileSystem.DriveSpace3.DriveSpace3Writer();
    w.AddFile("TEST.TXT", "hello"u8.ToArray());
    var cvf = w.Build();
    using var r = new FileSystem.DriveSpace3.DriveSpace3Reader(new MemoryStream(cvf));
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Signature, Is.EqualTo("MS_DSP3"));
    Assert.That(r.CvfSignature, Is.EqualTo("DVR3"));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsDoubleSpaceSignature() {
    var img = new byte[1024];
    img[0] = 0xEB; img[1] = 0x3C; img[2] = 0x90;
    Encoding.ASCII.GetBytes("MSDSP6.2").CopyTo(img.AsSpan(3));
    Assert.Throws<InvalidDataException>(
      () => _ = new FileSystem.DriveSpace3.DriveSpace3Reader(new MemoryStream(img)));
  }
}
