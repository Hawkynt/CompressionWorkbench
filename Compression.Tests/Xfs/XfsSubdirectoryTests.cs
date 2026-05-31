namespace Compression.Tests.Xfs;

/// <summary>
/// Subdirectory support for the XFS writer. A file added with a path-separated
/// name ("docs/api/reference.txt") must be placed inside real directory inodes
/// ("docs", "docs/api") rather than flattened into the root directory. Each
/// intermediate path component becomes an S_IFDIR inode holding a short-form
/// directory that references its children; the file round-trips through the
/// reader at its exact nested path with intact content.
/// </summary>
[TestFixture]
public class XfsSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new FileSystem.Xfs.XfsWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.Xfs.XfsReader(ms);

    var filesByPath = r.Entries.Where(e => !e.IsDirectory)
                               .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
    var dirPaths = r.Entries.Where(e => e.IsDirectory)
                            .Select(e => e.Name.Replace('\\', '/'))
                            .ToHashSet();

    Assert.That(filesByPath.ContainsKey("readme.txt"), Is.True, "root file present at its path");
    Assert.That(filesByPath.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(filesByPath.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");

    Assert.That(dirPaths.Contains("docs"), Is.True, "intermediate directory 'docs' exists");
    Assert.That(dirPaths.Contains("docs/api"), Is.True, "intermediate directory 'docs/api' exists");

    Assert.That(filesByPath["readme.txt"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(filesByPath["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "one-level nested file content intact");
    Assert.That(filesByPath["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "two-level nested file content intact");
  }
}
