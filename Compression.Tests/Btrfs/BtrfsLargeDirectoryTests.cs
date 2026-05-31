namespace Compression.Tests.Btrfs;

/// <summary>
/// Large-directory support for the Btrfs writer. A single directory holding
/// far more entries than fit in one 16 KiB leaf node must force the FS tree to
/// grow: the writer splits the sorted item set across several leaf nodes and
/// introduces an internal (index) node whose key pointers reference each leaf.
/// <see cref="FileSystem.Btrfs.BtrfsReader"/> descends the internal node to
/// reach every leaf, so all files round-trip at "dir/fileNNNN" with their
/// content intact regardless of how many leaves the tree spans.
/// </summary>
[TestFixture]
public class BtrfsLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void DirectoryWithManyFiles_SpanningMultipleLeaves_RoundTripsThroughReader() {
    const int fileCount = 1500;
    var w = new FileSystem.Btrfs.BtrfsWriter();

    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < fileCount; i++) {
      var path = $"dir/file{i:D4}";
      var content = System.Text.Encoding.ASCII.GetBytes($"payload-{i}");
      w.AddFile(path, content);
      expected[path] = content;
    }

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.Btrfs.BtrfsReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name.Replace('\\', '/'), e => e);

    Assert.That(files.Count, Is.EqualTo(fileCount),
      "every added file must be enumerated across all FS-tree leaves");

    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.Replace('\\', '/'))
                        .ToHashSet();
    Assert.That(dirs.Contains("dir"), Is.True, "the parent directory exists");

    // Every file present at its exact nested path.
    foreach (var path in expected.Keys)
      Assert.That(files.ContainsKey(path), Is.True, $"file present at '{path}'");

    // Spot-check content across the whole range (first, last, and a spread).
    int[] probes = [0, 1, 7, 63, 64, 65, 500, 999, 1000, 1499];
    foreach (var i in probes) {
      var path = $"dir/file{i:D4}";
      var got = r.Extract(files[path]);
      Assert.That(got, Is.EqualTo(expected[path]), $"content of '{path}' intact");
    }
  }
}
