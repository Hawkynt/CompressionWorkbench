namespace Compression.Tests.MinixFs;

[TestFixture]
public class MinixFsLargeDirectoryTests {
  // A Minix v3 directory zone (1024 bytes) holds 1024/64 = 16 fixed-size
  // entries. 1200 files need ~75 zones, far beyond the 7 direct zones of an
  // inode, so the directory must spill into a single-indirect zone. This pins
  // the multi-zone (direct + indirect) directory writer and reader.
  private const int FileCount = 1200;

  private static byte[] BuildImage(IEnumerable<(string Name, byte[] Data)> files) {
    using var ms = new MemoryStream();
    var w = new FileSystem.MinixFs.MinixFsWriter(ms, leaveOpen: true);
    foreach (var (n, d) in files) w.AddFile(n, d);
    w.Finish();
    return ms.ToArray();
  }

  private static byte[] PayloadFor(int i) => System.Text.Encoding.ASCII.GetBytes($"payload-{i:D5}");

  [Test, Category("RoundTrip")]
  public void DirectoryWithManyFiles_SpanningDirectAndIndirectZones_RoundTrips() {
    var files = new List<(string, byte[])>(FileCount);
    for (var i = 0; i < FileCount; i++)
      files.Add(($"bigdir/file{i:D5}.dat", PayloadFor(i)));

    using var ms = new MemoryStream(BuildImage(files));
    var r = new FileSystem.MinixFs.MinixFsReader(ms);

    Assert.That(r.Entries.Count(e => e.IsDirectory && e.Name == "bigdir"), Is.EqualTo(1),
      "the large parent directory must exist exactly once");

    var byName = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    for (var i = 0; i < FileCount; i++) {
      var path = $"bigdir/file{i:D5}.dat";
      Assert.That(byName.ContainsKey(path), Is.True, $"expected '{path}' to round-trip");
    }

    Assert.That(byName.Count(kv => kv.Key.StartsWith("bigdir/")), Is.EqualTo(FileCount),
      "all files of the large directory must be enumerated");

    foreach (var i in new[] { 0, FileCount / 2, FileCount - 1 }) {
      var path = $"bigdir/file{i:D5}.dat";
      Assert.That(r.Extract(byName[path]), Is.EqualTo(PayloadFor(i)),
        $"content of '{path}' must survive the round-trip");
    }
  }
}
