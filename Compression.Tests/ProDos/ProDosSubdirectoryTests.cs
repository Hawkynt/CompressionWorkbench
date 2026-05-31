using FileSystem.ProDos;

namespace Compression.Tests.ProDos;

/// <summary>
/// Subdirectory support for the ProDOS writer. ProDOS is hierarchical: a file
/// added under a path such as "DOCS/GUIDE" must be placed inside a real
/// subdirectory "DOCS" (storage type 0xD with a 0xE subdirectory header) rather
/// than flattened into the volume directory. Nested files must round-trip through
/// the reader at their full nested path, with every intermediate subdirectory present.
/// </summary>
[TestFixture]
public class ProDosSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var rootData = "ROOT FILE CONTENT"u8.ToArray();
    var guideData = "GUIDE IN DOCS"u8.ToArray();
    var refData = "DEEP API REFERENCE"u8.ToArray();

    var w = new ProDosWriter();
    w.AddFile("README", rootData);
    w.AddFile("DOCS/GUIDE", guideData);
    w.AddFile("DOCS/API/REFERENCE", refData);
    var img = w.Build();

    using var r = new ProDosReader(new MemoryStream(img));

    var byPath = r.Entries
      .Where(e => !e.IsDirectory)
      .ToDictionary(e => e.FullPath, e => r.Extract(e));

    Assert.That(byPath.ContainsKey("README"), Is.True, "root file present at its path");
    Assert.That(byPath.ContainsKey("DOCS/GUIDE"), Is.True, "one-level nested file present at its path");
    Assert.That(byPath.ContainsKey("DOCS/API/REFERENCE"), Is.True, "two-level nested file present at its path");

    Assert.That(byPath["README"], Is.EqualTo(rootData), "root file content intact");
    Assert.That(byPath["DOCS/GUIDE"], Is.EqualTo(guideData), "one-level nested file content intact");
    Assert.That(byPath["DOCS/API/REFERENCE"], Is.EqualTo(refData), "two-level nested file content intact");
  }

  [Test, Category("RoundTrip")]
  public void IntermediateSubdirectories_ExistAsDirectories() {
    var w = new ProDosWriter();
    w.AddFile("README", "x"u8.ToArray());
    w.AddFile("DOCS/GUIDE", "y"u8.ToArray());
    w.AddFile("DOCS/API/REFERENCE", "z"u8.ToArray());
    var img = w.Build();

    using var r = new ProDosReader(new MemoryStream(img));

    var dirPaths = r.Entries.Where(e => e.IsDirectory).Select(e => e.FullPath).ToHashSet();
    Assert.That(dirPaths.Contains("DOCS"), Is.True, "first-level subdirectory exists");
    Assert.That(dirPaths.Contains("DOCS/API"), Is.True, "second-level subdirectory exists");

    var docs = r.Entries.Single(e => e.FullPath == "DOCS");
    Assert.That(docs.StorageType, Is.EqualTo(0x0D), "subdirectory uses storage type 0xD");
  }
}
