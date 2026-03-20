using FileFormat.CramFs;

namespace Compression.Tests.CramFs;

[TestFixture]
public class CramFsTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello, CramFS!"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new CramFsWriter(ms, leaveOpen: true))
      w.AddFile("/test.txt", data);

    ms.Position = 0;
    var r = new CramFsReader(ms);
    var files = r.Entries.Where(e => e.IsRegularFile).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("test.txt"));
    Assert.That(r.Extract(files[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = new byte[1024];
    var data2 = new byte[512];
    Random.Shared.NextBytes(data1);
    Random.Shared.NextBytes(data2);

    using var ms = new MemoryStream();
    using (var w = new CramFsWriter(ms, leaveOpen: true)) {
      w.AddFile("/file1.bin", data1);
      w.AddFile("/file2.bin", data2);
    }

    ms.Position = 0;
    var r = new CramFsReader(ms);
    var files = r.Entries.Where(e => e.IsRegularFile).ToList();
    Assert.That(files, Has.Count.GreaterThanOrEqualTo(2));

    var f1 = files.First(e => e.Name == "file1.bin");
    var f2 = files.First(e => e.Name == "file2.bin");
    Assert.That(r.Extract(f1), Is.EqualTo(data1));
    Assert.That(r.Extract(f2), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_DirectoryAndFile() {
    var data = "nested"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new CramFsWriter(ms, leaveOpen: true)) {
      w.AddDirectory("/subdir");
      w.AddFile("/subdir/hello.txt", data);
    }

    ms.Position = 0;
    var r = new CramFsReader(ms);
    var dirs = r.Entries.Where(e => e.IsDirectory && e.Name == "subdir").ToList();
    var files = r.Entries.Where(e => e.IsRegularFile).ToList();

    Assert.That(dirs, Has.Count.EqualTo(1));
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(r.Extract(files[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsCramFS() {
    using var ms = new MemoryStream();
    using (var w = new CramFsWriter(ms, leaveOpen: true))
      w.AddFile("/x", [1, 2, 3]);

    ms.Position = 0;
    Span<byte> magic = stackalloc byte[4];
    ms.ReadExactly(magic);
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magic),
      Is.EqualTo(0x28CD3D45u));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_EmptyFile() {
    using var ms = new MemoryStream();
    using (var w = new CramFsWriter(ms, leaveOpen: true))
      w.AddFile("/empty.txt", []);

    ms.Position = 0;
    var r = new CramFsReader(ms);
    var files = r.Entries.Where(e => e.IsRegularFile).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(r.Extract(files[0]), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_Symlink() {
    using var ms = new MemoryStream();
    using (var w = new CramFsWriter(ms, leaveOpen: true)) {
      w.AddFile("/target.txt", "data"u8.ToArray());
      w.AddSymlink("/link.txt", "target.txt");
    }

    ms.Position = 0;
    var r = new CramFsReader(ms);
    var symlinks = r.Entries.Where(e => e.IsSymlink).ToList();
    Assert.That(symlinks, Has.Count.EqualTo(1));
    // Symlink target is stored as file data
    var target = r.Extract(symlinks[0]);
    Assert.That(System.Text.Encoding.UTF8.GetString(target), Is.EqualTo("target.txt"));
  }
}
