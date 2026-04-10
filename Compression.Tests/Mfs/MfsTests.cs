namespace Compression.Tests.Mfs;

[TestFixture]
public class MfsTests {
  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello MFS!"u8.ToArray();
    var w = new FileFormat.Mfs.MfsWriter();
    w.AddFile("TEST", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.Mfs.MfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEST"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileFormat.Mfs.MfsWriter();
    w.AddFile("A", "First"u8.ToArray());
    w.AddFile("B", "Second"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.Mfs.MfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_EmptyDisk() {
    var w = new FileFormat.Mfs.MfsWriter();
    var disk = w.Build();
    using var ms = new MemoryStream(disk);
    var r = new FileFormat.Mfs.MfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Mfs.MfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Mfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".mfs"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0xD2, 0xD7 }));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Mfs.MfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[2048];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Mfs.MfsReader(ms));
  }
}
