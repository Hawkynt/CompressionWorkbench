using System.Text;

namespace Compression.Tests.Yaffs2;

/// <summary>
/// Large-directory support for the YAFFS2 writer. YAFFS2 is log-structured: every
/// object header (including each directory entry) is its own chunk that names its
/// parent object id, so a directory has no inherent entry cap. Many files placed
/// in one directory must all round-trip at their correct nested paths with content
/// intact, exercising that the scanner reads every object header and reassembles
/// the parent chain for all of them.
/// </summary>
[TestFixture]
public class Yaffs2LargeDirectoryTests {

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
  public void ThousandFilesInOneDirectory_AllRoundTrip() {
    const int count = 1000;

    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    for (var i = 0; i < count; i++)
      w.AddFile($"big/f{i:D4}.txt", Encoding.ASCII.GetBytes($"content-{i}"));
    var image = w.Build();

    var scan = FileSystem.Yaffs2.Yaffs2Scanner.Scan(image);
    Assert.That(scan.ParseOk, Is.True);

    var paths = PathsById(scan);
    var fileByPath = scan.Objects
      .Where(o => o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.File)
      .ToDictionary(o => paths[o.ObjectId], o => o);

    Assert.That(fileByPath.Count, Is.EqualTo(count),
      $"all {count} file objects recovered by the scanner");

    for (var i = 0; i < count; i++) {
      var path = $"big/f{i:D4}.txt";
      Assert.That(fileByPath.ContainsKey(path), Is.True, $"{path} present");
    }

    // Spot-check several full contents across the range.
    foreach (var i in new[] { 0, 1, 333, 499, 500, 998, 999 }) {
      var obj = fileByPath[$"big/f{i:D4}.txt"];
      Assert.That(scan.DataChunks.ContainsKey(obj.ObjectId), Is.True,
        $"data chunks present for f{i:D4}.txt");
      // The scanner records where a chunk is, not a copy of it.
      var data = scan.DataChunks[obj.ObjectId]
        .SelectMany(c => image.Skip((int)c.Offset).Take(c.Length))
        .Take((int)obj.Size).ToArray();
      Assert.That(data, Is.EqualTo(Encoding.ASCII.GetBytes($"content-{i}")),
        $"content of f{i:D4}.txt intact");
    }
  }
}
