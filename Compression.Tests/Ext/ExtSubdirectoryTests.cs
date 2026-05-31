namespace Compression.Tests.Ext;

/// <summary>
/// Subdirectory support for the ext writer. A file whose name contains a path
/// separator must be placed inside real directory inodes ("." / ".." linked,
/// link counts and used-dirs accounting maintained) rather than flattened into
/// a single root directory entry, so nested paths survive a writer→reader
/// round-trip at their original location.
/// </summary>
[TestFixture]
public class ExtSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Ext.ExtReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name, e => r.Extract(e));

    Assert.That(files.ContainsKey("readme.txt"), Is.True, "root file present");
    Assert.That(files.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(files.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");

    Assert.That(files["readme.txt"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(files["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "nested file content intact");
    Assert.That(files["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "deep nested file content intact");

    var dirs = r.Entries.Where(e => e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(dirs.Contains("docs"), Is.True, "intermediate directory 'docs' exists as a directory");
    Assert.That(dirs.Contains("docs/api"), Is.True, "intermediate directory 'docs/api' exists as a directory");
  }
}
