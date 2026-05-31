using System.Linq;
using System.Text;

namespace Compression.Tests.Zfs;

/// <summary>
/// Large-directory support for the ZFS writer. A single directory holding far more entries
/// than fit in a micro-ZAP (a flat array in one 512-byte block) must spill into a fat ZAP —
/// a <c>zap_phys_t</c> header block carrying an embedded pointer table plus
/// <c>zap_leaf_phys_t</c> leaf blocks that chunk the name/value entries and chain them by the
/// salted ZAP hash. Every entry must round-trip through <see cref="FileSystem.Zfs.ZfsReader"/>
/// at its exact path with content intact.
/// </summary>
[TestFixture]
public class ZfsLargeDirectoryTests {

  private static byte[] BuildImage(IEnumerable<(string Name, byte[] Data)> files) {
    var w = new FileSystem.Zfs.ZfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  private static byte[] ContentFor(int i) => Encoding.UTF8.GetBytes($"content-of-file-{i:D4}");

  [Test, Category("RoundTrip")]
  public void ManyFilesInOneDirectory_RoundTripThroughReader() {
    const int count = 1000; // far beyond the micro-ZAP capacity of a single 512-byte block

    var files = Enumerable.Range(0, count)
      .Select(i => ($"dir/file{i:D4}", ContentFor(i)))
      .ToArray();

    var img = BuildImage(files);

    using var ms = new MemoryStream(img);
    using var r = new FileSystem.Zfs.ZfsReader(ms);

    var byName = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => e);

    Assert.That(byName.Count, Is.EqualTo(count),
      "every file in the large directory is present exactly once");

    for (var i = 0; i < count; i++) {
      var path = $"dir/file{i:D4}";
      Assert.That(byName.ContainsKey(path), Is.True, $"file present at '{path}'");
    }

    // Spot-check content at the boundaries and a few interior points.
    foreach (var i in new[] { 0, 1, 7, 42, 255, 256, 511, 512, 998, 999 }) {
      var entry = byName[$"dir/file{i:D4}"];
      Assert.That(r.Extract(entry), Is.EqualTo(ContentFor(i)),
        $"content of file{i:D4} intact");
    }

    var dirPaths = r.Entries.Where(e => e.IsDirectory)
                            .Select(e => e.Name.Replace('\\', '/'))
                            .ToHashSet();
    Assert.That(dirPaths.Contains("dir"), Is.True, "the containing directory exists");
  }
}
