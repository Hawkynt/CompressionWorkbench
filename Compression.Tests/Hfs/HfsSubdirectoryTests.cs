namespace Compression.Tests.Hfs;

/// <summary>
/// Subdirectory support for the Classic HFS writer. A file added with a name that
/// contains path separators must land inside a real catalog folder hierarchy
/// (directory record + directory thread per level) rather than being flattened
/// into the volume root, and the reader must reconstruct each file at its exact
/// nested path by walking the catalog parent-dirID chain.
/// </summary>
[TestFixture]
public class HfsSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new FileSystem.Hfs.HfsWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Hfs.HfsReader(ms);
    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.That(byPath.ContainsKey("readme.txt"), Is.True, "root file present at its path");
    Assert.That(byPath.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(byPath.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");

    Assert.That(byPath["readme.txt"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(byPath["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "one-level nested file content intact");
    Assert.That(byPath["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "two-level nested file content intact");

    var dirPaths = r.Entries.Where(e => e.IsDirectory)
                            .Select(e => e.Name.Replace('\\', '/'))
                            .ToHashSet();
    Assert.That(dirPaths.Contains("docs"), Is.True, "intermediate folder 'docs' exists as a directory entry");
    Assert.That(dirPaths.Contains("docs/api"), Is.True, "intermediate folder 'docs/api' exists as a directory entry");
  }
}
