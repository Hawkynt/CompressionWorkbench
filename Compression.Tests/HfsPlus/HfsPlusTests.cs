using FileFormat.HfsPlus;

namespace Compression.Tests.HfsPlus;

[TestFixture]
public class HfsPlusTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile() {
    var content = "Hello, HFS+!"u8.ToArray();
    var writer = new HfsPlusWriter();
    writer.AddFile("test.txt", content);
    var image = writer.Build();

    using var ms = new MemoryStream(image);
    var reader = new HfsPlusReader(ms);

    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("test.txt"));
    Assert.That(files[0].Size, Is.EqualTo(content.Length));

    var extracted = reader.Extract(files[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MultipleFiles() {
    var data1 = new byte[100];
    var data2 = new byte[200];
    new Random(42).NextBytes(data1);
    new Random(43).NextBytes(data2);

    var writer = new HfsPlusWriter();
    writer.AddFile("alpha.bin", data1);
    writer.AddFile("beta.bin", data2);
    var image = writer.Build();

    using var ms = new MemoryStream(image);
    var reader = new HfsPlusReader(ms);

    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(2));

    var names = files.Select(f => f.Name).OrderBy(n => n).ToArray();
    Assert.That(names, Does.Contain("alpha.bin"));
    Assert.That(names, Does.Contain("beta.bin"));

    var alphaEntry = files.First(f => f.Name == "alpha.bin");
    var betaEntry = files.First(f => f.Name == "beta.bin");
    Assert.That(reader.Extract(alphaEntry), Is.EqualTo(data1));
    Assert.That(reader.Extract(betaEntry), Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Properties() {
    var desc = new HfsPlusFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("HfsPlus"));
    Assert.That(desc.DisplayName, Is.EqualTo("HFS+"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".dmg"));
    Assert.That(desc.Extensions, Does.Contain(".hfsx"));
    Assert.That(desc.Extensions, Does.Contain(".hfs"));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(1024));
    Assert.That(desc.MagicSignatures[0].Confidence, Is.EqualTo(0.85));
  }

  [Category("ErrorHandling")]
  [Test]
  public void Reader_TooSmall_Throws() {
    var tiny = new byte[100];
    using var ms = new MemoryStream(tiny);
    Assert.Throws<InvalidDataException>(() => new HfsPlusReader(ms));
  }

  [Category("ErrorHandling")]
  [Test]
  public void Reader_BadMagic_Throws() {
    var bad = new byte[2048];
    // Write invalid signature at offset 1024.
    bad[1024] = 0xFF;
    bad[1025] = 0xFF;
    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => new HfsPlusReader(ms));
  }

  [Category("HappyPath")]
  [Test]
  public void EmptyDisk_NoEntries() {
    var writer = new HfsPlusWriter();
    var image = writer.Build();

    using var ms = new MemoryStream(image);
    var reader = new HfsPlusReader(ms);

    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(0));
  }
}
