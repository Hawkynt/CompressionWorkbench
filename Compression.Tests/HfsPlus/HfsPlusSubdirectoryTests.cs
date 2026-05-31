using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Subdirectory support for the HFS+ writer. A file added with a slash-separated
/// name must be stored inside the corresponding catalog folder hierarchy — with
/// real folder records and folder thread records for each intermediate level —
/// rather than flattened into the root folder under its whole path string.
/// </summary>
[TestFixture]
public class HfsPlusSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new HfsPlusWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new HfsPlusReader(ms);

    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.FullPath, e => r.Extract(e));

    Assert.That(byPath.ContainsKey("readme.txt"), Is.True, "root file present");
    Assert.That(byPath.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file at its path");
    Assert.That(byPath.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file at its path");

    Assert.That(byPath["readme.txt"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(byPath["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "one-level file content intact");
    Assert.That(byPath["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "deep file content intact");

    var dirs = r.Entries.Where(e => e.IsDirectory).Select(e => e.FullPath).ToHashSet();
    Assert.That(dirs, Does.Contain("docs"), "intermediate folder 'docs' exists");
    Assert.That(dirs, Does.Contain("docs/api"), "intermediate folder 'docs/api' exists");
  }
}
