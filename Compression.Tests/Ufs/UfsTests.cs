namespace Compression.Tests.Ufs;

[TestFixture]
public class UfsTests {
  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello UFS!"u8.ToArray();
    var w = new FileFormat.Ufs.UfsWriter();
    w.AddFile("test.txt", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.Ufs.UfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileFormat.Ufs.UfsWriter();
    w.AddFile("a.txt", "First"u8.ToArray());
    w.AddFile("b.txt", "Second"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.Ufs.UfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_EmptyDisk() {
    var w = new FileFormat.Ufs.UfsWriter();
    var disk = w.Build();
    using var ms = new MemoryStream(disk);
    var r = new FileFormat.Ufs.UfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Ufs.UfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Ufs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".ufs"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Ufs.UfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[16384];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Ufs.UfsReader(ms));
  }
}
