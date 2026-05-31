using System.Text;
using FileSystem.F2fs;

namespace Compression.Tests.F2fs;

/// <summary>
/// Subdirectory support for the F2FS writer. A file added with a slash-separated
/// name must be placed inside real directory inodes (one per path component) rather
/// than flattened into the root with the whole path as a single dentry name. The
/// reader already walks child directories, so a nested file must round-trip at its
/// exact nested path with intact content and with the intermediate directories
/// present as directory entries.
/// </summary>
[TestFixture]
public class F2fsSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new F2fsWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var img = w.Build();

    using var ms = new MemoryStream(img);
    var r = new F2fsReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.Replace('\\', '/'))
                        .ToHashSet();

    Assert.That(files.ContainsKey("readme.txt"), Is.True, "root file present at its path");
    Assert.That(files.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(files.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");

    Assert.That(files["readme.txt"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(files["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "nested file content intact");
    Assert.That(files["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "deep file content intact");

    Assert.That(dirs.Contains("docs"), Is.True, "intermediate directory 'docs' exists");
    Assert.That(dirs.Contains("docs/api"), Is.True, "intermediate directory 'docs/api' exists");
  }
}
