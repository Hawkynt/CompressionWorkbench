#pragma warning disable CS1591
using Compression.Core.DiskImage;

namespace Compression.Tests.DiskImage;

[TestFixture]
public class ClusterAllocatorTests {

  [Test]
  public void Allocate_ContiguousRun_GrabsFromStart() {
    var a = new ClusterAllocator(100);
    Assert.That(a.AllocateRun(5), Is.EqualTo(0));
    Assert.That(a.AllocateRun(3), Is.EqualTo(5));
    Assert.That(a.FreeCount, Is.EqualTo(92));
  }

  [Test]
  public void Free_ReturnsClustersToPool() {
    var a = new ClusterAllocator(100);
    var start = a.AllocateRun(10);
    a.FreeRange(start, 10);
    Assert.That(a.FreeCount, Is.EqualTo(100));
  }

  [Test]
  public void AllocateRun_WhenFragmented_TriggersFastDefrag() {
    var a = new ClusterAllocator(10);
    a.AllocateRun(1);    // cluster 0 used
    a.AllocateRun(1);    // 1 used
    a.AllocateRun(1);    // 2 used
    a.AllocateRun(1);    // 3 used
    a.AllocateRun(1);    // 4 used
    a.FreeRange(1, 1);   // free cluster 1
    a.FreeRange(3, 1);   // free cluster 3
    // Bitmap: [U][F][U][F][U][F][F][F][F][F]
    // FreeCount = 7, largest contiguous run = 5 (clusters 5..9).
    // Requesting 6 needs consolidation.

    var moves = new List<(int From, int To)>();
    var start = a.AllocateRun(6, (from, to) => { moves.Add((from, to)); return true; });
    Assert.That(start, Is.GreaterThanOrEqualTo(0), "allocation should succeed via fast-defrag");
    Assert.That(moves, Is.Not.Empty, "fast-defrag should have relocated at least one cluster");
  }

  [Test]
  public void AllocateRun_WhenNoSpace_ReturnsMinusOne() {
    var a = new ClusterAllocator(5);
    a.AllocateRun(5);
    Assert.That(a.AllocateRun(1), Is.EqualTo(-1));
  }

  [Test]
  public void AllocateRun_WithoutCallback_SkipsDefrag() {
    var a = new ClusterAllocator(10);
    a.AllocateRun(1); a.AllocateRun(1); a.AllocateRun(1); a.AllocateRun(1); a.AllocateRun(1);
    a.FreeRange(1, 1);
    a.FreeRange(3, 1);
    // Without a relocate callback the allocator can't defrag.
    Assert.That(a.AllocateRun(6), Is.EqualTo(-1));
  }

  [Test]
  public void ReserveMarksSystemAreasUsed() {
    var a = new ClusterAllocator(100);
    a.ReserveRange(0, 10);
    Assert.That(a.AllocateRun(1), Is.EqualTo(10));
    Assert.That(a.FreeCount, Is.EqualTo(89));
  }
}
