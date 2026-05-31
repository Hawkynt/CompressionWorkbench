using FileSystem.Ext1;

namespace Compression.Tests.Ext1;

/// <summary>
/// ext1 is a flat WORM store: many files land in the single root directory.
/// When the root directory's entries no longer fit one data block, the writer
/// must give the root inode multiple data blocks (direct pointers, then a
/// singly-indirect block) and split the dir-entry records across them without
/// any record spanning a block boundary. The reader walks the inode's full
/// block list, so every file round-trips with its content intact.
/// </summary>
[TestFixture]
public class Ext1LargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void ManyFilesInRootDirectory_RoundTripThroughReader() {
    const int fileCount = 1000;

    var w = new Ext1Writer();
    for (var i = 0; i < fileCount; ++i)
      w.AddFile($"file{i:D4}", System.Text.Encoding.ASCII.GetBytes($"content-{i:D4}"));

    var image = w.Build();

    using var ms = new MemoryStream(image);
    using var r = new Ext1Reader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name, e => r.Extract(e));

    Assert.That(files.Count, Is.EqualTo(fileCount), "every file in the large root directory must be listed");
    for (var i = 0; i < fileCount; ++i) {
      var path = $"file{i:D4}";
      Assert.That(files.ContainsKey(path), Is.True, $"file present at its path: {path}");
    }

    foreach (var i in new[] { 0, 1, 13, 255, 256, 499, 500, 999 }) {
      var path = $"file{i:D4}";
      Assert.That(files[path], Is.EqualTo(System.Text.Encoding.ASCII.GetBytes($"content-{i:D4}")),
        $"content intact for {path}");
    }
  }
}
