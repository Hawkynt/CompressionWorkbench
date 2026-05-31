using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// Subdirectory support for the APFS writer. Files whose name contains a path
/// separator must live inside real directory inodes (S_IFDIR) chained by
/// DIR_REC records, rather than being flattened into the root directory.
/// A file added as "a/b/c.txt" must round-trip through the reader at that
/// exact nested path, with intermediate directory inodes for "a" and "a/b".
/// </summary>
[TestFixture]
public class ApfsSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var rootData = "root file"u8.ToArray();
    var guideData = "in docs"u8.ToArray();
    var refData = "deep file"u8.ToArray();

    var w = new ApfsWriter();
    w.SetMinImageSize(4 * 1024 * 1024);
    w.AddFile("readme.txt", rootData);
    w.AddFile("docs/guide.txt", guideData);
    w.AddFile("docs/api/reference.txt", refData);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new ApfsReader(ms);

    var filesByPath = r.Entries.Where(e => !e.IsDirectory)
                               .ToDictionary(e => e.Name.Replace('\\', '/'), e => e);

    Assert.That(filesByPath.ContainsKey("readme.txt"), Is.True, "root file present at its path");
    Assert.That(filesByPath.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(filesByPath.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");

    Assert.That(r.Extract(filesByPath["readme.txt"]), Is.EqualTo(rootData), "root file content intact");
    Assert.That(r.Extract(filesByPath["docs/guide.txt"]), Is.EqualTo(guideData), "one-level nested content intact");
    Assert.That(r.Extract(filesByPath["docs/api/reference.txt"]), Is.EqualTo(refData), "two-level nested content intact");

    var dirsByPath = r.Entries.Where(e => e.IsDirectory)
                              .Select(e => e.Name.Replace('\\', '/'))
                              .ToHashSet();
    Assert.That(dirsByPath.Contains("docs"), Is.True, "intermediate directory 'docs' exists as a real directory inode");
    Assert.That(dirsByPath.Contains("docs/api"), Is.True, "intermediate directory 'docs/api' exists as a real directory inode");
  }
}
