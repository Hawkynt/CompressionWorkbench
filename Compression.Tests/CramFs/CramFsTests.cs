using FileSystem.CramFs;

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

  // ── Modify / Defragment / ExtentMap tests ────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsModifiable() {
    var desc = new CramFsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveModifiable>());
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveDefragmentable>());
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IFilesystemExtentMap>());
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void AddRemove_RoundTrips() {
    using var ms = new MemoryStream();
    using (var w = new CramFsWriter(ms, leaveOpen: true))
      w.AddFile("/keep.txt", "keep"u8.ToArray());

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "added"u8.ToArray());
      var desc = new CramFsFormatDescriptor();
      ((Compression.Registry.IArchiveModifiable)desc).Add(ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "/new.txt", false)]);

      ms.Position = 0;
      var r = new CramFsReader(ms);
      Assert.That(r.Entries.Any(e => e.IsRegularFile && e.FullPath.Contains("keep.txt")), Is.True);
      Assert.That(r.Entries.Any(e => e.IsRegularFile && e.FullPath.Contains("new.txt")), Is.True);

      ((Compression.Registry.IArchiveModifiable)desc).Remove(ms, ["/keep.txt"]);
      ms.Position = 0;
      var r2 = new CramFsReader(ms);
      Assert.That(r2.Entries.Any(e => e.IsRegularFile && e.FullPath.Contains("keep.txt")), Is.False);
      Assert.That(r2.Entries.Any(e => e.IsRegularFile && e.FullPath.Contains("new.txt")), Is.True);
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Defragment_PreservesFiles() {
    using var ms = new MemoryStream();
    using (var w = new CramFsWriter(ms, leaveOpen: true)) {
      w.AddFile("/a.txt", "alpha"u8.ToArray());
      w.AddFile("/b.txt", "beta"u8.ToArray());
    }

    new CramFsFormatDescriptor().Defragment(ms,
      new Compression.Registry.DefragOptions { Mode = Compression.Registry.DefragMode.ConsolidateAtStart });

    ms.Position = 0;
    var r = new CramFsReader(ms);
    var files = r.Entries.Where(e => e.IsRegularFile).ToList();
    Assert.That(files.Any(e => e.FullPath.Contains("a.txt")), Is.True);
    Assert.That(files.Any(e => e.FullPath.Contains("b.txt")), Is.True);
  }

  [Test, Category("HappyPath")]
  public void ExtentMap_ReturnsEntries() {
    using var ms = new MemoryStream();
    using (var w = new CramFsWriter(ms, leaveOpen: true))
      w.AddFile("/x.bin", new byte[100]);
    ms.Position = 0;

    var desc = new CramFsFormatDescriptor();
    var extents = ((Compression.Registry.IFilesystemExtentMap)desc).EnumerateExtents(ms).ToList();
    Assert.That(extents, Has.Count.GreaterThan(0));
    Assert.That(extents.Any(e => e.Kind == Compression.Registry.DefragBlockKind.MetadataReserved), Is.True);
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
