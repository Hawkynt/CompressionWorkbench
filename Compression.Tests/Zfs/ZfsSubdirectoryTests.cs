using System.Linq;

namespace Compression.Tests.Zfs;

/// <summary>
/// Subdirectory support for the ZFS writer. A file added with a path-separated name
/// (e.g. <c>docs/api/reference.txt</c>) must be placed inside real directory objects —
/// a directory dnode whose data is a ZAP mapping the child name to the child object id —
/// rather than flattened into the root directory ZAP under its full path string.
/// The file must round-trip through <see cref="FileSystem.Zfs.ZfsReader"/> at the exact
/// nested path, and the intermediate directories must be discoverable.
/// </summary>
[TestFixture]
public class ZfsSubdirectoryTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new FileSystem.Zfs.ZfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughReader() {
    var img = BuildImage(
      ("readme.txt", "root file"u8.ToArray()),
      ("docs/guide.txt", "in docs"u8.ToArray()),
      ("docs/api/reference.txt", "deep file"u8.ToArray()));

    using var ms = new MemoryStream(img);
    using var r = new FileSystem.Zfs.ZfsReader(ms);

    var byName = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.That(byName.ContainsKey("readme.txt"), Is.True, "root file present at its path");
    Assert.That(byName.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(byName.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");

    Assert.That(byName["readme.txt"], Is.EqualTo("root file"u8.ToArray()), "root file content intact");
    Assert.That(byName["docs/guide.txt"], Is.EqualTo("in docs"u8.ToArray()), "nested file content intact");
    Assert.That(byName["docs/api/reference.txt"], Is.EqualTo("deep file"u8.ToArray()), "deep file content intact");

    var dirPaths = r.Entries.Where(e => e.IsDirectory)
                            .Select(e => e.Name.Replace('\\', '/'))
                            .ToHashSet();
    Assert.That(dirPaths.Contains("docs"), Is.True, "intermediate directory 'docs' exists as a real directory");
    Assert.That(dirPaths.Contains("docs/api"), Is.True, "intermediate directory 'docs/api' exists as a real directory");
  }
}
