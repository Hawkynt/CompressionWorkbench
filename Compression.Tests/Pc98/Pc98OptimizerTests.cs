#pragma warning disable CS1591
using FileSystem.Pc98;

namespace Compression.Tests.Pc98;

/// <summary>
/// Verifies <see cref="Pc98Optimizer.Find"/> picks the smallest
/// sectors-per-cluster value that fits the supplied file set with ≤ 5 %
/// slack.
/// </summary>
[TestFixture]
public class Pc98OptimizerTests {

  [Test, Category("HappyPath")]
  public void Find_TinyFiles_PicksSpc1() {
    var sizes = new long[] { 100, 200, 300 };
    var l = Pc98Optimizer.Find(sizes);
    Assert.That(l.BytesPerSector, Is.EqualTo(512));
    Assert.That(l.SectorsPerCluster, Is.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Find_AlignedPayload_PicksSpc1() {
    // Three 510-byte files → 3 clusters × 512 = 1536, slack ≈ 0.4 %.
    var sizes = new long[] { 510, 510, 510 };
    var l = Pc98Optimizer.Find(sizes);
    Assert.That(l.SectorsPerCluster, Is.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Find_LargeFile_TotalSectorsCoversPayload() {
    var sizes = new long[] { 100 * 1024 };
    var l = Pc98Optimizer.Find(sizes);
    Assert.That(l.TotalBytes, Is.GreaterThanOrEqualTo(100 * 1024));
  }

  [Test, Category("HappyPath")]
  public void Find_EmptyFileSet_ReturnsMinimal() {
    var l = Pc98Optimizer.Find([]);
    Assert.That(l.SectorsPerCluster, Is.EqualTo(1));
    Assert.That(l.BytesPerSector, Is.EqualTo(512));
  }
}
