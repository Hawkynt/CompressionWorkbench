using System.Text;

namespace Compression.Tests.Jfs;

/// <summary>
/// Large-directory support for the JFS writer. A single directory holding far
/// more entries than fit in the dinode's inline dtroot (≤ 8 slots) must spill
/// its entries into external directory B+tree leaf pages, with the inline
/// dtroot promoted to a router node addressing those pages. Every file must
/// round-trip through <see cref="FileSystem.Jfs.JfsReader"/> at its exact path
/// with content intact, proving the reader follows the router's leaf pointers
/// and reassembles names from the leaf slots.
/// </summary>
[TestFixture]
public class JfsLargeDirectoryTests {

  private static byte[] BuildImage(IEnumerable<(string Name, byte[] Data)> files) {
    var w = new FileSystem.Jfs.JfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test, Category("RoundTrip")]
  public void DirectoryWithManyFiles_SpillsToExternalDtree_AndRoundTrips() {
    const int Count = 1000;

    var inputs = new List<(string Name, byte[] Data)>(Count);
    for (var i = 0; i < Count; i++) {
      var name = $"dir/file{i:D4}";
      var content = Encoding.UTF8.GetBytes($"content-{i}");
      inputs.Add((name, content));
    }

    var img = BuildImage(inputs);

    using var ms = new MemoryStream(img);
    var r = new FileSystem.Jfs.JfsReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.Replace('\\', '/'))
                        .ToHashSet();

    Assert.That(dirs.Contains("dir"), Is.True, "the large directory itself is present");
    Assert.That(files.Count, Is.EqualTo(Count), "every file in the large directory is present");

    // Every entry present at its exact path.
    for (var i = 0; i < Count; i++) {
      var path = $"dir/file{i:D4}";
      Assert.That(files.ContainsKey(path), Is.True, $"file present at path {path}");
    }

    // Spot-check content at the boundaries and a few interior points.
    foreach (var i in new[] { 0, 1, 7, 8, 9, 255, 256, 511, 512, 999 }) {
      var path = $"dir/file{i:D4}";
      Assert.That(files[path], Is.EqualTo(Encoding.UTF8.GetBytes($"content-{i}")),
        $"content intact for {path}");
    }
  }

  // The inline dtroot holds 8 entries; the 9th must trigger the external dtree.
  // Both sides of the boundary must round-trip every entry with content intact.
  [TestCase(8, TestName = "InlineDtroot_AtCapacity_RoundTrips")]
  [TestCase(9, TestName = "FirstSpillToExternalDtree_RoundTrips")]
  [TestCase(124, TestName = "JustOverOneLeafPage_RoundTrips")]
  [Category("RoundTrip")]
  public void DirectoryAroundInlineBoundary_RoundTrips(int count) {
    var inputs = new List<(string Name, byte[] Data)>(count);
    for (var i = 0; i < count; i++)
      inputs.Add(($"d/f{i:D4}", Encoding.UTF8.GetBytes($"v{i}")));

    var img = BuildImage(inputs);
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Jfs.JfsReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.That(files.Count, Is.EqualTo(count), $"all {count} files present");
    for (var i = 0; i < count; i++) {
      var path = $"d/f{i:D4}";
      Assert.That(files.ContainsKey(path), Is.True, $"present: {path}");
      Assert.That(files[path], Is.EqualTo(Encoding.UTF8.GetBytes($"v{i}")), $"content: {path}");
    }
  }
}
