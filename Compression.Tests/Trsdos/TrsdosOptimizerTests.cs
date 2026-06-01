#pragma warning disable CS1591
using FileSystem.Trsdos;

namespace Compression.Tests.Trsdos;

/// <summary>
/// Verifies <see cref="TrsdosOptimizer.Find"/> picks the smallest
/// geometry whose data area fits the supplied file set with ≤ 5 % slack.
/// </summary>
[TestFixture]
public class TrsdosOptimizerTests {

  [Test, Category("HappyPath")]
  public void Find_TinyFileSet_PicksSmallestGeometry() {
    var sizes = new long[] { 64, 128, 256 };
    var g = TrsdosOptimizer.Find(sizes);
    // Tiny payload — smallest geometry that fits.
    Assert.That(g.Tracks, Is.EqualTo(35));
  }

  [Test, Category("HappyPath")]
  public void Find_MediumFileSet_EscalatesToLargerGeometry() {
    // 150 KB payload — 40×18 = ~184 KB raw, ~175 KB data area, ~15 % slack.
    // Should pick the 40-track DD geometry since it satisfies the data
    // requirement (single-density 35×10 = 89 KB is too small).
    var sizes = new long[] { 150 * 1024 };
    var g = TrsdosOptimizer.Find(sizes);
    Assert.That(g.DataBytes, Is.GreaterThanOrEqualTo(150 * 1024));
  }

  [Test, Category("HappyPath")]
  public void Find_LargeFileSet_PicksHighTrack() {
    // 300 KB payload only fits on 80×18 = 360 KB raw.
    var sizes = new long[] { 300 * 1024 };
    var g = TrsdosOptimizer.Find(sizes);
    Assert.That(g.DataBytes, Is.GreaterThanOrEqualTo(300 * 1024));
    Assert.That(g.Tracks, Is.EqualTo(80));
  }

  [Test, Category("HappyPath")]
  public void Find_EmptyFileSet_ReturnsSmallestGeometry() {
    var g = TrsdosOptimizer.Find([]);
    Assert.That(g.Tracks, Is.EqualTo(35));
  }

  [Test, Category("HappyPath")]
  public void GranulesPerCylinder_AutoDerivedFromSpt() {
    // 5 sectors per granule (Model III/4 convention).
    var dd = TrsdosOptimizer.Geometries[1]; // 40 × 18
    Assert.That(dd.GranulesPerCylinder, Is.EqualTo(3)); // 18/5 = 3.
    var sd = TrsdosOptimizer.Geometries[0]; // 35 × 10
    Assert.That(sd.GranulesPerCylinder, Is.EqualTo(2)); // 10/5 = 2.
  }
}
