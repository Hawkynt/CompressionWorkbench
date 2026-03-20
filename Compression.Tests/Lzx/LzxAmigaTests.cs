namespace Compression.Tests.Lzx;

[TestFixture]
public class LzxAmigaTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile_Stored() {
    var data = "Hello, Amiga LZX archive!"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Lzx.LzxAmigaWriter(ms, leaveOpen: true))
      w.AddFile("test.txt", data, new DateTime(1995, 6, 15, 10, 30, 0));
    ms.Position = 0;

    var r = new FileFormat.Lzx.LzxAmigaReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var entry = r.Entries[0];
    Assert.That(entry.FileName, Is.EqualTo("test.txt"));
    Assert.That(entry.OriginalSize, Is.EqualTo(data.Length));
    Assert.That(r.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = new byte[1024];
    var data2 = new byte[512];
    Random.Shared.NextBytes(data1);
    Random.Shared.NextBytes(data2);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Lzx.LzxAmigaWriter(ms, leaveOpen: true)) {
      w.AddFile("file1.bin", data1);
      w.AddFile("file2.bin", data2);
    }
    ms.Position = 0;

    var r = new FileFormat.Lzx.LzxAmigaReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsLZX() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Lzx.LzxAmigaWriter(ms, leaveOpen: true))
      w.AddFile("x", [1, 2, 3]);
    ms.Position = 0;

    Assert.That(ms.ReadByte(), Is.EqualTo(0x4C)); // 'L'
    Assert.That(ms.ReadByte(), Is.EqualTo(0x5A)); // 'Z'
    Assert.That(ms.ReadByte(), Is.EqualTo(0x58)); // 'X'
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_EmptyFile() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Lzx.LzxAmigaWriter(ms, leaveOpen: true))
      w.AddFile("empty.dat", []);
    ms.Position = 0;

    var r = new FileFormat.Lzx.LzxAmigaReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].OriginalSize, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[65536];
    Random.Shared.NextBytes(data);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Lzx.LzxAmigaWriter(ms, leaveOpen: true))
      w.AddFile("large.bin", data);
    ms.Position = 0;

    var r = new FileFormat.Lzx.LzxAmigaReader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }
}
