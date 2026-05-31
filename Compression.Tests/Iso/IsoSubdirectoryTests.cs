using System.Text;

namespace Compression.Tests.Iso;

/// <summary>
/// Subdirectory support for the ISO 9660 writer. A file added with a path that
/// contains '/' separators must be placed inside a real directory-record tree —
/// each path segment becoming its own directory extent with "." and ".." records
/// and a matching path-table entry — rather than being flattened into a single
/// root directory record whose name carries the embedded slashes.
/// </summary>
[TestFixture]
public class IsoSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Iso.IsoReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name.ToUpperInvariant(), e => r.Extract(e));
    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.ToUpperInvariant())
                        .ToHashSet();

    Assert.That(files.ContainsKey("README.TXT"), Is.True, "root file present at its path");
    Assert.That(files.ContainsKey("DOCS/GUIDE.TXT"), Is.True, "one-level nested file present at its nested path");
    Assert.That(files.ContainsKey("DOCS/API/REFERENCE.TXT"), Is.True, "two-level nested file present at its nested path");

    Assert.That(dirs.Contains("DOCS"), Is.True, "intermediate directory 'docs' exists");
    Assert.That(dirs.Contains("DOCS/API"), Is.True, "intermediate directory 'docs/api' exists");

    Assert.That(files["README.TXT"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(files["DOCS/GUIDE.TXT"], Is.EqualTo("in docs"u8.ToArray()), "one-level nested file content intact");
    Assert.That(files["DOCS/API/REFERENCE.TXT"], Is.EqualTo("deep file"u8.ToArray()), "two-level nested file content intact");
  }
}
