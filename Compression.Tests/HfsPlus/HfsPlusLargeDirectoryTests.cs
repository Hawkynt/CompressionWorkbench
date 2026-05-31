using System.Text;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Large-directory support for the HFS+ writer. A single folder holding far more
/// catalog records than fit in one 4&#160;KB B-tree leaf node must spill into
/// multiple leaf nodes joined by an index node, and every file must round-trip
/// through <see cref="HfsPlusReader"/> at its correct nested path with content
/// intact.
/// </summary>
[TestFixture]
public class HfsPlusLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void ThousandFilesInOneFolder_RoundTripThroughReader() {
    const int FileCount = 1000;
    var writer = new HfsPlusWriter();
    for (var i = 0; i < FileCount; i++)
      writer.AddFile($"bulk/file{i:D4}.dat", Encoding.ASCII.GetBytes($"content-{i}"));

    var image = writer.Build();

    using var ms = new MemoryStream(image);
    var reader = new HfsPlusReader(ms);

    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    var byPath = files.ToDictionary(e => e.FullPath, e => e);

    for (var i = 0; i < FileCount; i++) {
      var path = $"bulk/file{i:D4}.dat";
      Assert.That(byPath.ContainsKey(path), Is.True, $"file present at '{path}'");
    }

    // Spot-check content for a sampling of files spread across the directory.
    foreach (var i in new[] { 0, 1, 137, 499, 500, 753, 998, 999 }) {
      var path = $"bulk/file{i:D4}.dat";
      var data = reader.Extract(byPath[path]);
      Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo($"content-{i}"),
        $"content intact for '{path}'");
    }

    var dirs = reader.Entries.Where(e => e.IsDirectory).Select(e => e.FullPath).ToHashSet();
    Assert.That(dirs, Does.Contain("bulk"), "parent folder present");
  }
}
