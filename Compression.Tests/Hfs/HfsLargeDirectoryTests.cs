namespace Compression.Tests.Hfs;

/// <summary>
/// Large-directory support for the Classic HFS writer. When a single directory
/// holds more catalog records than fit in one 512-byte B*-tree leaf node, the
/// writer must grow the catalog into multiple leaf nodes (chained via the node
/// descriptor fLink/bLink) anchored by index node(s), and the reader must walk
/// the whole leaf chain so every entry round-trips at its correct path with its
/// content intact.
/// </summary>
[TestFixture]
public class HfsLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void ManyFilesInOneDirectory_RoundTripThroughReader() {
    const int fileCount = 1000;
    var w = new FileSystem.Hfs.HfsWriter();

    var expected = new Dictionary<string, byte[]>();
    for (var i = 0; i < fileCount; i++) {
      var path = $"big/f{i:D4}.txt";
      var content = System.Text.Encoding.ASCII.GetBytes($"content-{i}");
      w.AddFile(path, content);
      expected[path] = content;
    }

    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Hfs.HfsReader(ms);
    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => e);

    Assert.That(byPath.Count, Is.EqualTo(fileCount), "every file present after round-trip");

    foreach (var (path, content) in expected) {
      Assert.That(byPath.ContainsKey(path), Is.True, $"file {path} present at its path");
      Assert.That(r.Extract(byPath[path]), Is.EqualTo(content), $"file {path} content intact");
    }

    var dirPaths = r.Entries.Where(e => e.IsDirectory)
                            .Select(e => e.Name.Replace('\\', '/'))
                            .ToHashSet();
    Assert.That(dirPaths.Contains("big"), Is.True, "containing folder present as directory entry");
  }
}
