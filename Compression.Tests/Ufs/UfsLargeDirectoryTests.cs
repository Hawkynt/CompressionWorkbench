namespace Compression.Tests.Ufs;

[TestFixture]
public class UfsLargeDirectoryTests {
  // A short-name UFS dir entry consumes (8 + namelen + 3) & ~3 bytes; with
  // ~13-byte names that is 24 bytes, so a single 8 KiB directory block holds at
  // most ~340 entries. 1200 files therefore force the directory across several
  // direct blocks, exercising the multi-block directory writer and reader.
  private const int FileCount = 1200;

  private static byte[] BuildImage(IEnumerable<(string Name, byte[] Data)> files) {
    var w = new FileSystem.Ufs.UfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  private static byte[] PayloadFor(int i) => System.Text.Encoding.ASCII.GetBytes($"payload-{i:D5}");

  [Test, Category("HappyPath")]
  public void DirectoryWithManyFiles_SpanningSeveralBlocks_RoundTrips() {
    var files = new List<(string, byte[])>(FileCount);
    for (var i = 0; i < FileCount; i++)
      files.Add(($"bigdir/file{i:D5}.dat", PayloadFor(i)));

    using var ms = new MemoryStream(BuildImage(files));
    var r = new FileSystem.Ufs.UfsReader(ms);

    // The shared parent directory exists exactly once as a real inode.
    Assert.That(r.Entries.Count(e => e.IsDirectory && e.Name == "bigdir"), Is.EqualTo(1),
      "the large parent directory must exist exactly once");

    // Every file is present at its nested path.
    var byName = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    for (var i = 0; i < FileCount; i++) {
      var path = $"bigdir/file{i:D5}.dat";
      Assert.That(byName.ContainsKey(path), Is.True, $"expected '{path}' to round-trip");
    }

    Assert.That(byName.Count(kv => kv.Key.StartsWith("bigdir/")), Is.EqualTo(FileCount),
      "all files of the large directory must be enumerated");

    // Spot-check content integrity at the start, middle and end of the directory.
    foreach (var i in new[] { 0, FileCount / 2, FileCount - 1 }) {
      var path = $"bigdir/file{i:D5}.dat";
      Assert.That(r.Extract(byName[path]), Is.EqualTo(PayloadFor(i)),
        $"content of '{path}' must survive the round-trip");
    }
  }
}
