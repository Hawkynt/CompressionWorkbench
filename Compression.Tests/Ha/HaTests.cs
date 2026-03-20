namespace Compression.Tests.Ha;

[TestFixture]
public class HaTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello, Ha archive!"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Ha.HaWriter(ms, leaveOpen: true))
      w.AddFile("test.txt", data, new DateTime(2024, 6, 15, 10, 30, 0));
    ms.Position = 0;

    var r = new FileFormat.Ha.HaReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var entry = r.Entries[0];
    Assert.That(entry.FileName, Is.EqualTo("test.txt"));
    Assert.That(entry.OriginalSize, Is.EqualTo(data.Length));
    Assert.That(entry.Method, Is.EqualTo(0)); // Store
    Assert.That(entry.IsDirectory, Is.False);
    Assert.That(r.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = new byte[1024];
    var data2 = new byte[512];
    Random.Shared.NextBytes(data1);
    Random.Shared.NextBytes(data2);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Ha.HaWriter(ms, leaveOpen: true)) {
      w.AddFile("file1.bin", data1);
      w.AddFile("subdir/file2.bin", data2);
    }
    ms.Position = 0;

    var r = new FileFormat.Ha.HaReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].FileName, Is.EqualTo("file1.bin"));
    Assert.That(r.Entries[1].FileName, Is.EqualTo("subdir/file2.bin"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_DirectoryEntry() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Ha.HaWriter(ms, leaveOpen: true)) {
      w.AddDirectory("mydir");
      w.AddFile("mydir/hello.txt", "hi"u8.ToArray());
    }
    ms.Position = 0;

    var r = new FileFormat.Ha.HaReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].IsDirectory, Is.True);
    Assert.That(r.Entries[0].FileName, Is.EqualTo("mydir"));
    Assert.That(r.Entries[1].IsDirectory, Is.False);
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_EmptyFile() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Ha.HaWriter(ms, leaveOpen: true))
      w.AddFile("empty.txt", []);
    ms.Position = 0;

    var r = new FileFormat.Ha.HaReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].OriginalSize, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Magic_IsHA() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Ha.HaWriter(ms, leaveOpen: true))
      w.AddFile("x", [1, 2, 3]);
    ms.Position = 0;
    Assert.That(ms.ReadByte(), Is.EqualTo(0x48)); // 'H'
    Assert.That(ms.ReadByte(), Is.EqualTo(0x41)); // 'A'
  }
}
