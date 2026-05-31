namespace Compression.Tests.Ntfs;

/// <summary>
/// Large-directory support for the NTFS writer. A single directory holding far
/// more file-name entries than fit in a resident $INDEX_ROOT must spill the
/// index into a non-resident $INDEX_ALLOCATION attribute (INDX blocks) tracked
/// by a $BITMAP, with the $INDEX_ROOT holding the upper entries pointing down
/// to the INDX leaves. The reader must walk the $INDEX_ALLOCATION blocks (USA
/// fixups undone) and surface every file at its exact path with intact content.
/// </summary>
[TestFixture]
public class NtfsLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void DirectoryWithThousandFiles_RoundTripsThroughReader() {
    const int count = 1000;
    var w = new FileSystem.Ntfs.NtfsWriter();

    // One directory ("dir") holding 1000 short-named files. Each file's content
    // is its own index so we can spot-check that data lands at the right path.
    var expected = new Dictionary<string, byte[]>();
    for (var i = 0; i < count; i++) {
      var path = $"dir/file{i:D4}";
      var content = System.Text.Encoding.ASCII.GetBytes($"content-{i:D4}");
      w.AddFile(path, content);
      expected[path] = content;
    }

    // A larger volume so the data + MFT records comfortably fit.
    var disk = w.Build(32 * 1024 * 1024);

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Ntfs.NtfsReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.That(files, Has.Count.EqualTo(count),
      "every file in the large directory is enumerated at its nested path");

    // All paths present.
    foreach (var path in expected.Keys)
      Assert.That(files.ContainsKey(path), Is.True, $"file present at '{path}'");

    // Spot-check content at a spread of indices, including first and last.
    foreach (var i in new[] { 0, 1, 250, 499, 500, 750, 999 }) {
      var path = $"dir/file{i:D4}";
      Assert.That(files[path], Is.EqualTo(expected[path]), $"content intact for '{path}'");
    }

    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.Replace('\\', '/'))
                        .ToHashSet();
    Assert.That(dirs.Contains("dir"), Is.True, "the large directory exists as a real directory record");
  }
}
