#pragma warning disable CS1591
using FileSystem.Efs;

namespace Compression.Tests.Efs;

[TestFixture]
public class EfsOptimizerTests {

  [Test, Category("HappyPath")]
  public void Find_EmptyFileSet_ReturnsMinimumImage() {
    var p = EfsOptimizer.Find([]);
    Assert.That(p.BlockSize, Is.EqualTo(512));
    Assert.That(p.CylinderGroupSize, Is.GreaterThan(0));
    Assert.That(p.EstimatedImageBytes, Is.GreaterThan(0));
  }

  [Test, Category("HappyPath")]
  public void Find_SmallFiles_PicksSmallerCylinderGroup() {
    var smallSet = Enumerable.Repeat(1024L, 8).ToList();
    var bigSet = Enumerable.Repeat(8L * 1024 * 1024, 4).ToList();

    var small = EfsOptimizer.Find(smallSet);
    var big = EfsOptimizer.Find(bigSet);

    // Small workload trends toward a smaller cylinder group size; big trends
    // toward a larger group to amortize metadata.
    Assert.That(small.CylinderGroupSize, Is.LessThanOrEqualTo(big.CylinderGroupSize));
  }

  [Test, Category("HappyPath")]
  public void Find_LargeFiles_RespectsBloatLimit() {
    var sizes = Enumerable.Repeat(1024L * 1024, 4).ToList();
    var p = EfsOptimizer.Find(sizes);
    var totalData = sizes.Sum();
    Assert.That(p.EstimatedImageBytes, Is.LessThanOrEqualTo(totalData * 2));
  }
}
