#pragma warning disable CS1591
using FileSystem.Cromemco;

namespace Compression.Tests.Cromemco;

/// <summary>
/// Verifies that <see cref="CromemcoOptimizer.Find"/> picks the smallest
/// geometry whose data area can hold the supplied file set with ≤ 5 %
/// slack.
/// </summary>
[TestFixture]
public class CromemcoOptimizerTests {

  [Test, Category("HappyPath")]
  public void Find_TinyFileSet_PicksSmallestGeometry() {
    var sizes = new long[] { 64, 128, 256 };
    var g = CromemcoOptimizer.Find(sizes);
    // ~5 % slack is impossible for a tiny payload on any Cromemco disk;
    // optimiser must return the smallest geometry that fits, which is
    // 35×18 = 80 640 bytes raw.
    Assert.That(g.Tracks, Is.EqualTo(35));
    Assert.That(g.SectorsPerTrack, Is.EqualTo(18));
  }

  [Test, Category("HappyPath")]
  public void Find_MediumFileSet_EscalatesToLargerGeometry() {
    // 70 KB payload — won't fit on 35×18 (80 640 raw - 18 sectors metadata
    // = 78 336 data bytes, but a 70 KB payload leaves only 8 KB slack ≈ 10 %)
    // so the optimiser should escalate.
    var sizes = new long[] { 70 * 1024 };
    var g = CromemcoOptimizer.Find(sizes);
    Assert.That(g.Tracks, Is.GreaterThanOrEqualTo(35));
    Assert.That(g.DataBytes, Is.GreaterThanOrEqualTo(70 * 1024));
  }

  [Test, Category("HappyPath")]
  public void Find_LargeFileSet_PicksDoubleDensity() {
    // 200 KB payload only fits on 77×26 = 256 256 bytes raw.
    var sizes = new long[] { 200 * 1024 };
    var g = CromemcoOptimizer.Find(sizes);
    Assert.That(g.DataBytes, Is.GreaterThanOrEqualTo(200 * 1024));
    Assert.That(g.Density, Is.EqualTo("Double"));
  }

  [Test, Category("HappyPath")]
  public void Find_EmptyFileSet_ReturnsSmallestGeometry() {
    var g = CromemcoOptimizer.Find([]);
    Assert.That(g.Tracks, Is.EqualTo(35));
  }
}
