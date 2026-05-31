namespace Compression.Tests.Xfs;

/// <summary>
/// Large-directory support for the XFS writer. A short-form directory (the
/// inline <c>xfs_dir2_sf</c> list in the inode literal area) only fits a few
/// dozen short names before it overflows the ~448-byte fork. Beyond that the
/// directory must convert to the on-disk <c>xfs_dir2</c> block form: the entry
/// list moves out of the inode into a directory data block referenced by an
/// extent (<c>di_format</c> local→extents). This test pins a directory holding
/// far more entries than short-form can hold and asserts that every file
/// round-trips through the reader at its exact path with intact content.
/// </summary>
[TestFixture]
public class XfsLargeDirectoryTests {
  // 1000 short names (~12 bytes each on disk) are ~12 KiB of directory data —
  // hundreds of times the short-form capacity and several 4 KiB data blocks.
  private const int FileCount = 1000;

  private static byte[] PayloadFor(int i) => System.Text.Encoding.ASCII.GetBytes($"xfs-payload-{i:D4}");

  [Test, Category("RoundTrip")]
  public void DirectoryWithManyFiles_ExceedingShortForm_RoundTrips() {
    var w = new FileSystem.Xfs.XfsWriter();
    for (var i = 0; i < FileCount; i++)
      w.AddFile($"bigdir/file{i:D4}.dat", PayloadFor(i));

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.Xfs.XfsReader(ms);

    Assert.That(r.Entries.Count(e => e.IsDirectory && e.Name.Replace('\\', '/') == "bigdir"), Is.EqualTo(1),
      "the large parent directory must exist exactly once");

    var byName = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => e);

    for (var i = 0; i < FileCount; i++) {
      var path = $"bigdir/file{i:D4}.dat";
      Assert.That(byName.ContainsKey(path), Is.True, $"expected '{path}' to round-trip");
    }

    Assert.That(byName.Count(kv => kv.Key.StartsWith("bigdir/")), Is.EqualTo(FileCount),
      "all files of the large directory must be enumerated exactly once");

    foreach (var i in new[] { 0, 1, FileCount / 2, FileCount - 1 })
      Assert.That(r.Extract(byName[$"bigdir/file{i:D4}.dat"]), Is.EqualTo(PayloadFor(i)),
        $"content of 'bigdir/file{i:D4}.dat' must survive the round-trip");
  }
}
