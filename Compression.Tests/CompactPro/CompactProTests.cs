namespace Compression.Tests.CompactPro;

[TestFixture]
public class CompactProTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello, Compact Pro!"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.CompactPro.CompactProWriter(ms, leaveOpen: true))
      w.AddFile("test.txt", data);
    ms.Position = 0;

    var r = new FileFormat.CompactPro.CompactProReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var entry = r.Entries[0];
    Assert.That(entry.FileName, Is.EqualTo("test.txt"));
    Assert.That(entry.DataForkSize, Is.EqualTo(data.Length));
    Assert.That(r.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = new byte[1024];
    var data2 = new byte[512];
    Random.Shared.NextBytes(data1);
    Random.Shared.NextBytes(data2);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.CompactPro.CompactProWriter(ms, leaveOpen: true)) {
      w.AddFile("file1.bin", data1);
      w.AddFile("file2.bin", data2);
    }
    ms.Position = 0;

    var r = new FileFormat.CompactPro.CompactProReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_WithResourceFork() {
    var data = "data fork"u8.ToArray();
    var rsrc = "resource fork"u8.ToArray();

    using var ms = new MemoryStream();
    using (var w = new FileFormat.CompactPro.CompactProWriter(ms, leaveOpen: true))
      w.AddFile("macfile", data, rsrc);
    ms.Position = 0;

    var r = new FileFormat.CompactPro.CompactProReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
    Assert.That(r.ExtractResourceFork(r.Entries[0]), Is.EqualTo(rsrc));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_DirectoryStructure() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.CompactPro.CompactProWriter(ms, leaveOpen: true)) {
      w.AddDirectory("folder");
      w.AddFile("folder/file.txt", "hello"u8.ToArray());
      w.EndDirectory();
    }
    ms.Position = 0;

    var r = new FileFormat.CompactPro.CompactProReader(ms);
    Assert.That(r.Entries.Count, Is.GreaterThanOrEqualTo(1));
    var fileEntry = r.Entries.First(e => !e.IsDirectory);
    Assert.That(r.Extract(fileEntry), Is.EqualTo("hello"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Header_StartsWithMagicByte() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.CompactPro.CompactProWriter(ms, leaveOpen: true))
      w.AddFile("x", [1]);
    ms.Position = 0;
    Assert.That(ms.ReadByte(), Is.EqualTo(0x01));
  }
}
