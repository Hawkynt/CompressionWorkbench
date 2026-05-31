using System.Text;

namespace Compression.Tests.Fat;

/// <summary>
/// Subdirectory support for the FAT writer. Files whose name contains a path
/// separator must be placed inside the corresponding directory tree rather than
/// flattened into the root — which both preserves structure and keeps the root
/// directory small (so deep trees stay on FAT12/16 instead of being forced to
/// FAT32 by root-entry overflow).
/// </summary>
[TestFixture]
public class FatSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    var byName = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.That(byName.ContainsKey("readme.txt"), Is.True, "root file present");
    Assert.That(byName.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(byName.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");
    Assert.That(byName["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "nested file content intact");
  }

  [Test, Category("Spec")]
  public void ManyFilesInSubdirs_StaysFat12_NotForcedToFat32() {
    // 300 files spread across subdirectories: each directory holds few entries,
    // so the ROOT directory never overflows and the image stays small (FAT12),
    // instead of being pushed to FAT32 the way a flat layout would force.
    var w = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < 300; i++)
      w.AddFile($"dir{i / 20:D2}/file{i:D3}.txt", Encoding.ASCII.GetBytes($"f{i}"));
    var disk = w.BuildAutoSized();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    Assert.That(r.FatType, Is.EqualTo(12), "subdir layout keeps the root small → FAT12, not FAT32");
    Assert.That(r.Entries.Count(e => !e.IsDirectory), Is.EqualTo(300), "all 300 files present");
    Assert.That(disk.Length, Is.LessThan(20 * 1024 * 1024), "tiny data → small image, no FAT32 ballooning");
  }
}
