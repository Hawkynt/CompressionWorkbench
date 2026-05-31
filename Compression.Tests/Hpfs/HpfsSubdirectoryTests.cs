using System.Text;
using FileSystem.Hpfs;

namespace Compression.Tests.Hpfs;

/// <summary>
/// Behaviour: a file added under a nested path round-trips through the HPFS
/// writer and reader at that exact path, with real intermediate directories.
/// </summary>
[TestFixture]
public class HpfsSubdirectoryTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void NestedFiles_RoundTripAtTheirPaths() {
    // Given a writer with files at the root and inside one- and two-level dirs.
    var readme = "root readme"u8.ToArray();
    var guide = "the docs guide"u8.ToArray();
    var reference = "the api reference"u8.ToArray();

    var w = new HpfsWriter();
    w.AddFile("readme.txt", readme);
    w.AddFile("docs/guide.txt", guide);
    w.AddFile("docs/api/reference.txt", reference);

    // When the image is built and read back.
    var image = w.Build();
    using var r = new HpfsReader(new MemoryStream(image));

    var byPath = r.Entries
      .Where(e => !e.IsDirectory)
      .ToDictionary(e => e.Name.Replace('\\', '/'));

    // Then every file appears at its full nested path with intact content.
    Assert.That(byPath.ContainsKey("readme.txt"), Is.True,
      "root file should round-trip at the root");
    Assert.That(byPath.ContainsKey("docs/guide.txt"), Is.True,
      "one-level file should round-trip under docs/");
    Assert.That(byPath.ContainsKey("docs/api/reference.txt"), Is.True,
      "two-level file should round-trip under docs/api/");

    Assert.That(r.Extract(byPath["readme.txt"]), Is.EqualTo(readme));
    Assert.That(r.Extract(byPath["docs/guide.txt"]), Is.EqualTo(guide));
    Assert.That(r.Extract(byPath["docs/api/reference.txt"]), Is.EqualTo(reference));
  }

  [Test, Category("HappyPath")]
  public void IntermediateDirectories_AppearAsDirectoryEntries() {
    var w = new HpfsWriter();
    w.AddFile("docs/api/reference.txt", "x"u8.ToArray());
    var image = w.Build();

    using var r = new HpfsReader(new MemoryStream(image));

    var dirs = r.Entries
      .Where(e => e.IsDirectory)
      .Select(e => e.Name.Replace('\\', '/'))
      .ToHashSet();

    Assert.That(dirs, Does.Contain("docs"), "the 'docs' directory should exist");
    Assert.That(dirs, Does.Contain("docs/api"), "the 'docs/api' directory should exist");
  }
}
