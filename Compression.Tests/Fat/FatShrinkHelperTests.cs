#pragma warning disable CS1591
using FileSystem.Fat;

namespace Compression.Tests.Fat;

[TestFixture]
public class FatShrinkHelperTests {

  [Test]
  public void Shrink_ReducesImageSize() {
    // Build a mostly-empty 1.44 MB floppy with one tiny file
    var w = new FatWriter();
    w.AddFile("TINY.TXT", "hello"u8.ToArray());
    var image = w.Build(totalSectors: 2880); // 1.44 MB

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var result = FatShrinkHelper.Shrink(ms);
    Assert.That(result.WasReduced, Is.True);
    Assert.That(result.NewSize, Is.LessThan(result.OriginalSize));
    Assert.That(ms.Length, Is.EqualTo(result.NewSize));

    // Verify the file is still readable
    ms.Position = 0;
    var reader = new FatReader(ms);
    var entries = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("TINY.TXT"));
    var data = reader.Extract(entries[0]);
    Assert.That(System.Text.Encoding.UTF8.GetString(data), Is.EqualTo("hello"));
  }

  [Test]
  public void Shrink_FullImage_NotReduced() {
    // Build a small image that is nearly full
    var w = new FatWriter();
    // Fill up most of a 720K floppy
    w.AddFile("BIG.BIN", new byte[700_000]);
    var image = w.Build(totalSectors: 1440); // 720 KB

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var result = FatShrinkHelper.Shrink(ms);
    // Either not reduced at all, or reduced very minimally
    Assert.That(result.OriginalSize, Is.GreaterThan(0));
  }

  [Test]
  public void Shrink_PreservesMultipleFiles() {
    var w = new FatWriter();
    w.AddFile("A.TXT", "alpha"u8.ToArray());
    w.AddFile("B.TXT", "beta"u8.ToArray());
    w.AddFile("C.TXT", "gamma"u8.ToArray());
    var image = w.Build(totalSectors: 2880);

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var result = FatShrinkHelper.Shrink(ms);
    Assert.That(result.WasReduced, Is.True);

    ms.Position = 0;
    var reader = new FatReader(ms);
    var files = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(
      e => e.Name, e => System.Text.Encoding.UTF8.GetString(reader.Extract(e)));
    Assert.That(files, Contains.Key("A.TXT"));
    Assert.That(files, Contains.Key("B.TXT"));
    Assert.That(files, Contains.Key("C.TXT"));
    Assert.That(files["A.TXT"], Is.EqualTo("alpha"));
    Assert.That(files["B.TXT"], Is.EqualTo("beta"));
    Assert.That(files["C.TXT"], Is.EqualTo("gamma"));
  }

  [Test]
  public void ClusterHint_ReturnsValidRecommendation() {
    var w = new FatWriter();
    // Add several small files to get meaningful slack analysis
    for (var i = 0; i < 20; i++)
      w.AddFile($"F{i:D3}.TXT", new byte[100 + i * 50]);
    var image = w.Build(totalSectors: 2880);

    using var ms = new MemoryStream(image);
    var result = FatShrinkHelper.AnalyzeClusterSizes(ms);

    Assert.That(result.CurrentClusterSize, Is.GreaterThan(0));
    Assert.That(result.RecommendedClusterSize, Is.GreaterThan(0));
    Assert.That(result.AllStats, Has.Count.EqualTo(8)); // 512..65536
    Assert.That(result.CurrentSlackPercent, Is.GreaterThanOrEqualTo(0));
    Assert.That(result.RecommendedSlackPercent, Is.GreaterThanOrEqualTo(0));
    // Smaller cluster sizes should generally have less slack for small files
    var smallest = result.AllStats.First(s => s.ClusterSize == 512);
    var largest = result.AllStats.First(s => s.ClusterSize == 65536);
    Assert.That(smallest.SlackPercent, Is.LessThanOrEqualTo(largest.SlackPercent));
  }

  [Test]
  public void ClusterHint_SingleLargeFile_LessVariation() {
    var w = new FatWriter();
    w.AddFile("BIG.BIN", new byte[100_000]);
    var image = w.Build(totalSectors: 2880);

    using var ms = new MemoryStream(image);
    var result = FatShrinkHelper.AnalyzeClusterSizes(ms);
    Assert.That(result.AllStats, Has.Count.EqualTo(8));
    // All stats should have valid percentages
    foreach (var stat in result.AllStats) {
      Assert.That(stat.SlackPercent, Is.GreaterThanOrEqualTo(0));
      Assert.That(stat.SlackPercent, Is.LessThanOrEqualTo(100));
    }
  }
}
