namespace Compression.Tests.D71;

[TestFixture]
public class D71Tests {

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var data = "Hello 1571!"u8.ToArray();
    var w = new FileSystem.D71.D71Writer();
    w.AddFile("HELLO", data);
    var disk = w.Build();

    Assert.That(disk.Length, Is.EqualTo(349696));

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.D71.D71Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileSystem.D71.D71Writer();
    w.AddFile("FILE1", "First"u8.ToArray());
    w.AddFile("FILE2", "Second file data"u8.ToArray());
    w.AddFile("FILE3", new byte[100]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.D71.D71Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("FILE1"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("FILE2"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("FILE3"));

    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("First"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("Second file data"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void DiskSize_Is349696() {
    var w = new FileSystem.D71.D71Writer();
    var disk = w.Build();
    Assert.That(disk.Length, Is.EqualTo(349696));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.D71.D71FormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("D71"));
    Assert.That(desc.Extensions, Does.Contain(".d71"));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.D71.D71Reader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyDisk_NoEntries() {
    var w = new FileSystem.D71.D71Writer();
    var disk = w.Build();
    using var ms = new MemoryStream(disk);
    var r = new FileSystem.D71.D71Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }
}
