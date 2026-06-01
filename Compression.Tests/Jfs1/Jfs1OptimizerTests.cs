#pragma warning disable CS1591
using FileSystem.Jfs1;

namespace Compression.Tests.Jfs1;

[TestFixture]
public class Jfs1OptimizerTests {

  [Test, Category("HappyPath")]
  public void Find_EmptyFileSet_ReturnsMinimumImage() {
    var p = Jfs1Optimizer.Find([]);
    Assert.That(p.BlockSize, Is.AnyOf(1024, 2048, 4096));
    Assert.That(p.AggregateBlockSize, Is.EqualTo(p.BlockSize));
  }

  [Test, Category("HappyPath")]
  public void Find_SmallFiles_PicksSmallerOrEqualBlock() {
    var smallSet = Enumerable.Repeat(64L, 8).ToList();
    var bigSet = Enumerable.Repeat(64L * 1024, 8).ToList();
    var small = Jfs1Optimizer.Find(smallSet);
    var big = Jfs1Optimizer.Find(bigSet);
    Assert.That(small.BlockSize, Is.LessThanOrEqualTo(big.BlockSize));
  }

  [Test, Category("HappyPath")]
  public void Find_LargeFiles_StaysWithin2xBloat() {
    var sizes = Enumerable.Repeat(1024L * 1024, 4).ToList();
    var p = Jfs1Optimizer.Find(sizes);
    var totalData = sizes.Sum();
    Assert.That(p.EstimatedImageBytes, Is.LessThanOrEqualTo(totalData * 2));
  }
}
