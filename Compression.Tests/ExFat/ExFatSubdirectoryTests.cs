namespace Compression.Tests.ExFat;

/// <summary>
/// Subdirectory support for the exFAT writer. A file whose name contains a path
/// separator must be placed inside the corresponding directory tree — written as
/// real File entry sets within a subdirectory cluster chain — rather than
/// flattened into the root directory with the slashes embedded in the name.
/// </summary>
[TestFixture]
public class ExFatSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new FileSystem.ExFat.ExFatWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.ExFat.ExFatReader(ms);

    var byName = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.That(byName.ContainsKey("readme.txt"), Is.True, "root file present at its path");
    Assert.That(byName.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(byName.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");

    Assert.That(byName["readme.txt"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(byName["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "one-level nested content intact");
    Assert.That(byName["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "two-level nested content intact");

    var dirNames = r.Entries.Where(e => e.IsDirectory)
                            .Select(e => e.Name.Replace('\\', '/'))
                            .ToHashSet();
    Assert.That(dirNames.Contains("docs"), Is.True, "intermediate directory 'docs' exists");
    Assert.That(dirNames.Contains("docs/api"), Is.True, "intermediate directory 'docs/api' exists");
  }
}
