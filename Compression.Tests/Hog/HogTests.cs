using Compression.Registry;

namespace Compression.Tests.Hog;

[TestFixture]
public class HogTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "test data"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Hog.HogWriter(ms, leaveOpen: true))
      w.AddFile("test.bin", data);
    ms.Position = 0;

    var r = new FileFormat.Hog.HogReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.bin"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = new byte[64];
    var data2 = new byte[128];
    var data3 = new byte[32];
    Array.Fill(data1, (byte)0xAA);
    Array.Fill(data2, (byte)0xBB);
    Array.Fill(data3, (byte)0xCC);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Hog.HogWriter(ms, leaveOpen: true)) {
      w.AddFile("file1.bin", data1);
      w.AddFile("file2.bin", data2);
      w.AddFile("file3.bin", data3);
    }
    ms.Position = 0;

    var r = new FileFormat.Hog.HogReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("file1.bin"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("file2.bin"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("file3.bin"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(data3));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Hog.HogFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Hog"));
    Assert.That(d.Extensions, Contains.Item(".hog"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { (byte)'D', (byte)'H', (byte)'F' }));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void BadMagic_Throws() {
    var bad = new byte[] { 0x00, 0x01, 0x02 };
    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Hog.HogReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void TooSmall_Throws() {
    var tiny = new byte[] { 0x44 }; // just one byte
    using var ms = new MemoryStream(tiny);
    Assert.Throws<EndOfStreamException>(() => _ = new FileFormat.Hog.HogReader(ms));
  }
}
