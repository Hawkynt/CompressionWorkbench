namespace Compression.Tests.Yaffs2;

/// <summary>
/// Subdirectory support for the YAFFS2 writer. A file added with a path that
/// contains separators (for example "docs/api/reference.txt") must be placed
/// inside real directory objects rather than flattened into the root, and must
/// round-trip back through the scanner at that exact nested path.
/// </summary>
[TestFixture]
public class Yaffs2SubdirectoryTests {

  private static Dictionary<int, string> PathsById(FileSystem.Yaffs2.Yaffs2Scanner.ScanResult scan) {
    var byId = scan.Objects.ToDictionary(o => o.ObjectId);
    var paths = new Dictionary<int, string>();
    foreach (var o in scan.Objects) {
      var segments = new List<string>();
      var cur = o;
      var guard = 0;
      while (cur != null && guard++ < 256) {
        if (string.IsNullOrEmpty(cur.Name)) break;
        segments.Add(cur.Name);
        if (cur.ParentId is 1 or 0 || cur.ParentId == cur.ObjectId) break;
        if (!byId.TryGetValue(cur.ParentId, out var parent)) break;
        cur = parent;
      }
      segments.Reverse();
      paths[o.ObjectId] = string.Join('/', segments);
    }
    return paths;
  }

  [Test, Category("RoundTrip")]
  public void NestedPaths_RoundTripThroughScanner() {
    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    var image = w.Build();

    var scan = FileSystem.Yaffs2.Yaffs2Scanner.Scan(image);
    Assert.That(scan.ParseOk, Is.True);

    var paths = PathsById(scan);

    // Files must round-trip at their full nested paths.
    var fileByPath = scan.Objects
      .Where(o => o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.File)
      .ToDictionary(o => paths[o.ObjectId], o => o);

    Assert.That(fileByPath.ContainsKey("readme.txt"), Is.True, "root file present at its path");
    Assert.That(fileByPath.ContainsKey("docs/guide.txt"), Is.True, "one-level nested file present at its path");
    Assert.That(fileByPath.ContainsKey("docs/api/reference.txt"), Is.True, "two-level nested file present at its path");

    // Intermediate directory objects must exist.
    var dirPaths = scan.Objects
      .Where(o => o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.Directory)
      .Select(o => paths[o.ObjectId])
      .ToHashSet();
    Assert.That(dirPaths, Does.Contain("docs"), "intermediate directory 'docs' exists");
    Assert.That(dirPaths, Does.Contain("docs/api"), "intermediate directory 'docs/api' exists");

    // Nested content must be intact.
    var deep = fileByPath["docs/api/reference.txt"];
    Assert.That(scan.DataChunks.ContainsKey(deep.ObjectId), Is.True);
    var chunks = scan.DataChunks[deep.ObjectId];
    var data = chunks.SelectMany(c => image.Skip((int)c.Offset).Take(c.Length))
      .Take((int)deep.Size).ToArray();
    Assert.That(data, Is.EqualTo("deep file"u8.ToArray()), "nested file content intact");
  }

  [Test, Category("Spec")]
  public void SharedDirectory_IsCreatedOnce() {
    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    w.AddFile("docs/a.txt", "a"u8.ToArray());
    w.AddFile("docs/b.txt", "b"u8.ToArray());
    var image = w.Build();

    var scan = FileSystem.Yaffs2.Yaffs2Scanner.Scan(image);
    var paths = PathsById(scan);
    var docsDirs = scan.Objects
      .Where(o => o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.Directory)
      .Count(o => paths[o.ObjectId] == "docs");
    Assert.That(docsDirs, Is.EqualTo(1), "the shared 'docs' directory is created exactly once");
  }
}
