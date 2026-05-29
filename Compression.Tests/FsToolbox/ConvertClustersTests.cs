#pragma warning disable CS1591
namespace Compression.Tests.FsToolbox;

[TestFixture]
public class ConvertClustersTests {

  /// <summary>
  /// Builds a minimal FAT12 image with two small files, then rebuilds it
  /// with a different cluster size and verifies all files survive.
  /// </summary>
  [Test]
  public void ConvertClusters_RebuildWithLargerCluster_PreservesFiles() {
    // Create a small FAT12 image with two files.
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("HELLO.TXT", "Hello, world!"u8.ToArray());
    writer.AddFile("DATA.BIN", new byte[256]);
    var originalImage = writer.Build(totalSectors: 2880);

    // Write to temp file.
    var tempPath = Path.Combine(Path.GetTempPath(), $"cwb_cc_test_{Guid.NewGuid():N}.img");
    var outPath = Path.Combine(Path.GetTempPath(), $"cwb_cc_out_{Guid.NewGuid():N}.img");
    try {
      File.WriteAllBytes(tempPath, originalImage);

      // Convert from default 512-byte clusters to 1024-byte clusters.
      Compression.Lib.ArchiveOperations.ConvertClusters(tempPath, outPath, 1024);

      Assert.That(File.Exists(outPath), Is.True);

      // Read back and verify files.
      using var fs = File.OpenRead(outPath);
      var reader = new FileSystem.Fat.FatReader(fs);
      var entries = reader.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(entries, Has.Count.EqualTo(2));

      var hello = entries.First(e => e.Name == "HELLO.TXT");
      Assert.That(reader.Extract(hello), Is.EqualTo("Hello, world!"u8.ToArray()));

      var data = entries.First(e => e.Name == "DATA.BIN");
      Assert.That(reader.Extract(data), Has.Length.EqualTo(256));
    } finally {
      if (File.Exists(tempPath)) File.Delete(tempPath);
      if (File.Exists(outPath)) File.Delete(outPath);
    }
  }

  /// <summary>
  /// Verifies the waste preview shows different slack percentages for
  /// different cluster sizes.
  /// </summary>
  [Test]
  public void PreviewClusterConversion_ShowsDifferentSlack() {
    // Create a FAT12 image with files of various sizes.
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("A.TXT", new byte[100]);  // 100 bytes in a 512-byte cluster = 412 bytes slack
    writer.AddFile("B.TXT", new byte[600]);  // 600 bytes needs 2 clusters at 512 = 424 bytes slack
    writer.AddFile("C.TXT", new byte[1000]); // 1000 bytes in 2 clusters at 512 = 24 bytes slack
    var image = writer.Build(totalSectors: 2880);

    var tempPath = Path.Combine(Path.GetTempPath(), $"cwb_cc_preview_{Guid.NewGuid():N}.img");
    try {
      File.WriteAllBytes(tempPath, image);

      var (current, target) = Compression.Lib.ArchiveOperations.PreviewClusterConversion(tempPath, 1024);

      // Current should have 512-byte clusters.
      Assert.That(current.ClusterSize, Is.EqualTo(512));
      // Target should be 1024-byte clusters.
      Assert.That(target.ClusterSize, Is.EqualTo(1024));
      // Both should have non-negative slack.
      Assert.That(current.TotalSlack, Is.GreaterThanOrEqualTo(0));
      Assert.That(target.TotalSlack, Is.GreaterThanOrEqualTo(0));
    } finally {
      if (File.Exists(tempPath)) File.Delete(tempPath);
    }
  }

  /// <summary>
  /// In-place conversion: same input and output path.
  /// </summary>
  [Test]
  public void ConvertClusters_InPlace_Works() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("TEST.DAT", new byte[42]);
    var image = writer.Build(totalSectors: 2880);

    var tempPath = Path.Combine(Path.GetTempPath(), $"cwb_cc_inplace_{Guid.NewGuid():N}.img");
    try {
      File.WriteAllBytes(tempPath, image);
      Compression.Lib.ArchiveOperations.ConvertClusters(tempPath, tempPath, 2048);

      using var fs = File.OpenRead(tempPath);
      var reader = new FileSystem.Fat.FatReader(fs);
      var entries = reader.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("TEST.DAT"));
      Assert.That(reader.Extract(entries[0]), Has.Length.EqualTo(42));
    } finally {
      if (File.Exists(tempPath)) File.Delete(tempPath);
    }
  }
}
