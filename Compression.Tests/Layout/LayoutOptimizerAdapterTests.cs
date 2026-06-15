using Compression.Core.Layout;

namespace Compression.Tests.Layout;

/// <summary>
/// The one-call layout adapter must reproduce the generic optimiser's
/// slack+overhead minimum, prune null-overhead candidates, tie-break toward the
/// smaller size, and degrade gracefully when no overhead function is supplied.
/// </summary>
[TestFixture]
public class LayoutOptimizerAdapterTests {

  [Test, Category("Spec")]
  public void SelectAllocationUnit_NoOverhead_PicksLeastSlackSize() {
    // A single 1025-byte file: at 1024 it spills into a 2nd block (1023 slack);
    // at 2048 it wastes 1023; at 4096 it wastes 3071. Smallest slack ⇒ either
    // 1024 or 2048 ties at 1023 — tie-break picks the smaller (1024).
    var chosen = LayoutOptimizerAdapter.SelectAllocationUnit([1024, 2048, 4096], [1025]);
    Assert.That(chosen, Is.EqualTo(1024));
  }

  [Test, Category("Spec")]
  public void SelectAllocationUnit_OverheadDominates_PrefersLargerUnit() {
    // Big file-set with a steep per-size overhead that shrinks as the unit grows
    // (fewer table entries). The 4096 candidate must win.
    var files = Enumerable.Repeat(4096L, 100).ToList();
    var chosen = LayoutOptimizerAdapter.SelectAllocationUnit(
      [1024, 2048, 4096], files,
      fixedOverhead: bs => 100_000_000L / bs); // overhead falls with bigger blocks
    Assert.That(chosen, Is.EqualTo(4096));
  }

  [Test, Category("Spec")]
  public void SelectAllocationUnit_PrunesNullOverheadCandidates() {
    // 1024 is illegal (overhead null) and would otherwise have least slack; the
    // adapter must skip it and pick the next-best legal size.
    var chosen = LayoutOptimizerAdapter.SelectAllocationUnit(
      [1024, 2048, 4096], [1000],
      fixedOverhead: bs => bs == 1024 ? (long?)null : 0L);
    Assert.That(chosen, Is.EqualTo(2048));
  }

  [Test, Category("Boundary")]
  public void SelectAllocationUnit_EmptyFileset_ReturnsAValidCandidate() {
    var chosen = LayoutOptimizerAdapter.SelectAllocationUnit([512, 1024, 2048], []);
    Assert.That(chosen, Is.AnyOf(512, 1024, 2048));
  }

  [Test, Category("Sad")]
  public void SelectAllocationUnit_NoCandidates_Throws() {
    Assert.Throws<ArgumentException>(
      () => LayoutOptimizerAdapter.SelectAllocationUnit([], [10]));
  }

  [Test, Category("Spec")]
  public void SlackAt_MatchesManualComputation() {
    // 3000 bytes at 1024-byte units = 3 units (3072) ⇒ 72 slack bytes.
    Assert.That(LayoutOptimizerAdapter.SlackAt([3000], 1024), Is.EqualTo(72));
  }
}
