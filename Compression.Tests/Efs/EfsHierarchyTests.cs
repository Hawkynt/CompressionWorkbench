#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Efs;

namespace Compression.Tests.Efs;

/// <summary>
/// Synthesises a small EFS image with two root files, a subdir of two files, and
/// a deeper subdir with one file (five user files total). Verifies that
/// <see cref="EfsFormatDescriptor.List"/> surfaces each entry at its real nested
/// path, and that <see cref="EfsFormatDescriptor.Extract"/> reproduces the tree
/// on disk byte-for-byte.
/// </summary>
[TestFixture]
public class EfsHierarchyTests {

  private static byte[] BuildSyntheticImage() {
    var w = new EfsWriter();
    w.AddFile("root1.txt", "root one"u8.ToArray());
    w.AddFile("root2.txt", "root two"u8.ToArray());
    w.AddFile("sub/a.txt", "sub a"u8.ToArray());
    w.AddFile("sub/b.txt", "sub b"u8.ToArray());
    w.AddFile("sub/deep/c.txt", "deep c"u8.ToArray());
    return w.Build();
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsFiveFiles_AtCorrectNestedPaths() {
    var image = BuildSyntheticImage();
    using var ms = new MemoryStream(image);
    var entries = new EfsFormatDescriptor().List(ms, null);
    var files = entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(files, Does.Contain("root1.txt"));
    Assert.That(files, Does.Contain("root2.txt"));
    Assert.That(files, Does.Contain("sub/a.txt"));
    Assert.That(files, Does.Contain("sub/b.txt"));
    Assert.That(files, Does.Contain("sub/deep/c.txt"));
  }

  [Test, Category("HappyPath")]
  public void Extract_ReproducesTreeOnDisk() {
    var image = BuildSyntheticImage();
    using var ms = new MemoryStream(image);
    var tmp = Path.Combine(Path.GetTempPath(), $"efs-hier-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      new EfsFormatDescriptor().Extract(ms, tmp, null, null);
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
    var d = new EfsFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
      Assert.That(d, Is.InstanceOf<IWipeEmpty>());
      Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    });
  }
}
