namespace Compression.Tests.Uharc;

[TestFixture]
public class UharcTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello, UHARC! Ultra-high compression."u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Uharc.UharcWriter(ms, leaveOpen: true))
      w.AddFile("test.txt", data);
    ms.Position = 0;

    var r = new FileFormat.Uharc.UharcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].FileName, Is.EqualTo("test.txt"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = new byte[1024];
    var data2 = new byte[512];
    Random.Shared.NextBytes(data1);
    Random.Shared.NextBytes(data2);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Uharc.UharcWriter(ms, leaveOpen: true)) {
      w.AddFile("file1.bin", data1);
      w.AddFile("file2.bin", data2);
    }
    ms.Position = 0;

    var r = new FileFormat.Uharc.UharcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsUHA() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Uharc.UharcWriter(ms, leaveOpen: true))
      w.AddFile("x", [1, 2, 3]);
    ms.Position = 0;

    Assert.That(ms.ReadByte(), Is.EqualTo(0x55)); // 'U'
    Assert.That(ms.ReadByte(), Is.EqualTo(0x48)); // 'H'
    Assert.That(ms.ReadByte(), Is.EqualTo(0x41)); // 'A'
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_DirectoryEntry() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Uharc.UharcWriter(ms, leaveOpen: true)) {
      w.AddDirectory("mydir");
      w.AddFile("mydir/hello.txt", "hi"u8.ToArray());
    }
    ms.Position = 0;

    var r = new FileFormat.Uharc.UharcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].IsDirectory, Is.True);
    Assert.That(r.Entries[1].IsDirectory, Is.False);
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("hi"u8.ToArray()));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_EmptyFile() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Uharc.UharcWriter(ms, leaveOpen: true))
      w.AddFile("empty.bin", []);
    ms.Position = 0;

    var r = new FileFormat.Uharc.UharcReader(ms);
    Assert.That(r.Entries[0].OriginalSize, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }
}
