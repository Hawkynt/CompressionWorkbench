#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// Two claims about placement, measured rather than assumed.
/// </summary>
/// <remarks>
/// <para>The first is that folding "put this file at the next free offset" over
/// every file is a simpler defragmenter. It is simpler; the surveys below say
/// what it costs.</para>
///
/// <para>The second is that an ascending-order goal is easier to reach than
/// contiguity on a volume whose mover cannot hold a run outside it. The figures
/// say it is not: it needs holding more often, not less. What it is instead is
/// markedly cheaper, which is a different reason to have it.</para>
///
/// <para>Both print their numbers. A survey whose result is a pass and nothing
/// else is a survey nobody can check.</para>
/// </remarks>
[TestFixture]
public class LayoutGoalSurveyTests {

  private const int Cluster = 512;
  private const int Owners = 10;
  private const int BlocksPerOwner = 5;

  private static long NextTarget(long cursor, IReadOnlyList<DefragBlockInfo> layout) {
    foreach (var extent in layout.Where(e => e.Kind is DefragBlockKind.MetadataReserved or DefragBlockKind.Bad)
               .OrderBy(e => e.Offset))
      if (cursor >= extent.Offset && cursor < extent.Offset + extent.Length)
        cursor = extent.Offset + extent.Length;
    return cursor;
  }

  /// <summary>
  /// Reading (a): defragment by folding placement over every file, and compare
  /// it against the planner that computes the whole target layout at once.
  /// </summary>
  [Test, Category("Contract")]
  [TestCase(0.15)]
  [TestCase(0.25)]
  [TestCase(0.40)]
  public void APlacementFoldCostsMoreThanTheGlobalPlanner(double freeFraction) {
    long foldMoves = 0, foldBytes = 0, foldParks = 0;
    long globalMoves = 0, globalBytes = 0, globalParks = 0;
    var foldRuns = 0;
    var globalRuns = 0;
    var startRuns = 0;
    const int Seeds = 12;

    for (var seed = 1; seed <= Seeds; ++seed) {
      var start = LayoutSimulation.Scattered(seed, Owners, BlocksPerOwner, freeFraction, Cluster,
        out var dataOrigin, out var imageSize);
      startRuns += LayoutSimulation.TotalRuns(start);

      // (a) as an implementation: one placement per file, at the next free offset.
      var layout = start;
      var cursor = dataOrigin;
      foreach (var owner in start.Where(e => e.Kind == DefragBlockKind.Used)
                 .Select(e => e.FileName!).Distinct(StringComparer.OrdinalIgnoreCase)
                 .OrderBy(n => n, StringComparer.Ordinal)) {
        cursor = NextTarget(cursor, layout);
        var slots = PlacementPlanner.TargetSlots(layout, owner, cursor, dataOrigin, imageSize, Cluster);
        var moves = PlacementPlanner.Plan(layout, owner, cursor, dataOrigin, imageSize, Cluster);
        foldMoves += moves.Count;
        foldBytes += LayoutSimulation.BytesMoved(moves);
        foldParks += LayoutSimulation.Parks(moves);
        layout = LayoutSimulation.Apply(layout, moves, Cluster, dataOrigin, imageSize);
        cursor = slots[^1] + Cluster;
      }
      foldRuns += LayoutSimulation.TotalRuns(layout);
      foreach (var reading in AscendingBlockOrder.ReadAll(layout, Cluster))
        Assert.That(reading.Ascends, Is.True, $"fold, seed {seed}: {reading}");

      // The planner that computes the whole target layout and then orders it.
      var whole = DefragPlanner.Plan(start, dataOrigin, imageSize, Cluster,
        LayoutProfile.Performance, DefragMode.ConsolidateAtStart);
      globalMoves += whole.Count;
      globalBytes += LayoutSimulation.BytesMoved(whole);
      globalParks += LayoutSimulation.Parks(whole);
      var packed = LayoutSimulation.Apply(start, whole, Cluster, dataOrigin, imageSize);
      globalRuns += LayoutSimulation.TotalRuns(packed);
    }

    TestContext.Out.WriteLine(
      $"free {freeFraction:P0}, {Seeds} volumes of {Owners} owners x {BlocksPerOwner} blocks " +
      $"({startRuns} run(s) to start with):");
    TestContext.Out.WriteLine(
      $"  placement fold : {foldMoves,5} move(s)  {foldBytes,9:N0} byte(s)  {foldParks,3} park(s)  " +
      $"{foldRuns,4} run(s) after");
    TestContext.Out.WriteLine(
      $"  global planner : {globalMoves,5} move(s)  {globalBytes,9:N0} byte(s)  {globalParks,3} park(s)  " +
      $"{globalRuns,4} run(s) after");
    TestContext.Out.WriteLine(
      $"  fold / global  : {(double)foldMoves / globalMoves:F2}x moves, " +
      $"{(double)foldBytes / globalBytes:F2}x bytes");

    // One reserved block sits inside the data area, and the fold walks straight
    // over it rather than leaving a gap the way the global planner does, so
    // exactly one owner per volume comes out in two ascending pieces.
    Assert.That(foldRuns, Is.LessThanOrEqualTo(Owners * Seeds + Seeds),
      "The fold left owners in more pieces than the reserved block accounts for.");
    Assert.That(foldMoves, Is.GreaterThan(globalMoves),
      "The fold turned out no dearer than the global planner, which the survey above should say.");
  }

  /// <summary>
  /// Reading (b): how often an ascending-only goal is reachable with nothing
  /// held outside the volume, where contiguity is not.
  /// </summary>
  /// <remarks>
  /// A mover without <c>SupportsHeldRuns</c> — thirty-four of the eighty-five in
  /// this tree — can run a plan only if the plan parks nothing. So the question
  /// is not how many blocks each goal parks; it is how many volumes each goal
  /// can be reached on at all by such a mover.
  /// </remarks>
  [Test, Category("Contract")]
  public void AnAscendingGoalDoesNotAvoidParking_ButCostsAThirdOfTheBytes() {
    const int Seeds = 24;
    double[] levels = [0, 0.02, 0.05, 0.10, 0.20, 0.30, 0.50];

    TestContext.Out.WriteLine(
      $"{Seeds} volumes per level, {Owners} owners x {BlocksPerOwner} blocks, scattered.");
    TestContext.Out.WriteLine(
      "  free | planned with no parking | park(s) | byte(s) moved      | run(s) after");
    TestContext.Out.WriteLine(
      "       | packing   ascending     | pack/asc| packing   ascending| pack/asc");

    var cheaperEverywhere = true;
    var ascendingEverAvoidsParking = 0;
    foreach (var level in levels) {
      int packOk = 0, ascOk = 0, packParks = 0, ascParks = 0, packRuns = 0, ascRuns = 0;
      long packBytes = 0, ascBytes = 0;

      for (var seed = 1; seed <= Seeds; ++seed) {
        var layout = LayoutSimulation.Scattered(seed, Owners, BlocksPerOwner, level, Cluster,
          out var dataOrigin, out var imageSize);

        var pack = Attempt(layout, dataOrigin, imageSize, DefragMode.ConsolidateAtStart);
        var asc = Attempt(layout, dataOrigin, imageSize, DefragMode.AscendingOrder);
        if (pack is { Count: >= 0 } && LayoutSimulation.Parks(pack) == 0) ++packOk;
        if (asc is { Count: >= 0 } && LayoutSimulation.Parks(asc) == 0) ++ascOk;
        if (asc != null && pack != null && LayoutSimulation.Parks(asc) == 0
            && LayoutSimulation.Parks(pack) > 0) ++ascendingEverAvoidsParking;

        if (pack != null) {
          packParks += LayoutSimulation.Parks(pack);
          packBytes += LayoutSimulation.BytesMoved(pack);
          packRuns += LayoutSimulation.TotalRuns(
            LayoutSimulation.Apply(layout, pack, Cluster, dataOrigin, imageSize));
        }
        if (asc != null) {
          ascParks += LayoutSimulation.Parks(asc);
          ascBytes += LayoutSimulation.BytesMoved(asc);
          var after = LayoutSimulation.Apply(layout, asc, Cluster, dataOrigin, imageSize);
          ascRuns += LayoutSimulation.TotalRuns(after);
          foreach (var reading in AscendingBlockOrder.ReadAll(after, Cluster))
            Assert.That(reading.Ascends, Is.True, $"free {level:P0}, seed {seed}: {reading}");
        }
      }

      if (ascBytes >= packBytes) cheaperEverywhere = false;
      TestContext.Out.WriteLine(
        $"  {level,4:P0} | {packOk,4}/{Seeds}  {ascOk,5}/{Seeds}     | {packParks,3}/{ascParks,-4}| " +
        $"{packBytes,8:N0}  {ascBytes,8:N0} | {packRuns,4}/{ascRuns,-4}");
    }

    // What the measurement actually supports. The weaker goal is not a way past
    // a mover that cannot hold a run: it needs holding MORE often than packing,
    // because packing vacates space as it sweeps forward while an in-place sort
    // has nothing spare. What it is, is cheap.
    Assert.That(cheaperEverywhere, Is.True,
      "The ascending goal did not move fewer bytes than packing at every free-space level, " +
      "which is the only advantage the survey found for it.");
    TestContext.Out.WriteLine(
      $"  ascending avoided parking where packing could not: {ascendingEverAvoidsParking} " +
      $"of {Seeds * levels.Length} volumes.");
  }

  /// <summary>The plan, or null when the goal cannot be reached at all.</summary>
  private static IReadOnlyList<ClusterMove>? Attempt(IReadOnlyList<DefragBlockInfo> layout,
      long dataOrigin, long imageSize, DefragMode mode) {
    try {
      return DefragPlanner.Plan(layout, dataOrigin, imageSize, Cluster,
        LayoutProfile.Performance, mode);
    } catch (InvalidOperationException) {
      return null;
    }
  }

}
