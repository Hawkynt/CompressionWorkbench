namespace Compression.Tests.Jfs;

/// <summary>
/// Subdirectory support for the JFS writer. A file added with a path that
/// contains separators (e.g. <c>docs/api/reference.txt</c>) must be placed
/// inside real directory inodes for each path component rather than flattened
/// into the root directory. The reader walks the dtree recursively, so a nested
/// file must round-trip at its exact path with the intermediate directories
/// materialised as directory inodes.
/// </summary>
[TestFixture]
public class JfsSubdirectoryTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new FileSystem.Jfs.JfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var img = BuildImage(
      ("readme.txt", "root file"u8.ToArray()),
      ("docs/guide.txt", "in docs"u8.ToArray()),
      ("docs/api/reference.txt", "deep file"u8.ToArray()));

    using var ms = new MemoryStream(img);
    var r = new FileSystem.Jfs.JfsReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.Replace('\\', '/'))
                        .ToHashSet();

    Assert.That(files.ContainsKey("readme.txt"), Is.True, "root file present at its path");
    Assert.That(files.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(files.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");

    Assert.That(files["readme.txt"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(files["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "one-level nested content intact");
    Assert.That(files["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "two-level nested content intact");

    Assert.That(dirs.Contains("docs"), Is.True, "intermediate directory 'docs' materialised");
    Assert.That(dirs.Contains("docs/api"), Is.True, "intermediate directory 'docs/api' materialised");
  }
}
