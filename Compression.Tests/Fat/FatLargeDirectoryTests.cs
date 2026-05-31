using System.Text;

namespace Compression.Tests.Fat;

/// <summary>
/// Large-directory support for the FAT writer. A single subdirectory holding
/// many small files must grow beyond one cluster by allocating a multi-cluster
/// chain (FatWriter.PlaceTree sizes each directory to ceil(content/clusterSize)
/// clusters). The fixed FAT12/16 root cannot grow, so the files live in a
/// subdirectory; the image is auto-sized to fit. Every file must round-trip at
/// its correct nested path with its content intact.
/// </summary>
[TestFixture]
public class FatLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void ThousandFilesInOneSubdirectory_AllRoundTrip() {
    const int count = 1000;

    var w = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < count; i++)
      w.AddFile($"big/file{i:D4}.txt", Encoding.ASCII.GetBytes($"content-{i}"));
    var disk = w.BuildAutoSized();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);

    var byPath = r.Entries.Where(e => !e.IsDirectory)
                          .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));

    Assert.That(byPath.Count, Is.EqualTo(count),
      $"all {count} files present in the multi-cluster subdirectory");

    for (var i = 0; i < count; i++) {
      var path = $"big/file{i:D4}.txt";
      Assert.That(byPath.ContainsKey(path), Is.True, $"{path} present");
    }

    // Spot-check several full contents across the range.
    foreach (var i in new[] { 0, 1, 17, 499, 500, 998, 999 })
      Assert.That(byPath[$"big/file{i:D4}.txt"],
        Is.EqualTo(Encoding.ASCII.GetBytes($"content-{i}")),
        $"content of file{i:D4}.txt intact");
  }
}
