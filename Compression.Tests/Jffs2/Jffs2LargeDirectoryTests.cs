using System.Text;

namespace Compression.Tests.Jffs2;

/// <summary>
/// Large-directory support for the JFFS2 writer. JFFS2 is log-structured: each
/// directory entry is its own dirent node referencing its parent inode, so a
/// directory has no inherent entry cap — capacity is bounded only by erase-block
/// space. Many files placed in one directory must all round-trip at their correct
/// nested paths with content intact, which exercises that the scanner reads every
/// dirent node and reassembles the parent chain for all of them.
/// </summary>
[TestFixture]
public class Jffs2LargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void ThousandFilesInOneDirectory_AllRoundTrip() {
    const int count = 1000;

    var w = new FileSystem.Jffs2.Jffs2Writer();
    for (var i = 0; i < count; i++)
      w.AddFile($"big/f{i:D4}.txt", Encoding.ASCII.GetBytes($"content-{i}"));
    var image = w.Build();

    var r = new FileSystem.Jffs2.Jffs2FileReader(image);
    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), r.Extract);

    Assert.That(byPath.Count, Is.EqualTo(count),
      $"all {count} dirent nodes recovered by the scanner");

    for (var i = 0; i < count; i++) {
      var path = $"big/f{i:D4}.txt";
      Assert.That(byPath.ContainsKey(path), Is.True, $"{path} present");
    }

    // Spot-check several full contents across the range.
    foreach (var i in new[] { 0, 1, 333, 499, 500, 998, 999 })
      Assert.That(byPath[$"big/f{i:D4}.txt"],
        Is.EqualTo(Encoding.ASCII.GetBytes($"content-{i}")),
        $"content of f{i:D4}.txt intact");
  }
}
