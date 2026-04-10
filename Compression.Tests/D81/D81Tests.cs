namespace Compression.Tests.D81;

[TestFixture]
public class D81Tests {

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var data = "Hello 1581!"u8.ToArray();
    var w = new FileFormat.D81.D81Writer();
    w.AddFile("HELLO", data);
    var disk = w.Build();

    Assert.That(disk.Length, Is.EqualTo(819200));

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.D81.D81Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileFormat.D81.D81Writer();
    w.AddFile("FILE1", "First"u8.ToArray());
    w.AddFile("FILE2", "Second file data"u8.ToArray());
    w.AddFile("FILE3", new byte[100]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.D81.D81Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("FILE1"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("FILE2"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("FILE3"));

    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("First"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("Second file data"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void DiskSize_Is819200() {
    var w = new FileFormat.D81.D81Writer();
    var disk = w.Build();
    Assert.That(disk.Length, Is.EqualTo(819200));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.D81.D81FormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("D81"));
    Assert.That(desc.Extensions, Does.Contain(".d81"));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.D81.D81Reader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyDisk_NoEntries() {
    var w = new FileFormat.D81.D81Writer();
    var disk = w.Build();
    using var ms = new MemoryStream(disk);
    var r = new FileFormat.D81.D81Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }
}
