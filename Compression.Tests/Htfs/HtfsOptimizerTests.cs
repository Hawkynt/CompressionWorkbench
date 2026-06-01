#pragma warning disable CS1591
using FileSystem.Htfs;

namespace Compression.Tests.Htfs;

[TestFixture]
public class HtfsOptimizerTests {

  [Test, Category("HappyPath")]
  public void Find_EmptyFileSet_ReturnsMinimumImage() {
    var p = HtfsOptimizer.Find([]);
    Assert.That(p.BlockSize, Is.AnyOf(512, 1024, 2048));
    Assert.That(p.InodeCount, Is.GreaterThan(0));
  }

  [Test, Category("HappyPath")]
  public void Find_SmallFiles_PicksSmallerOrEqualBlock() {
    var smallSet = Enumerable.Repeat(64L, 8).ToList();
    var bigSet = Enumerable.Repeat(64L * 1024, 8).ToList();
    var small = HtfsOptimizer.Find(smallSet);
    var big = HtfsOptimizer.Find(bigSet);
    Assert.That(small.BlockSize, Is.LessThanOrEqualTo(big.BlockSize));
  }

  [Test, Category("HappyPath")]
  public void Find_LargeFiles_StaysWithin2xBloat() {
    var sizes = Enumerable.Repeat(1024L * 1024, 4).ToList();
    var p = HtfsOptimizer.Find(sizes);
    var totalData = sizes.Sum();
    Assert.That(p.EstimatedImageBytes, Is.LessThanOrEqualTo(totalData * 2));
  }
}
