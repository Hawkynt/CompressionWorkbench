#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Jfs1;

namespace Compression.Tests.Jfs1;

[TestFixture]
public class Jfs1HierarchyTests {

  private static byte[] BuildSyntheticImage() {
    var w = new Jfs1Writer();
    w.AddFile("root1.txt", "root one"u8.ToArray());
    w.AddFile("root2.txt", "root two"u8.ToArray());
    w.AddFile("sub/a.txt", "sub a"u8.ToArray());
    w.AddFile("sub/b.txt", "sub b"u8.ToArray());
    w.AddFile("sub/deep/c.txt", "deep c"u8.ToArray());
    return w.Build();
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsFiveFiles_AtCorrectNestedPaths() {
    var img = BuildSyntheticImage();
    using var ms = new MemoryStream(img);
    var entries = new Jfs1FormatDescriptor().List(ms, null);
    var files = entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(files, Does.Contain("root1.txt"));
    Assert.That(files, Does.Contain("root2.txt"));
    Assert.That(files, Does.Contain("sub/a.txt"));
    Assert.That(files, Does.Contain("sub/b.txt"));
    Assert.That(files, Does.Contain("sub/deep/c.txt"));
  }

  [Test, Category("HappyPath")]
  public void Extract_ReproducesTreeOnDisk() {
    var img = BuildSyntheticImage();
    using var ms = new MemoryStream(img);
    var tmp = Path.Combine(Path.GetTempPath(), $"jfs1-hier-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      new Jfs1FormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "root1.txt")), Is.EqualTo("root one"u8.ToArray()));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "root2.txt")), Is.EqualTo("root two"u8.ToArray()));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "sub", "a.txt")), Is.EqualTo("sub a"u8.ToArray()));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "sub", "b.txt")), Is.EqualTo("sub b"u8.ToArray()));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "sub", "deep", "c.txt")), Is.EqualTo("deep c"u8.ToArray()));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesNewCapabilities() {
    var d = new Jfs1FormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
      Assert.That(d, Is.InstanceOf<IWipeEmpty>());
      Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    });
  }
}
