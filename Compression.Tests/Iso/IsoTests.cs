using System.Text;

namespace Compression.Tests.Iso;

[TestFixture]
public class IsoTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello ISO!"u8.ToArray();
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("TEST.TXT", data);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Iso.IsoReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Does.Contain("TEST"));
    Assert.That(r.Entries[0].IsDirectory, Is.False);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("A.TXT", "First"u8.ToArray());
    w.AddFile("B.TXT", "Second"u8.ToArray());
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Iso.IsoReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_EmptyImage() {
    var w = new FileSystem.Iso.IsoWriter();
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Iso.IsoReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[5000];
    new Random(42).NextBytes(data);
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("BIG.DAT", data);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Iso.IsoReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.Iso.IsoFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Iso"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".iso"));
    Assert.That(desc.Extensions, Does.Contain(".iso"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(3));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(0x8001));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(desc.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("FILE.BIN", new byte[10]);
    var image = w.Build();
    using var ms = new MemoryStream(image);

    var desc = new FileSystem.Iso.IsoFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Iso.IsoReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[20 * 2048];
    data[16 * 2048] = 1; // PVD type byte
    // No CD001 magic
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Iso.IsoReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Magic_Detection() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("X.TXT", new byte[1]);
    var image = w.Build();

    // Verify CD001 exists at offset 0x8001
    Assert.That(Encoding.ASCII.GetString(image, 0x8001, 5), Is.EqualTo("CD001"));
  }
}
