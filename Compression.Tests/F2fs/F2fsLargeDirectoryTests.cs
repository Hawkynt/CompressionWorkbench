using System.Text;
using FileSystem.F2fs;

namespace Compression.Tests.F2fs;

/// <summary>
/// Large-directory support for the F2FS writer. A directory whose entry count
/// exceeds the inline-dentry capacity (~180 slots in the inode block) must spill
/// its dentries into regular 4 KiB directory data blocks. Those blocks are
/// referenced from the directory inode's data pointers, so the reader walks them
/// and finds every child. The whole set of files must round-trip at its exact
/// nested path with intact content.
/// </summary>
[TestFixture]
public class F2fsLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void DirectoryWithManyFiles_RoundTripsThroughReader() {
    const int fileCount = 1000;
    var w = new F2fsWriter();
    for (var i = 0; i < fileCount; ++i)
      w.AddFile($"dir/file{i:D4}", Encoding.UTF8.GetBytes($"content-{i:D4}"));
    var img = w.Build();

    using var ms = new MemoryStream(img);
    var r = new F2fsReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory)
                         .ToDictionary(e => e.Name.Replace('\\', '/'), e => e);
    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.Replace('\\', '/'))
                        .ToHashSet();

    Assert.That(dirs.Contains("dir"), Is.True, "the containing directory exists");

    // Every file must be present at its exact path.
    for (var i = 0; i < fileCount; ++i)
      Assert.That(files.ContainsKey($"dir/file{i:D4}"), Is.True, $"file {i:D4} present at its path");

    Assert.That(files.Count, Is.EqualTo(fileCount), "exactly the added files are present");

    // Spot-check content across the range.
    foreach (var i in new[] { 0, 1, 199, 200, 213, 214, 500, 999 }) {
      var content = r.Extract(files[$"dir/file{i:D4}"]);
      Assert.That(content, Is.EqualTo(Encoding.UTF8.GetBytes($"content-{i:D4}")),
        $"file {i:D4} content intact");
    }
  }
}
