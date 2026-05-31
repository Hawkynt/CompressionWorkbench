namespace Compression.Tests.Ufs;

[TestFixture]
public class UfsSubdirectoryTests {
  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new FileSystem.Ufs.UfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  private static FileSystem.Ufs.UfsEntry FindFile(FileSystem.Ufs.UfsReader r, string path) {
    var entry = r.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name == path);
    Assert.That(entry, Is.Not.Null, $"expected file '{path}' to round-trip at its nested path");
    return entry!;
  }

  [Test, Category("HappyPath")]
  public void NestedFiles_RoundTripAtTheirFullPaths() {
    var readme = "top-level readme"u8.ToArray();
    var guide = "the user guide"u8.ToArray();
    var reference = "the api reference"u8.ToArray();

    using var ms = new MemoryStream(BuildImage(
      ("readme.txt", readme),
      ("docs/guide.txt", guide),
      ("docs/api/reference.txt", reference)));
    var r = new FileSystem.Ufs.UfsReader(ms);

    Assert.That(r.Extract(FindFile(r, "readme.txt")), Is.EqualTo(readme));
    Assert.That(r.Extract(FindFile(r, "docs/guide.txt")), Is.EqualTo(guide));
    Assert.That(r.Extract(FindFile(r, "docs/api/reference.txt")), Is.EqualTo(reference));
  }

  [Test, Category("HappyPath")]
  public void IntermediateDirectories_ExistAsDirectoryEntries() {
    using var ms = new MemoryStream(BuildImage(
      ("docs/guide.txt", "g"u8.ToArray()),
      ("docs/api/reference.txt", "r"u8.ToArray())));
    var r = new FileSystem.Ufs.UfsReader(ms);

    var docs = r.Entries.FirstOrDefault(e => e.IsDirectory && e.Name == "docs");
    var api = r.Entries.FirstOrDefault(e => e.IsDirectory && e.Name == "docs/api");
    Assert.That(docs, Is.Not.Null, "intermediate directory 'docs' must exist as a real inode");
    Assert.That(api, Is.Not.Null, "intermediate directory 'docs/api' must exist as a real inode");
  }

  [Test, Category("HappyPath")]
  public void SharedIntermediateDirectory_IsCreatedOnce() {
    using var ms = new MemoryStream(BuildImage(
      ("docs/a.txt", "a"u8.ToArray()),
      ("docs/b.txt", "b"u8.ToArray())));
    var r = new FileSystem.Ufs.UfsReader(ms);

    var docs = r.Entries.Where(e => e.IsDirectory && e.Name == "docs").ToList();
    Assert.That(docs, Has.Count.EqualTo(1), "a shared parent directory must be created exactly once");
    Assert.That(r.Entries.Count(e => !e.IsDirectory && e.Name == "docs/a.txt"), Is.EqualTo(1));
    Assert.That(r.Entries.Count(e => !e.IsDirectory && e.Name == "docs/b.txt"), Is.EqualTo(1));
  }
}
