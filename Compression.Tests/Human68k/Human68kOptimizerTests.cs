#pragma warning disable CS1591
using FileSystem.Human68k;

namespace Compression.Tests.Human68k;

/// <summary>
/// Verifies <see cref="Human68kOptimizer.Find"/> picks the smallest
/// sectors-per-cluster value that fits the supplied file set with
/// ≤ 5 % wasted slack.
/// </summary>
[TestFixture]
public class Human68kOptimizerTests {

  [Test, Category("HappyPath")]
  public void Find_TinyFiles_PicksSpc1() {
    // 100-byte file × 3 — cluster=512 already wastes a lot, but SPC=1 is best.
    var sizes = new long[] { 100, 200, 300 };
    var l = Human68kOptimizer.Find(sizes);
    Assert.That(l.BytesPerSector, Is.EqualTo(512));
    Assert.That(l.SectorsPerCluster, Is.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Find_AlignedPayload_PicksSpc1() {
    // Three files each ~510 bytes — total 1530, with SPC=1 (cluster=512) need 3
    // clusters = 1536, slack ≈ 0.4 %. SPC=1 is optimal.
    var sizes = new long[] { 510, 510, 510 };
    var l = Human68kOptimizer.Find(sizes);
    Assert.That(l.SectorsPerCluster, Is.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Find_LargeFile_TotalSectorsCoversPayload() {
    var sizes = new long[] { 100 * 1024 }; // 100 KB.
    var l = Human68kOptimizer.Find(sizes);
    Assert.That(l.TotalBytes, Is.GreaterThanOrEqualTo(100 * 1024));
  }

  [Test, Category("HappyPath")]
  public void Find_EmptyFileSet_ReturnsMinimal() {
    var l = Human68kOptimizer.Find([]);
    Assert.That(l.SectorsPerCluster, Is.EqualTo(1));
    Assert.That(l.BytesPerSector, Is.EqualTo(512));
  }
}
