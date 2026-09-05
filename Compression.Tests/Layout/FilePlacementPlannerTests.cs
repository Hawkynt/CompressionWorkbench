#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// Putting one named owner at one chosen offset: where it lands, what it does
/// to everything in the way, and what it refuses.
/// </summary>
[TestFixture]
public class FilePlacementPlannerTests {

  private const int Cluster = 512;
  private const int Owners = 8;
  private const int BlocksPerOwner = 6;

  private static List<DefragBlockInfo> Volume(out long dataOrigin, out long imageSize,
      double freeFraction = 0.30, int seed = 4711)
    => LayoutSimulation.Scattered(seed, Owners, BlocksPerOwner, freeFraction, Cluster,
      out dataOrigin, out imageSize);

  /// <summary>Where the reserved block inside the data area sits.</summary>
  private static long ReservedInside(IReadOnlyList<DefragBlockInfo> layout)
    => layout.Single(e => e.Kind == DefragBlockKind.MetadataReserved && e.Offset > 0).Offset;

  [Test, Category("HappyPath")]
  public void AnOwnerLandsExactlyWhereItWasAsked_AndComesOutInOneRun() {
    var layout = Volume(out var dataOrigin, out var imageSize);
    const string owner = "F0003.BIN";

    var moves = PlacementPlanner.Plan(layout, owner, dataOrigin, dataOrigin, imageSize, Cluster);
    Assert.That(moves, Is.Not.Empty, "A scattered owner was left where it was.");

    var after = LayoutSimulation.Apply(layout, moves, Cluster, dataOrigin, imageSize);
    var placed = after.Where(e => e.Kind == DefragBlockKind.Used
      && string.Equals(e.FileName, owner, StringComparison.OrdinalIgnoreCase)).ToList();

    Assert.That(placed[0].Offset, Is.EqualTo(dataOrigin), $"'{owner}' does not start where it was asked to.");
    var reading = AscendingBlockOrder.Read(after, owner, Cluster);
    Assert.That(reading.Ascends, Is.True, reading.ToString());
    Assert.That(reading.Runs, Is.EqualTo(1), $"Nothing was in the way, yet {reading}.");
    Assert.That(reading.Blocks, Is.EqualTo(BlocksPerOwner));
  }

  [Test, Category("HappyPath")]
  public void AnOwnerSteppedOverAReservedRegion_ComesOutSplitButStillAscending() {
    var layout = Volume(out var dataOrigin, out var imageSize);
    const string owner = "F0005.BIN";

    // Two clusters short of the reserved block, so the owner has to step over it.
    var target = ReservedInside(layout) - 2L * Cluster;

    var slots = PlacementPlanner.TargetSlots(layout, owner, target, dataOrigin, imageSize, Cluster);
    TestContext.Out.WriteLine($"slots: {string.Join(", ", slots)}");

    var moves = PlacementPlanner.Plan(layout, owner, target, dataOrigin, imageSize, Cluster);
    var after = LayoutSimulation.Apply(layout, moves, Cluster, dataOrigin, imageSize);

    var placed = after.Where(e => e.Kind == DefragBlockKind.Used
      && string.Equals(e.FileName, owner, StringComparison.OrdinalIgnoreCase)).ToList();
    Assert.That(placed[0].Offset, Is.EqualTo(target), $"'{owner}' does not start where it was asked to.");

    var reading = AscendingBlockOrder.Read(after, owner, Cluster);
    Assert.That(reading.Runs, Is.EqualTo(2),
      $"A reserved block inside the span should split the owner in two, but {reading}.");
    Assert.That(reading.Ascends, Is.True,
      $"The split lost the one promise that survives it: {reading}.");
    Assert.That(reading.Blocks, Is.EqualTo(BlocksPerOwner));
  }

  [Test, Category("HappyPath")]
  public void AnOwnerAlreadyAtItsTarget_IsNotMovedAtAll() {
    var layout = Volume(out var dataOrigin, out var imageSize);
    const string owner = "F0001.BIN";

    var first = PlacementPlanner.Plan(layout, owner, dataOrigin, dataOrigin, imageSize, Cluster);
    var after = LayoutSimulation.Apply(layout, first, Cluster, dataOrigin, imageSize);

    var again = PlacementPlanner.Plan(after, owner, dataOrigin, dataOrigin, imageSize, Cluster);
    Assert.That(again, Is.Empty, "An owner that already sits at its target was moved anyway.");
  }

  [Test, Category("EdgeCase")]
  public void EverythingInTheWayEndsUpSomewhereElse_AndNothingIsWrittenOverSomethingLive() {
    var layout = Volume(out var dataOrigin, out var imageSize);
    const string owner = "F0007.BIN";
    var target = dataOrigin + 4L * Cluster;

    var moves = PlacementPlanner.Plan(layout, owner, target, dataOrigin, imageSize, Cluster);

    // Apply() is the oracle: it fails the moment a move writes onto a block
    // that has not been read out yet, which is the failure a move count hides.
    var after = LayoutSimulation.Apply(layout, moves, Cluster, dataOrigin, imageSize);

    var before = layout.Where(e => e.Kind == DefragBlockKind.Used)
      .GroupBy(e => e.FileName!, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key, g => g.Sum(e => e.Length), StringComparer.OrdinalIgnoreCase);
    foreach (var (name, bytes) in before) {
      var now = after.Where(e => e.Kind == DefragBlockKind.Used
        && string.Equals(e.FileName, name, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Length);
      Assert.That(now, Is.EqualTo(bytes), $"'{name}' lost blocks to the placement.");
    }
  }

  [Test, Category("ErrorHandling")]
  public void ARequestThatCannotBeHonoured_IsRefusedBeforeAnythingMoves() {
    var layout = Volume(out var dataOrigin, out var imageSize);

    var cases = new (string What, string Owner, long Target)[] {
      ("an owner the volume does not hold", "NOSUCH.BIN", dataOrigin),
      ("a target below the data area",      "F0000.BIN",  0),
      ("a target past the end",             "F0000.BIN",  imageSize),
      ("a target off the cluster grid",     "F0000.BIN",  dataOrigin + 3),
      ("a target inside a reserved region", "F0000.BIN",  ReservedInside(layout)),
      ("no room left above the target",     "F0000.BIN",  imageSize - 2L * Cluster),
    };

    foreach (var (what, owner, target) in cases) {
      var thrown = Assert.Throws<InvalidOperationException>(
        () => PlacementPlanner.Plan(layout, owner, target, dataOrigin, imageSize, Cluster),
        $"{what} was not refused.");
      TestContext.Out.WriteLine($"{what}: {thrown!.Message}");
      Assert.That(thrown.Message, Does.Contain("Nothing was changed"),
        $"{what} was refused without saying the volume is untouched.");
    }
  }

  /// <summary>
  /// A reserved region that begins part-way into the target's first cluster
  /// used to make the owner step over it and start above — somewhere the caller
  /// never asked for, reported as though it had been honoured.
  /// </summary>
  [Test, Category("ErrorHandling")]
  public void ATargetWhoseFirstClusterIsPartlyReserved_IsRefusedRatherThanQuietlyMovedUp() {
    const long Origin = Cluster;
    const long ImageSize = Cluster * 20L;
    var target = Origin + 4L * Cluster;

    List<DefragBlockInfo> Volume(long badAt) => [
      new(0, Cluster, DefragBlockKind.MetadataReserved, "superblock"),
      new(Origin, Cluster, DefragBlockKind.Used, "A"),
      new(Origin + Cluster, Cluster, DefragBlockKind.Used, "A"),
      new(badAt, 200, DefragBlockKind.Bad, null),
    ];

    // Begins 100 bytes into the target's own first cluster.
    var thrown = Assert.Throws<InvalidOperationException>(
      () => PlacementPlanner.Plan(Volume(target + 100), "A", target, Origin, ImageSize, Cluster));
    TestContext.Out.WriteLine(thrown!.Message);
    Assert.That(thrown.Message, Does.Contain("Nothing was changed"));

    // The same volume with the bad region a cluster further on places fine, so
    // the refusal above is caused by the overlap and nothing incidental.
    var moves = PlacementPlanner.Plan(Volume(target + Cluster + 100), "A", target, Origin, ImageSize, Cluster);
    var after = LayoutSimulation.Apply(Volume(target + Cluster + 100), moves, Cluster, Origin, ImageSize);
    var placed = after.First(e => e.Kind == DefragBlockKind.Used
      && string.Equals(e.FileName, "A", StringComparison.OrdinalIgnoreCase));
    Assert.That(placed.Offset, Is.EqualTo(target));
    Assert.That(AscendingBlockOrder.Holds(after, "A", Cluster), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void AVolumeWithNowhereToPutWhatIsInTheWay_IsRefused() {
    // Full: every block of the data area is live, so an eviction has nowhere to go.
    var layout = LayoutSimulation.Scattered(7, owners: 4, blocksPerOwner: 4, freeFraction: 0,
      Cluster, out var dataOrigin, out var imageSize);

    var thrown = Assert.Throws<InvalidOperationException>(
      () => PlacementPlanner.Plan(layout, "F0003.BIN", dataOrigin, dataOrigin, imageSize, Cluster,
        allowMemoryStaging: false));
    TestContext.Out.WriteLine(thrown!.Message);
    Assert.That(thrown.Message, Does.Contain("Nothing was changed"));
  }
}
