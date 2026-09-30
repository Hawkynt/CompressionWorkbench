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
  public void Descriptor_IsDefragmentableButNotModifiable() {
    var desc = new SquashFsFormatDescriptor();
    Assert.That(desc, Is.Not.InstanceOf<Compression.Registry.IArchiveModifiable>());
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveDefragmentable>());
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IFilesystemExtentMap>());
    // SquashFS is read-only by design: images are created, not edited. Editing
    // existed only as a rebuild that lost compression, owners, modes, times,
    // symlinks and xattrs, so it is not advertised.
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanModify), Is.False);
  }

  [Test]
  public void IsNotModifiable_BecauseEveryEditWasALossyRebuild() {
    // The only edit path re-created the image: gzip instead of the original
    // compression, uid 0, fixed modes, fresh times, and no symlinks or xattrs.
    var desc = new SquashFsFormatDescriptor();
    Assert.That(desc, Is.Not.InstanceOf<Compression.Registry.IArchiveModifiable>());
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
