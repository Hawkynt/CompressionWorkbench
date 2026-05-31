using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// Large-directory support for the APFS writer. A single directory holding many
/// files produces more filesystem-tree records (INODE + DIR_REC + FILE_EXTENT)
/// than fit in one 4 KiB B-tree node. The writer must therefore split the
/// FS-tree into several leaf nodes beneath an internal (level &gt;= 1) node, keep
/// the records in APFS key order, and the reader must descend the internal node
/// to reach every leaf. All files must round-trip through the reader at their
/// correct "dir/fileNNNN" paths with their content intact.
/// </summary>
[TestFixture]
public class ApfsLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void ManyFilesInOneDirectory_RoundTripThroughReader() {
    const int fileCount = 1500;
    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    var w = new ApfsWriter();
    w.SetMinImageSize(64 * 1024 * 1024);
    for (var i = 0; i < fileCount; i++) {
      var path = $"big/file{i:D4}";
      // Small, deterministic, per-file distinct content for spot-check verification.
      var content = System.Text.Encoding.ASCII.GetBytes($"content-{i:D4}");
      expected[path] = content;
      w.AddFile(path, content);
    }
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new ApfsReader(ms);

    var filesByPath = r.Entries.Where(e => !e.IsDirectory)
                               .ToDictionary(e => e.Name.Replace('\\', '/'), e => e);

    Assert.That(filesByPath, Has.Count.EqualTo(fileCount),
      "every file in the large directory must be present after round-trip");

    // The containing directory must exist as a real directory inode.
    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.Replace('\\', '/'))
                        .ToHashSet();
    Assert.That(dirs.Contains("big"), Is.True, "containing directory present as a real inode");

    // Every expected path is present.
    foreach (var path in expected.Keys)
      Assert.That(filesByPath.ContainsKey(path), Is.True, $"missing entry: {path}");

    // Spot-check content across the whole range, including first/last and boundaries.
    foreach (var i in new[] { 0, 1, 255, 256, 511, 512, 750, 1023, 1024, 1499 }) {
      var path = $"big/file{i:D4}";
      Assert.That(r.Extract(filesByPath[path]), Is.EqualTo(expected[path]),
        $"content intact for {path}");
    }
  }
}
