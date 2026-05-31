using System.Text;
using FileSystem.Hpfs;

namespace Compression.Tests.Hpfs;

/// <summary>
/// A single directory with far more entries than fit in one 2 KiB dirent block
/// must round-trip: the writer spills the dirents across multiple blocks linked
/// through the B-tree (down-pointers), and the reader follows those links to
/// surface every child. With ~1000 short-named files the contents span many
/// dirent blocks.
/// </summary>
[TestFixture]
public class HpfsLargeDirectoryTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ManyFilesInOneDirectory_AllRoundTrip() {
    const int count = 1000;

    var w = new HpfsWriter();
    for (var i = 0; i < count; i++)
      w.AddFile($"big/file{i:D4}.txt", Encoding.ASCII.GetBytes($"content-{i}"));
    var image = w.Build();

    using var r = new HpfsReader(new MemoryStream(image));

    var byPath = r.Entries
      .Where(e => !e.IsDirectory)
      .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.That(byPath.Count, Is.EqualTo(count),
      $"all {count} files in the one directory should be present");

    foreach (var i in new[] { 0, 1, 123, 500, 777, count - 1 }) {
      var path = $"big/file{i:D4}.txt";
      Assert.That(byPath.ContainsKey(path), Is.True, $"{path} should be present");
      Assert.That(byPath[path], Is.EqualTo(Encoding.ASCII.GetBytes($"content-{i}")),
        $"{path} content should be intact");
    }
  }
}
