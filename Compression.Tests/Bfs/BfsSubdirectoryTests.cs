namespace Compression.Tests.Bfs;

/// <summary>
/// Subdirectory support for the BFS writer. A file added with a path-separated
/// name (e.g. "docs/api/reference.txt") must be stored inside real directory
/// inodes — one per path segment — each carrying its own B+ tree of children,
/// rather than being flattened into the root directory. The reader must recurse
/// those directory inodes and surface each file at its full nested path.
/// </summary>
[TestFixture]
public class BfsSubdirectoryTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new FileSystem.Bfs.BfsWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Bfs.BfsReader(ms);

    var byName = r.Entries
      .Where(e => !e.IsDirectory)
      .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.That(byName.ContainsKey("readme.txt"), Is.True, "root file present at its path");
    Assert.That(byName.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(byName.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");

    Assert.That(byName["readme.txt"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(byName["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "one-level nested file content intact");
    Assert.That(byName["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "two-level nested file content intact");
  }

  [Test, Category("HappyPath")]
  public void IntermediateDirectories_AreRealDirectoryInodes() {
    var w = new FileSystem.Bfs.BfsWriter();
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Bfs.BfsReader(ms);

    var dirs = r.Entries
      .Where(e => e.IsDirectory)
      .Select(e => e.Name.Replace('\\', '/'))
      .ToHashSet();

    Assert.That(dirs, Does.Contain("docs"), "first-level directory inode exists");
    Assert.That(dirs, Does.Contain("docs/api"), "second-level directory inode exists");
  }
}
