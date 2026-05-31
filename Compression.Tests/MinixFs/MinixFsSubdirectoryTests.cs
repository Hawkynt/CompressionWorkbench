namespace Compression.Tests.MinixFs;

[TestFixture]
public class MinixFsSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTrip_WithRealDirectoryInodes() {
    var readme = "top-level readme"u8.ToArray();
    var guide  = "the docs guide"u8.ToArray();
    var apiRef = "nested api reference"u8.ToArray();

    using var ms = new MemoryStream();
    var w = new FileSystem.MinixFs.MinixFsWriter(ms, leaveOpen: true);
    w.AddFile("readme.txt", readme);
    w.AddFile("docs/guide.txt", guide);
    w.AddFile("docs/api/reference.txt", apiRef);
    w.Finish();

    ms.Position = 0;
    var r = new FileSystem.MinixFs.MinixFsReader(ms);
    var byName = r.Entries.ToDictionary(e => e.Name);

    // Files must round-trip at their nested paths with intact content.
    Assert.That(byName.ContainsKey("readme.txt"), Is.True, "top-level file missing");
    Assert.That(byName.ContainsKey("docs/guide.txt"), Is.True, "docs/guide.txt missing");
    Assert.That(byName.ContainsKey("docs/api/reference.txt"), Is.True, "docs/api/reference.txt missing");

    Assert.That(byName["readme.txt"].IsDirectory, Is.False);
    Assert.That(byName["docs/guide.txt"].IsDirectory, Is.False);
    Assert.That(byName["docs/api/reference.txt"].IsDirectory, Is.False);

    Assert.That(r.Extract(byName["readme.txt"]), Is.EqualTo(readme));
    Assert.That(r.Extract(byName["docs/guide.txt"]), Is.EqualTo(guide));
    Assert.That(r.Extract(byName["docs/api/reference.txt"]), Is.EqualTo(apiRef));

    // Intermediate directories must exist as real directory inodes.
    Assert.That(byName.ContainsKey("docs"), Is.True, "intermediate dir 'docs' missing");
    Assert.That(byName["docs"].IsDirectory, Is.True, "'docs' is not a directory");
    Assert.That(byName.ContainsKey("docs/api"), Is.True, "intermediate dir 'docs/api' missing");
    Assert.That(byName["docs/api"].IsDirectory, Is.True, "'docs/api' is not a directory");
  }

  [Test, Category("RoundTrip")]
  public void SharedIntermediateDirectory_IsCreatedOnce() {
    using var ms = new MemoryStream();
    var w = new FileSystem.MinixFs.MinixFsWriter(ms, leaveOpen: true);
    w.AddFile("docs/a.txt", "a"u8.ToArray());
    w.AddFile("docs/b.txt", "b"u8.ToArray());
    w.Finish();

    ms.Position = 0;
    var r = new FileSystem.MinixFs.MinixFsReader(ms);
    var docs = r.Entries.Where(e => e.Name == "docs" && e.IsDirectory).ToList();
    Assert.That(docs, Has.Count.EqualTo(1), "'docs' should be created exactly once");

    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(r.Extract(byName["docs/a.txt"]), Is.EqualTo("a"u8.ToArray()));
    Assert.That(r.Extract(byName["docs/b.txt"]), Is.EqualTo("b"u8.ToArray()));
  }
}
