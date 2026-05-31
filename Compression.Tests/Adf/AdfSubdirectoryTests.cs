namespace Compression.Tests.Adf;

/// <summary>
/// Subdirectory support for the Amiga ADF writer. A file added with a name that
/// contains path separators must be placed inside a real AmigaDOS user-directory
/// tree (linked into each parent's hash table) rather than flattened into the
/// root directory. The hierarchical layout round-trips through the reader at the
/// original nested path, with the intermediate directories present as their own
/// entries.
/// </summary>
[TestFixture]
public class AdfSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var disk = w.Build();

    Assert.That(disk.Length, Is.EqualTo(901120));

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Adf.AdfReader(ms);

    var filesByPath = r.Entries.Where(e => !e.IsDirectory)
                               .ToDictionary(e => e.FullPath, e => r.Extract(e));

    Assert.That(filesByPath.ContainsKey("readme.txt"), Is.True, "root file present at its path");
    Assert.That(filesByPath.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(filesByPath.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");

    Assert.That(filesByPath["readme.txt"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(filesByPath["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "one-level nested content intact");
    Assert.That(filesByPath["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "two-level nested content intact");

    var dirPaths = r.Entries.Where(e => e.IsDirectory).Select(e => e.FullPath).ToHashSet();
    Assert.That(dirPaths.Contains("docs"), Is.True, "intermediate directory 'docs' exists");
    Assert.That(dirPaths.Contains("docs/api"), Is.True, "intermediate directory 'docs/api' exists");
  }

  [Test, Category("Spec")]
  public void SharedDirectory_CreatedOnce_NotDuplicated() {
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("docs/a.txt", "a"u8.ToArray());
    w.AddFile("docs/b.txt", "b"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Adf.AdfReader(ms);

    var docsDirs = r.Entries.Count(e => e.IsDirectory && e.FullPath == "docs");
    Assert.That(docsDirs, Is.EqualTo(1), "shared parent directory is created exactly once");

    var docsFiles = r.Entries.Count(e => !e.IsDirectory && (e.FullPath == "docs/a.txt" || e.FullPath == "docs/b.txt"));
    Assert.That(docsFiles, Is.EqualTo(2), "both files share the single 'docs' directory");
  }
}
