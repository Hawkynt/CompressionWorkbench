#pragma warning disable CS1591
using FileSystem.Gfs1;

namespace Compression.Tests.Gfs1;

[TestFixture]
public class Gfs1OptimizerTests {

  [Test, Category("HappyPath")]
  public void Find_EmptyFileSet_ReturnsMinimumImage() {
    var p = Gfs1Optimizer.Find([]);
    Assert.That(p.BlockSize, Is.EqualTo(4096));
    Assert.That(p.JournalCount, Is.GreaterThanOrEqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Find_SmallFiles_FewerJournals_LargeFiles_MoreJournals() {
    var smallSet = Enumerable.Repeat(64L, 4).ToList();
    var bigSet = Enumerable.Repeat(64L * 1024 * 1024, 4).ToList();
    var small = Gfs1Optimizer.Find(smallSet);
    var big = Gfs1Optimizer.Find(bigSet);
    Assert.That(small.JournalCount, Is.LessThanOrEqualTo(big.JournalCount));
  }

  [Test, Category("HappyPath")]
  public void Find_LargeFiles_StaysWithin2xBloat() {
    var sizes = Enumerable.Repeat(1024L * 1024, 4).ToList();
    var p = Gfs1Optimizer.Find(sizes);
    var totalData = sizes.Sum();
    Assert.That(p.EstimatedImageBytes, Is.LessThanOrEqualTo(totalData * 2));
  }
}
