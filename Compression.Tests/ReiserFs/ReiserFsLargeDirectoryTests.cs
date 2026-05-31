using System.Text;

namespace Compression.Tests.ReiserFs;

/// <summary>
/// Large-directory support for the ReiserFS writer. A single directory holding
/// many entries cannot fit inside one 4 KiB leaf block: both the directory item
/// (the reiserfs_de_head array plus packed names) and the per-file stat-data /
/// direct items overflow it. The writer must therefore grow a real S+tree —
/// formatting several leaf blocks and an internal block that points at them via
/// disk_child pointers — and the reader must descend that internal block to
/// reach every leaf. Each file must round-trip at its full path with its content
/// intact.
/// </summary>
[TestFixture]
public class ReiserFsLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void DirectoryWithManyFiles_RoundTripsThroughReader() {
    const int fileCount = 1000;

    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < fileCount; i++) {
      var path = $"dir/file{i:D4}";
      var payload = Encoding.ASCII.GetBytes($"content-{i:D4}");
      w.AddFile(path, payload);
      expected[path] = payload;
    }

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.That(byPath, Has.Count.EqualTo(fileCount),
      "every file in the large directory must be surfaced exactly once");

    Assert.Multiple(() => {
      foreach (var (path, _) in expected)
        Assert.That(byPath.ContainsKey(path), Is.True, $"file present at its path: {path}");
    });

    // Spot-check content across the range (first, last, a few middles).
    foreach (var i in new[] { 0, 1, 250, 499, 500, 750, 998, 999 }) {
      var path = $"dir/file{i:D4}";
      Assert.That(byPath[path], Is.EqualTo(expected[path]), $"content intact for {path}");
    }

    // The containing directory must surface as a real directory object.
    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.Replace('\\', '/'))
                        .ToHashSet();
    Assert.That(dirs, Does.Contain("dir"), "containing directory present");
  }
}
