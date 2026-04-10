namespace Compression.Tests.D64;

[TestFixture]
public class D64Tests {

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var data = "Hello C64!"u8.ToArray();
    var w = new FileFormat.D64.D64Writer();
    w.AddFile("HELLO", data);
    var disk = w.Build();

    Assert.That(disk.Length, Is.EqualTo(174848));

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.D64.D64Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileFormat.D64.D64Writer();
    w.AddFile("FILE1", "First"u8.ToArray());
    w.AddFile("FILE2", "Second file data"u8.ToArray());
    w.AddFile("FILE3", new byte[100]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.D64.D64Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("FILE1"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("FILE2"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("FILE3"));

    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("First"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("Second file data"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_LargeFile() {
    var data = new byte[5000];
    new Random(42).NextBytes(data);
    var w = new FileFormat.D64.D64Writer();
    w.AddFile("BIGFILE", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.D64.D64Reader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void DiskSize_IsStandard() {
    var w = new FileFormat.D64.D64Writer();
    var disk = w.Build();
    Assert.That(disk.Length, Is.EqualTo(174848));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.D64.D64FormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("D64"));
    Assert.That(desc.Extensions, Does.Contain(".d64"));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var w = new FileFormat.D64.D64Writer();
    w.AddFile("TEST", new byte[50]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var desc = new FileFormat.D64.D64FormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_ViaInterface() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileFormat.D64.D64FormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveFormatOperations)desc).Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "TEST", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally { File.Delete(tmpFile); }
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.D64.D64Reader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyDisk_NoEntries() {
    var w = new FileFormat.D64.D64Writer();
    var disk = w.Build();
    using var ms = new MemoryStream(disk);
    var r = new FileFormat.D64.D64Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("EdgeCase")]
  public void FileType_PRG() {
    var w = new FileFormat.D64.D64Writer();
    w.AddFile("GAME", 0x82, new byte[10]);
    var disk = w.Build();
    using var ms = new MemoryStream(disk);
    var r = new FileFormat.D64.D64Reader(ms);
    Assert.That(r.Entries[0].FileType, Is.EqualTo(0x82));
  }
}
