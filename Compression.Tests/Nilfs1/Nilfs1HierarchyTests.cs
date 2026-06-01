#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Nilfs1;

namespace Compression.Tests.Nilfs1;

/// <summary>
/// Hierarchy round-trip tests for NILFS v1. The writer encodes subdirectories
/// as path-prefixed names in its compact directory index; the reader returns
/// them flat with prefixes.
/// </summary>
[TestFixture]
public class Nilfs1HierarchyTests {

  private static byte[] BuildHierarchyImage() {
    var w = new Nilfs1Writer();
    w.AddFile("root1.txt", Encoding.UTF8.GetBytes("Root file 1"));
    w.AddFile("root2.txt", Encoding.UTF8.GetBytes("Root file 2"));
    w.AddFile("subdir/a.txt", Encoding.UTF8.GetBytes("Subdir file A"));
    w.AddFile("subdir/b.txt", Encoding.UTF8.GetBytes("Subdir file B"));
    w.AddFile("subdir/deeper/leaf.txt", Encoding.UTF8.GetBytes("Deepest"));
    return w.Build();
  }

  [Test]
  public void List_ReturnsFiveFilesWithSubdirPrefixedNames() {
    var d = new Nilfs1FormatDescriptor();
    using var ms = new MemoryStream(BuildHierarchyImage());
    var entries = d.List(ms, null).Where(e => !e.IsDirectory).ToList();
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("root1.txt"));
    Assert.That(names, Does.Contain("root2.txt"));
    Assert.That(names, Does.Contain("subdir/a.txt"));
    Assert.That(names, Does.Contain("subdir/b.txt"));
    Assert.That(names, Does.Contain("subdir/deeper/leaf.txt"));
  }

  [Test]
  public void Extract_ReproducesDirectoryTreeOnDisk() {
    var d = new Nilfs1FormatDescriptor();
    using var ms = new MemoryStream(BuildHierarchyImage());
    var tmp = Path.Combine(Path.GetTempPath(), $"nilfs1-hier-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      d.Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "root1.txt")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "subdir", "a.txt")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "subdir", "deeper", "leaf.txt")), Is.True);
      Assert.That(File.ReadAllText(Path.Combine(tmp, "subdir", "a.txt")), Is.EqualTo("Subdir file A"));
      Assert.That(File.ReadAllText(Path.Combine(tmp, "subdir", "deeper", "leaf.txt")), Is.EqualTo("Deepest"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test]
  public void SuperblockIsValidNilfsV1() {
    var img = BuildHierarchyImage();
    using var r = new Nilfs1Reader(new MemoryStream(img));
    Assert.That(r.ValidSuperblock, Is.True);
    Assert.That(r.RevLevel, Is.EqualTo(1u));
    Assert.That(r.Magic, Is.EqualTo((ushort)0x3434));
  }
}
