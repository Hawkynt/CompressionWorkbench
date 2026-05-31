using System.Text;

namespace Compression.Tests.ReiserFs;

/// <summary>
/// Subdirectory support for the ReiserFS writer. A file added with a path that
/// contains separators (for example <c>docs/api/reference.txt</c>) must be
/// placed inside real directory objects — each intermediate component gets its
/// own object with a stat-data item (mode S_IFDIR) and a directory item holding
/// "." / ".." plus child entries — rather than being flattened into the root.
/// The reader must then recurse those directory objects and surface every file
/// at its full nested path.
/// </summary>
[TestFixture]
public class ReiserFsSubdirectoryTests {

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var rootPayload = "root file"u8.ToArray();
    var guidePayload = "in docs"u8.ToArray();
    var refPayload = "deep file"u8.ToArray();

    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    w.AddFile("readme.txt", rootPayload);
    w.AddFile("docs/guide.txt", guidePayload);
    w.AddFile("docs/api/reference.txt", refPayload);

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.Multiple(() => {
      Assert.That(byPath.ContainsKey("readme.txt"), Is.True, "root file present at its path");
      Assert.That(byPath.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
      Assert.That(byPath.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");
    });

    Assert.Multiple(() => {
      Assert.That(byPath["readme.txt"], Is.EqualTo(rootPayload), "root file content intact");
      Assert.That(byPath["docs/guide.txt"], Is.EqualTo(guidePayload), "one-level nested file content intact");
      Assert.That(byPath["docs/api/reference.txt"], Is.EqualTo(refPayload), "two-level nested file content intact");
    });

    // Intermediate directory objects must exist as real directory entries.
    var dirs = r.Entries.Where(e => e.IsDirectory)
                        .Select(e => e.Name.Replace('\\', '/'))
                        .ToHashSet();
    Assert.Multiple(() => {
      Assert.That(dirs, Does.Contain("docs"), "intermediate directory 'docs' present");
      Assert.That(dirs, Does.Contain("docs/api"), "intermediate directory 'docs/api' present");
    });
  }
}
