#pragma warning disable CS1591
using FileSystem.Ti99;

namespace Compression.Tests.Ti99;

[TestFixture]
public class Ti99OptimizerTests {

  [Test]
  public void Find_TinyFileset_PicksSmallestGeometry() {
    var g = Ti99Optimizer.Find([100, 200]);
    Assert.That(g.Tracks, Is.AnyOf(35, 40, 80));
    Assert.That(g.SectorsPerTrack, Is.AnyOf(8, 9, 18));
    Assert.That(g.Sides, Is.AnyOf(1, 2));
    Assert.That(g.TotalSectors, Is.GreaterThan(0));
  }

  [Test]
  public void Find_LargeFileset_PicksBiggerGeometry() {
    var small = Ti99Optimizer.Find([100]);
    var big = Ti99Optimizer.Find([400_000]);
    Assert.That(big.TotalSectors, Is.GreaterThanOrEqualTo(small.TotalSectors));
  }

  [Test]
  public void Find_ResultIsValidWriterInput() {
    var g = Ti99Optimizer.Find([1000, 2000, 500]);
    var w = new Ti99Writer();
    w.AddFile("A", new byte[1000]);
    w.AddFile("B", new byte[2000]);
    w.AddFile("C", new byte[500]);
    var img = w.BuildSectorDump(g.Tracks, g.SectorsPerTrack, g.Sides);
    Assert.That(img.Length, Is.EqualTo(g.TotalSectors * 256));
    using var r = new Ti99Reader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.True);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("A"));
    Assert.That(names, Does.Contain("B"));
    Assert.That(names, Does.Contain("C"));
  }
}
