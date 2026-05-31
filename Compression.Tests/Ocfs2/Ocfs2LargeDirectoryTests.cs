using System.Text;

namespace Compression.Tests.Ocfs2;

/// <summary>
/// A single OCFS2 directory holding many files must round-trip. Once the
/// directory's <c>ocfs2_dir_entry</c> records exceed the inline capacity of the
/// dinode, the writer must switch the directory to extent-backed allocation:
/// clear OCFS2_INLINE_DATA_FL, allocate data clusters, write the dir entries
/// across them ("." / ".." first), and record the extent list plus i_size. The
/// reader must walk those extent-backed directory blocks, not only inline data.
/// </summary>
[TestFixture]
public class Ocfs2LargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void ManyFilesInOneDirectory_RoundTripThroughReader() {
    const int count = 2000;
    var w = new FileSystem.Ocfs2.Ocfs2Writer();
    for (var i = 0; i < count; i++)
      w.AddFile($"bulk/file_{i:D4}.txt", Encoding.ASCII.GetBytes($"payload-{i}"));
    var image = w.Build();

    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    using var ms = new MemoryStream(image);

    var paths = d.List(ms, null).Select(e => e.Name.Replace('\\', '/')).ToHashSet();
    Assert.That(paths.Count, Is.EqualTo(count), "every file in the large directory is recovered");

    ms.Position = 0;
    var outDir = Path.Combine(Path.GetTempPath(), "ocfs2_large_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      foreach (var i in new[] { 0, 1, count / 2, count - 2, count - 1 }) {
        var rel = Path.Combine("bulk", $"file_{i:D4}.txt");
        var full = Path.Combine(outDir, rel);
        Assert.That(File.Exists(full), Is.True, $"{rel} extracted");
        Assert.That(File.ReadAllBytes(full), Is.EqualTo(Encoding.ASCII.GetBytes($"payload-{i}")),
          $"{rel} content intact");
      }
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }
}
