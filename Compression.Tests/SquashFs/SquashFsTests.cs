using FileFormat.SquashFs;

namespace Compression.Tests.SquashFs;

[TestFixture]
public class SquashFsTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello, SquashFS!"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new SquashFsWriter(ms, leaveOpen: true))
      w.AddFile("hello.txt", data);

    ms.Position = 0;
    var r = new SquashFsReader(ms, leaveOpen: true);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].FullPath, Does.Contain("hello.txt"));
    Assert.That(r.Extract(files[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = new byte[1024];
    var data2 = new byte[512];
    Random.Shared.NextBytes(data1);
    Random.Shared.NextBytes(data2);

    using var ms = new MemoryStream();
    using (var w = new SquashFsWriter(ms, leaveOpen: true)) {
      w.AddFile("file1.bin", data1);
      w.AddFile("file2.bin", data2);
    }

    ms.Position = 0;
    var r = new SquashFsReader(ms, leaveOpen: true);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.GreaterThanOrEqualTo(2));

    var f1 = files.First(e => e.FullPath.Contains("file1.bin"));
    var f2 = files.First(e => e.FullPath.Contains("file2.bin"));
    Assert.That(r.Extract(f1), Is.EqualTo(data1));
    Assert.That(r.Extract(f2), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_DirectoryAndFile() {
    var data = "nested content"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new SquashFsWriter(ms, leaveOpen: true)) {
      w.AddDirectory("subdir");
      w.AddFile("subdir/test.txt", data);
    }

    ms.Position = 0;
    var r = new SquashFsReader(ms, leaveOpen: true);
    var dirs = r.Entries.Where(e => e.IsDirectory).ToList();
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();

    Assert.That(dirs.Any(d => d.FullPath.Contains("subdir")), Is.True);
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(r.Extract(files[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsSquashFS() {
    using var ms = new MemoryStream();
    using (var w = new SquashFsWriter(ms, leaveOpen: true))
      w.AddFile("x", [1, 2, 3]);

    ms.Position = 0;
    Span<byte> magic = stackalloc byte[4];
    ms.ReadExactly(magic);
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magic),
      Is.EqualTo(0x73717368u));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_EmptyFile() {
    using var ms = new MemoryStream();
    using (var w = new SquashFsWriter(ms, leaveOpen: true))
      w.AddFile("empty.txt", []);

    ms.Position = 0;
    var r = new SquashFsReader(ms, leaveOpen: true);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(r.Extract(files[0]), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[200_000];
    Random.Shared.NextBytes(data);

    using var ms = new MemoryStream();
    using (var w = new SquashFsWriter(ms, leaveOpen: true))
      w.AddFile("large.bin", data);

    ms.Position = 0;
    var r = new SquashFsReader(ms, leaveOpen: true);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(r.Extract(files[0]), Is.EqualTo(data));
  }
}
