namespace Compression.Tests.Ntfs;

/// <summary>
/// Subdirectory support for the NTFS writer. A file added with a path that
/// contains '/' separators must be placed inside real directory MFT records
/// (each carrying its own $I30 index) rather than flattened into the root
/// directory under its whole slashed name. The reader must then surface each
/// file at its exact nested path with intact content.
/// </summary>
[TestFixture]
public class NtfsSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Ntfs.NtfsReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.Multiple(() => {
      Assert.That(files.ContainsKey("readme.txt"), Is.True, "root file present at its path");
      Assert.That(files.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
      Assert.That(files.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");
    });

    Assert.That(files["readme.txt"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(files["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "one-level nested content intact");
    Assert.That(files["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "two-level nested content intact");

    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.Replace('\\', '/'))
                        .ToHashSet();
    Assert.Multiple(() => {
      Assert.That(dirs.Contains("docs"), Is.True, "intermediate directory 'docs' exists as a real directory record");
      Assert.That(dirs.Contains("docs/api"), Is.True, "intermediate directory 'docs/api' exists as a real directory record");
    });
  }
}
