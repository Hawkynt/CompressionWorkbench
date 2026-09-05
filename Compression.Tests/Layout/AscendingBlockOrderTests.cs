#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// The ordering property itself, and the mode that exists to reach it: a
/// sequential read of any owner never seeks backwards.
/// </summary>
[TestFixture]
public class AscendingBlockOrderTests {

  private const int Cluster = 512;

  [Test, Category("HappyPath")]
  public void TheReadingNamesTheBlockThatGoesBackwards() {
    var layout = new List<DefragBlockInfo> {
      new(1000, 512, DefragBlockKind.Used, "A"),
      new(3000, 512, DefragBlockKind.Used, "A"),
      new(2000, 512, DefragBlockKind.Used, "A"),   // logical block 2, below block 1
      new(4000, 512, DefragBlockKind.Used, "B"),
      new(4512, 512, DefragBlockKind.Used, "B"),
    };

    var a = AscendingBlockOrder.Read(layout, "A", Cluster);
    Assert.That(a.Ascends, Is.False);
    Assert.That(a.Blocks, Is.EqualTo(3));
    Assert.That(a.Runs, Is.EqualTo(3));
    Assert.That(a.Descents, Is.EqualTo(1));
    Assert.That(a.FirstDescentIndex, Is.EqualTo(2));
    Assert.That(a.FirstDescentFrom, Is.EqualTo(3000));
    Assert.That(a.FirstDescentTo, Is.EqualTo(2000));
    TestContext.Out.WriteLine(a.ToString());

    var b = AscendingBlockOrder.Read(layout, "B", Cluster);
    Assert.That(b.Ascends, Is.True);
    Assert.That(b.Runs, Is.EqualTo(1), "Two blocks that touch are one run.");
    Assert.That(AscendingBlockOrder.HoldsForAll(layout, Cluster), Is.False);
    Assert.That(AscendingBlockOrder.Violations(layout, Cluster).Select(v => v.Owner),
      Is.EqualTo(new[] { "A" }));
  }

  /// <summary>
  /// Address order would make the property vacuous. It is about the owner's own
  /// order, which is the order the extent map walks the chain in.
  /// </summary>
  [Test, Category("EdgeCase")]
  public void TheReadingFollowsTheChain_NotTheAddresses() {
    var descending = new List<DefragBlockInfo> {
      new(9000, 512, DefragBlockKind.Used, "A"),
      new(1000, 512, DefragBlockKind.Used, "A"),
    };
    Assert.That(AscendingBlockOrder.Holds(descending, "A", Cluster), Is.False,
      "An owner whose chain runs backwards was read as if it ascended.");
  }

  [Test, Category("HappyPath")]
  [TestCase(0.05)]
  [TestCase(0.20)]
  [TestCase(0.50)]
  public void TheAscendingModeMakesEveryOwnerReadForwards(double freeFraction) {
    var layout = LayoutSimulation.Scattered(20260905, owners: 10, blocksPerOwner: 5, freeFraction,
      Cluster, out var dataOrigin, out var imageSize);
    Assert.That(AscendingBlockOrder.HoldsForAll(layout, Cluster), Is.False,
      "The scattered volume happened to be in order already, so this proves nothing.");

    var moves = DefragPlanner.Plan(layout, dataOrigin, imageSize, Cluster,
      LayoutProfile.Performance, DefragMode.AscendingOrder);

    var after = LayoutSimulation.Apply(layout, moves, Cluster, dataOrigin, imageSize);
    foreach (var reading in AscendingBlockOrder.ReadAll(after, Cluster))
      Assert.That(reading.Ascends, Is.True, reading.ToString());

    TestContext.Out.WriteLine(
      $"free {freeFraction:P0}: {moves.Count} move(s), {LayoutSimulation.BytesMoved(moves):N0} byte(s), " +
      $"{LayoutSimulation.Parks(moves)} park(s), " +
      $"{LayoutSimulation.TotalRuns(layout)} run(s) -> {LayoutSimulation.TotalRuns(after)}");
  }

  /// <summary>
  /// The claim worth having: with room to work in, ascending order is reached
  /// by moving blocks forward into free clusters and nothing is ever lifted out
  /// of the volume — while packing the same volume is a cycle that has to be
  /// broken.
  /// </summary>
  [Test, Category("HappyPath")]
  public void WithRoomToWorkIn_AscendingOrderNeverHoldsARunOutsideTheVolume() {
    for (var seed = 1; seed <= 12; ++seed) {
      var layout = LayoutSimulation.Scattered(seed, owners: 10, blocksPerOwner: 5, freeFraction: 0.25,
        Cluster, out var dataOrigin, out var imageSize);

      var moves = DefragPlanner.Plan(layout, dataOrigin, imageSize, Cluster,
        LayoutProfile.Performance, DefragMode.AscendingOrder, allowMemoryStaging: false);
      Assert.That(LayoutSimulation.Parks(moves), Is.Zero,
        $"seed {seed}: the ascending mode held a run outside the volume.");

      var after = LayoutSimulation.Apply(layout, moves, Cluster, dataOrigin, imageSize);
      foreach (var reading in AscendingBlockOrder.ReadAll(after, Cluster))
        Assert.That(reading.Ascends, Is.True, $"seed {seed}: {reading}");
    }
  }

  /// <summary>
  /// The invariant is not only the new mode's promise. An ordinary
  /// defragmentation has to satisfy it too, or a partial success means nothing.
  /// </summary>
  [Test, Category("HappyPath")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.AscendingOrder)]
  public void EveryOwnerReadsForwardsAfterAnOrdinaryDefragmentation(DefragMode mode) {
    for (var seed = 1; seed <= 8; ++seed) {
      var layout = LayoutSimulation.Scattered(seed, owners: 9, blocksPerOwner: 4, freeFraction: 0.25,
        Cluster, out var dataOrigin, out var imageSize);

      var moves = DefragPlanner.Plan(layout, dataOrigin, imageSize, Cluster,
        LayoutProfile.Performance, mode);
      var after = LayoutSimulation.Apply(layout, moves, Cluster, dataOrigin, imageSize);

      foreach (var reading in AscendingBlockOrder.ReadAll(after, Cluster))
        Assert.That(reading.Ascends, Is.True, $"seed {seed}, {mode}: {reading}");
    }
  }

  /// <summary>
  /// The honest limit, written down as a test: making a descending pair ascend
  /// is a swap, and a swap needs somewhere to put a block. A full volume has
  /// nowhere, so the weaker goal is no easier there than the stronger one.
  /// </summary>
  [Test, Category("EdgeCase")]
  public void OnAFullVolumeTheWeakerGoalIsNoEasier_AndSaysSoRatherThanPretending() {
    var layout = LayoutSimulation.Scattered(3, owners: 6, blocksPerOwner: 4, freeFraction: 0,
      Cluster, out var dataOrigin, out var imageSize);

    var thrown = Assert.Throws<InvalidOperationException>(() => DefragPlanner.Plan(
      layout, dataOrigin, imageSize, Cluster, LayoutProfile.Performance, DefragMode.AscendingOrder,
      allowMemoryStaging: false));
    TestContext.Out.WriteLine(thrown!.Message);

    // Given somewhere to put a block it works, and it still costs less than
    // packing the same volume does.
    var ascending = DefragPlanner.Plan(layout, dataOrigin, imageSize, Cluster,
      LayoutProfile.Performance, DefragMode.AscendingOrder);
    var packed = DefragPlanner.Plan(layout, dataOrigin, imageSize, Cluster,
      LayoutProfile.Performance, DefragMode.ConsolidateAtStart);

    TestContext.Out.WriteLine(
      $"full volume: ascending {ascending.Count} move(s) / {LayoutSimulation.Parks(ascending)} park(s), " +
      $"packing {packed.Count} move(s) / {LayoutSimulation.Parks(packed)} park(s)");

    Assert.That(LayoutSimulation.Parks(ascending), Is.GreaterThan(0),
      "A full volume was reordered with nothing held, so the refusal above was avoidable.");
    Assert.That(LayoutSimulation.Parks(packed), Is.GreaterThan(0));

    var after = LayoutSimulation.Apply(layout, ascending, Cluster, dataOrigin, imageSize);
    foreach (var reading in AscendingBlockOrder.ReadAll(after, Cluster))
      Assert.That(reading.Ascends, Is.True, reading.ToString());
  }
}
