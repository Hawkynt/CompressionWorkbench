using System.Text;

namespace Compression.Tests.Adf;

/// <summary>
/// Large-directory support for the Amiga ADF writer. An AmigaDOS directory keeps
/// a 72-entry hash table; entries that collide in a bucket are linked through the
/// hash-chain field, so a single directory holds far more than 72 children. The
/// writer must chain colliding entries correctly and the reader must follow every
/// chain to recover them all.
///
/// A standard DD floppy is only 880 KB (1760 sectors), and each tiny file costs a
/// header block plus a data block, so the practical ceiling for one directory is
/// roughly 850 files. We use 500 — comfortably within capacity while still placing
/// about seven entries in every hash bucket, exercising the chaining on both
/// sides. (The format has no separate FFS "directory cache"; capacity is bounded
/// only by free sectors, not by any per-directory entry cap.)
/// </summary>
[TestFixture]
public class AdfLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void HundredsOfFilesInOneDirectory_ChainThroughHashBuckets_AllRoundTrip() {
    const int count = 500;

    var w = new FileSystem.Adf.AdfWriter();
    for (var i = 0; i < count; i++)
      w.AddFile($"big/f{i:D4}.txt", Encoding.ASCII.GetBytes($"content-{i}"));
    var disk = w.Build();

    Assert.That(disk.Length, Is.EqualTo(901120), "standard DD floppy image size");

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Adf.AdfReader(ms);

    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.FullPath, e => r.Extract(e));

    Assert.That(byPath.Count, Is.EqualTo(count),
      $"all {count} files recovered by following every hash chain");

    for (var i = 0; i < count; i++) {
      var path = $"big/f{i:D4}.txt";
      Assert.That(byPath.ContainsKey(path), Is.True, $"{path} present");
    }

    // Spot-check several full contents across the range.
    foreach (var i in new[] { 0, 1, 73, 144, 250, 498, 499 })
      Assert.That(byPath[$"big/f{i:D4}.txt"],
        Is.EqualTo(Encoding.ASCII.GetBytes($"content-{i}")),
        $"content of f{i:D4}.txt intact");
  }
}
