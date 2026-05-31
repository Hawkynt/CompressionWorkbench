namespace Compression.Tests.Ext;

/// <summary>
/// A single directory holding far more entries than fit in one data block must
/// still round-trip. The ext writer gives a directory inode multiple data blocks
/// (direct pointers, then a singly-indirect block) and splits its dir-entry
/// records across those blocks so that none spans a block boundary. The reader
/// walks the inode's full block list, so every file is found at its original
/// path "dir/fileNNN" with its content intact.
/// </summary>
[TestFixture]
public class ExtLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void ManyFilesInOneDirectory_RoundTripThroughReader() {
    const int fileCount = 1000;

    var w = new FileSystem.Ext.ExtWriter();
    for (var i = 0; i < fileCount; ++i)
      w.AddFile($"bigdir/file{i:D4}", System.Text.Encoding.ASCII.GetBytes($"content-{i:D4}"));

    var image = w.BuildAutoSized();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Ext.ExtReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name, e => r.Extract(e));

    Assert.That(files.Count, Is.EqualTo(fileCount), "every file in the large directory must be listed");
    for (var i = 0; i < fileCount; ++i) {
      var path = $"bigdir/file{i:D4}";
      Assert.That(files.ContainsKey(path), Is.True, $"file present at its path: {path}");
    }

    // Spot-check several contents fully.
    foreach (var i in new[] { 0, 1, 13, 255, 256, 499, 500, 999 }) {
      var path = $"bigdir/file{i:D4}";
      Assert.That(files[path], Is.EqualTo(System.Text.Encoding.ASCII.GetBytes($"content-{i:D4}")),
        $"content intact for {path}");
    }
  }
}
