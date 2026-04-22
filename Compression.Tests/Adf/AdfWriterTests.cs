namespace Compression.Tests.Adf;

[TestFixture]
public class AdfWriterTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello Amiga!"u8.ToArray();
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("hello.txt", data);
    var disk = w.Build();

    Assert.That(disk.Length, Is.EqualTo(901120));

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Adf.AdfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("file1", "First"u8.ToArray());
    w.AddFile("file2", "Second"u8.ToArray());
    w.AddFile("file3", "Third"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Adf.AdfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[5000];
    new Random(42).NextBytes(data);
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("bigfile", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Adf.AdfReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void FFS_Detected() {
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("test", new byte[10]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Adf.AdfReader(ms);
    Assert.That(r.IsFfs, Is.True);
  }

  [Test, Category("RoundTrip")]
  public void EmptyDisk() {
    var w = new FileSystem.Adf.AdfWriter();
    var disk = w.Build();
    Assert.That(disk.Length, Is.EqualTo(901120));

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Adf.AdfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ViaInterface() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileSystem.Adf.AdfFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)desc).Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "TEST", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally {
      File.Delete(tmpFile);
    }
  }
}
