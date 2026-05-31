using System.Text;

namespace Compression.Tests.Bfs;

/// <summary>
/// A single directory holding far more entries than fit in one B+ tree leaf
/// (1024-byte node) must round-trip: the writer spills the entries across
/// multiple linked leaf nodes, and the reader follows the leaf chain to surface
/// every child. With ~1000 short-named files the contents span many leaves.
/// </summary>
[TestFixture]
public class BfsLargeDirectoryTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ManyFilesInOneDirectory_AllRoundTrip() {
    const int count = 1000;

    var w = new FileSystem.Bfs.BfsWriter();
    for (var i = 0; i < count; i++)
      w.AddFile($"big/file{i:D4}.txt", Encoding.ASCII.GetBytes($"content-{i}"));
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Bfs.BfsReader(ms);

    var byName = r.Entries
      .Where(e => !e.IsDirectory)
      .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.That(byName.Count, Is.EqualTo(count),
      $"all {count} files in the one directory should be present");

    // Spot-check a handful spread across the directory.
    foreach (var i in new[] { 0, 1, 123, 500, 777, count - 1 }) {
      var path = $"big/file{i:D4}.txt";
      Assert.That(byName.ContainsKey(path), Is.True, $"{path} should be present");
      Assert.That(byName[path], Is.EqualTo(Encoding.ASCII.GetBytes($"content-{i}")),
        $"{path} content should be intact");
    }
  }
}
