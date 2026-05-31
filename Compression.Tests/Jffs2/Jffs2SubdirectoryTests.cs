using System.Text;

namespace Compression.Tests.Jffs2;

/// <summary>
/// Subdirectory support for the JFFS2 writer. A file added with a path that
/// contains separators (e.g. "docs/api/reference.txt") must be placed inside a
/// real directory tree — each intermediate path segment becoming its own
/// directory inode plus a dirent in its parent — rather than flattened into the
/// root with the slashes baked into a single name. The reader must then walk the
/// parent-inode (pino) chain to reassemble the full nested path.
/// </summary>
[TestFixture]
public class Jffs2SubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new FileSystem.Jffs2.Jffs2Writer();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var image = w.Build();

    var r = new FileSystem.Jffs2.Jffs2FileReader(image);
    var byName = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), r.Extract);

    Assert.That(byName.ContainsKey("readme.txt"), Is.True, "root file present at its path");
    Assert.That(byName.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(byName.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");

    Assert.That(byName["readme.txt"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(byName["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "one-level file content intact");
    Assert.That(byName["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "deep file content intact");
  }

  [Test, Category("Spec")]
  public void IntermediateDirectories_ExistAsRealDirectoryInodes() {
    var w = new FileSystem.Jffs2.Jffs2Writer();
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var image = w.Build();

    var r = new FileSystem.Jffs2.Jffs2FileReader(image);
    var dirNames = r.Entries.Where(e => e.IsDirectory)
                            .Select(e => e.Name.Replace('\\', '/'))
                            .ToHashSet();

    Assert.That(dirNames, Does.Contain("docs"), "intermediate directory 'docs' exists");
    Assert.That(dirNames, Does.Contain("docs/api"), "intermediate directory 'docs/api' exists");
  }

  [Test, Category("Spec")]
  public void SharedParentDirectory_IsCreatedOnce() {
    // Two files under the same directory must reuse a single directory inode,
    // not allocate a fresh "docs" directory per file.
    var w = new FileSystem.Jffs2.Jffs2Writer();
    w.AddFile("docs/a.txt", "a"u8.ToArray());
    w.AddFile("docs/b.txt", "b"u8.ToArray());
    var image = w.Build();

    var r = new FileSystem.Jffs2.Jffs2FileReader(image);
    var docsDirs = r.Entries.Where(e => e.IsDirectory && e.Name.Replace('\\', '/') == "docs").ToList();
    Assert.That(docsDirs.Count, Is.EqualTo(1), "the shared 'docs' directory is created exactly once");

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .Select(e => e.Name.Replace('\\', '/'))
                         .ToHashSet();
    Assert.That(files, Does.Contain("docs/a.txt"));
    Assert.That(files, Does.Contain("docs/b.txt"));
  }
}
