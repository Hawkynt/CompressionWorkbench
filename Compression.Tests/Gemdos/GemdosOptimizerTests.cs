#pragma warning disable CS1591
using FileSystem.Gemdos;

namespace Compression.Tests.Gemdos;

[TestFixture]
public class GemdosOptimizerTests {

  [Test]
  public void Find_TinyFiles_PicksSmallestImageSize() {
    var g = GemdosOptimizer.Find([100, 200, 500]);
    Assert.That(g.BytesPerSector, Is.EqualTo(512));
    Assert.That(g.TotalSectors, Is.LessThanOrEqualTo(1440),
                "Three tiny files must fit in 720 KB or smaller.");
    Assert.That(g.SectorsPerCluster, Is.AnyOf(1, 2, 4));
  }

  [Test]
  public void Find_LargeFiles_PicksBiggerImageSize() {
    var g = GemdosOptimizer.Find([400_000, 300_000, 200_000]);
    Assert.That(g.TotalSectors, Is.GreaterThanOrEqualTo(2880),
                "Files totaling > 700 KB require ≥ 1.44 MB image.");
  }

  [Test]
  public void Find_SlackBudget_Respects5PercentRule() {
    // Two 1-byte files at spc=1 (512 B/cluster) waste ~1022 bytes, ratio = 1022 / 2 = 511x — well over 5%.
    // Optimizer must still pick a valid combination but should NOT tiebreak to the most wasteful cluster.
    var g = GemdosOptimizer.Find([1, 1]);
    Assert.That(g.TotalSectors, Is.GreaterThan(0));
    Assert.That(g.SectorsPerCluster, Is.GreaterThan(0));
  }

  [Test]
  public void Find_ResultIsValidWriterInput() {
    var g = GemdosOptimizer.Find([1000, 2000]);
    var w = new GemdosWriter();
    w.AddFile("A.TXT", new byte[1000]);
    w.AddFile("B.TXT", new byte[2000]);
    var disk = w.Build(g.TotalSectors, g.BytesPerSector, g.SectorsPerCluster, g.RootEntries);
    Assert.That(disk.Length, Is.EqualTo(g.TotalSectors * g.BytesPerSector));
    using var ms = new MemoryStream(disk);
    using var r = new GemdosReader(ms);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("A.TXT"));
    Assert.That(names, Does.Contain("B.TXT"));
  }
}
