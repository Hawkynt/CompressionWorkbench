using FileSystem.SquashFs;

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
    new Random(42).NextBytes(data1);
    new Random(42).NextBytes(data2);

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

  // ── Modify / Defragment / ExtentMap tests ────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsModifiable() {
    var desc = new SquashFsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveModifiable>());
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveDefragmentable>());
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IFilesystemExtentMap>());
    // Linux mounts SquashFS read-only, but this API reports offline image-editor
    // semantics. Add/Remove rebuild and verify a new valid image, so existing
    // instances are genuinely modifiable at the workbench surface — the case
    // FormatCapabilities.cs names SquashFS in.
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void AddRemove_RoundTrips() {
    using var ms = new MemoryStream();
    using (var w = new SquashFsWriter(ms, leaveOpen: true))
      w.AddFile("keep.txt", "keep"u8.ToArray());

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "added"u8.ToArray());
      var desc = new SquashFsFormatDescriptor();
      ((Compression.Registry.IArchiveModifiable)desc).Add(ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "new.txt", false)]);

      ms.Position = 0;
      var r = new SquashFsReader(ms, leaveOpen: true);
      Assert.That(r.Entries.Any(e => e.FullPath.Contains("keep.txt")), Is.True);
      Assert.That(r.Entries.Any(e => e.FullPath.Contains("new.txt")), Is.True);

      ((Compression.Registry.IArchiveModifiable)desc).Remove(ms, ["keep.txt"]);
      ms.Position = 0;
      var r2 = new SquashFsReader(ms, leaveOpen: true);
      Assert.That(r2.Entries.Any(e => !e.IsDirectory && e.FullPath.Contains("keep.txt")), Is.False);
      Assert.That(r2.Entries.Any(e => e.FullPath.Contains("new.txt")), Is.True);
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Defragment_PreservesFiles() {
    using var ms = new MemoryStream();
    using (var w = new SquashFsWriter(ms, leaveOpen: true)) {
      w.AddFile("a.txt", "alpha"u8.ToArray());
      w.AddFile("b.txt", "beta"u8.ToArray());
    }

    new SquashFsFormatDescriptor().Defragment(ms,
      new Compression.Registry.DefragOptions { Mode = Compression.Registry.DefragMode.ConsolidateAtStart });

    ms.Position = 0;
    var r = new SquashFsReader(ms, leaveOpen: true);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files.Any(e => e.FullPath.Contains("a.txt")), Is.True);
    Assert.That(files.Any(e => e.FullPath.Contains("b.txt")), Is.True);
  }

  [Test, Category("HappyPath")]
  public void ExtentMap_ReturnsEntries() {
    using var ms = new MemoryStream();
    using (var w = new SquashFsWriter(ms, leaveOpen: true))
      w.AddFile("x.bin", new byte[100]);
    ms.Position = 0;

    var desc = new SquashFsFormatDescriptor();
    var extents = ((Compression.Registry.IFilesystemExtentMap)desc).EnumerateExtents(ms).ToList();
    Assert.That(extents, Has.Count.GreaterThan(0));
    Assert.That(extents.Any(e => e.Kind == Compression.Registry.DefragBlockKind.MetadataReserved), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[200_000];
    new Random(42).NextBytes(data);

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
