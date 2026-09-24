using Compression.Core.Layout;
using Compression.Registry;

namespace Compression.Tests.Layout;

[TestFixture]
public class DefragPlannerExecutorContractTests {
  [Test]
  public void FragmentedOwner_WithoutWholeChainOrIndependentRunRelinking_IsRejectedBeforeWrites() {
    using var image = NewImage();
    var mover = new RecordingMover();
    var moves = FragmentedMoves();

    Assert.That(
      () => DefragPlannerExecutor.Execute(image, new DefragOptions(), mover, moves, image.Length),
      Throws.InvalidOperationException);
    Assert.That(mover.MoveCount, Is.Zero);
    Assert.That(mover.RunRelinkCount, Is.Zero);
    Assert.That(mover.ScatteredRelinkCount, Is.Zero);
  }

  [Test]
  public void FragmentedOwner_WithIndependentRunRelinking_RepointsEveryRun() {
    using var image = NewImage();
    var mover = new RecordingMover { RepointRunsIndependently = true };
    var moves = FragmentedMoves();

    DefragPlannerExecutor.Execute(image, new DefragOptions(), mover, moves, image.Length);

    Assert.That(mover.MoveCount, Is.EqualTo(3));
    Assert.That(mover.RunRelinkCount, Is.EqualTo(3));
    Assert.That(mover.ScatteredRelinkCount, Is.Zero);
  }

  [Test]
  public void FragmentedOwner_WithScatteredRelinking_RepointsWholeAllocationOnce() {
    using var image = NewImage();
    var mover = new RecordingMover { ScatterRelink = true };
    var moves = FragmentedMoves();
    DefragBlockInfo[] layout = [
      new(0, 1, DefragBlockKind.Used, "A"),
      new(2, 1, DefragBlockKind.Used, "A"),
      new(4, 1, DefragBlockKind.Used, "A"),
    ];

    DefragPlannerExecutor.Execute(image, new DefragOptions(), mover, moves, image.Length, layout: layout);

    Assert.That(mover.MoveCount, Is.EqualTo(3));
    Assert.That(mover.RunRelinkCount, Is.Zero);
    Assert.That(mover.ScatteredRelinkCount, Is.EqualTo(1));
    Assert.That(mover.LastOldBlocks, Is.EqualTo(new long[] { 0, 2, 4 }));
    Assert.That(mover.LastNewBlocks, Is.EqualTo(new long[] { 8, 9, 10 }));
  }

  [Test]
  public void FullVolumeCycle_UsesHeldRun_WhenMoverSupportsIt() {
    using var image = new MemoryStream([0xA1, 0xB2], writable: true);
    var mover = new RecordingMover { HeldRuns = true, RepointRunsIndependently = true };
    ClusterMove[] moves = [
      new(0, -1, 1, "A") { Staging = DefragStaging.Park, StagingSlot = 0 },
      new(1, 0, 1, "B"),
      new(0, 1, 1, "A") { Staging = DefragStaging.Unpark, StagingSlot = 0 },
    ];

    DefragPlannerExecutor.Execute(image, new DefragOptions(), mover, moves, image.Length);

    Assert.That(image.ToArray(), Is.EqualTo(new byte[] { 0xB2, 0xA1 }));
    Assert.That(mover.RunRelinkCount, Is.EqualTo(2));
    Assert.That(mover.ReleaseOldSpaceFlags, Is.EqualTo(new[] { true, false }));
  }

  [Test]
  public void FullVolumeCycle_WithoutHeldRunSupport_IsRejectedBeforeWrites() {
    using var image = new MemoryStream([0xA1, 0xB2], writable: true);
    var mover = new RecordingMover { RepointRunsIndependently = true };
    ClusterMove[] moves = [
      new(0, -1, 1, "A") { Staging = DefragStaging.Park, StagingSlot = 0 },
      new(1, 0, 1, "B"),
      new(0, 1, 1, "A") { Staging = DefragStaging.Unpark, StagingSlot = 0 },
    ];

    Assert.That(
      () => DefragPlannerExecutor.Execute(image, new DefragOptions(), mover, moves, image.Length),
      Throws.InvalidOperationException);
    Assert.That(image.ToArray(), Is.EqualTo(new byte[] { 0xA1, 0xB2 }));
    Assert.That(mover.MoveCount, Is.Zero);
  }

  [Test]
  public void ContentGuard_RestoresSnapshotBeforeFallback_AfterMetadataUpdateFailure() {
    using var image = new MemoryStream("original"u8.ToArray(), writable: true);
    var fallbackSawOriginal = false;

    DefragContentGuard.RunOrRebuild(
      image,
      stream => [ReadAll(stream)],
      inPlace: () => {
        image.Position = 0;
        image.Write("damaged!"u8);
        throw new InvalidOperationException("metadata update failed");
      },
      rebuild: () => {
        image.Position = 0;
        fallbackSawOriginal = ReadAll(image).SequenceEqual("original"u8.ToArray());
      });

    Assert.That(fallbackSawOriginal, Is.True);
    Assert.That(image.ToArray(), Is.EqualTo("original"u8.ToArray()));
  }

  private static MemoryStream NewImage() {
    var bytes = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    return new MemoryStream(bytes, writable: true);
  }

  private static ClusterMove[] FragmentedMoves() => [
    new(0, 8, 1, "A"),
    new(2, 9, 1, "A"),
    new(4, 10, 1, "A"),
  ];

  private static byte[] ReadAll(Stream stream) {
    stream.Position = 0;
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private sealed class RecordingMover : IFilesystemBlockMover {
    public bool ScatterRelink { get; init; }
    public bool RepointRunsIndependently { get; init; }
    public bool HeldRuns { get; init; }

    public int MoveCount { get; private set; }
    public int RunRelinkCount { get; private set; }
    public int ScatteredRelinkCount { get; private set; }
    public List<bool> ReleaseOldSpaceFlags { get; } = [];
    public IReadOnlyList<long>? LastOldBlocks { get; private set; }
    public IReadOnlyList<long>? LastNewBlocks { get; private set; }

    public int AllocationBlockSize => 1;
    public bool SupportsScatteredRelink => this.ScatterRelink;
    bool IFilesystemBlockMover.RepointsRunsIndependently => this.RepointRunsIndependently;
    public bool SupportsHeldRuns => this.HeldRuns;

    public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
      ++this.MoveCount;
      var buffer = new byte[checked((int)length)];
      image.Position = srcOffset;
      image.ReadExactly(buffer);
      image.Position = dstOffset;
      image.Write(buffer);
      if (!zeroSource) return;
      image.Position = srcOffset;
      image.Write(new byte[buffer.Length]);
    }

    public void UpdateAllocationAfterMove(
        Stream image,
        string fileName,
        long oldOffset,
        long newOffset,
        long length) {
      ++this.RunRelinkCount;
      this.ReleaseOldSpaceFlags.Add(true);
    }

    public void UpdateAllocationAfterMove(
        Stream image,
        string fileName,
        long oldOffset,
        long newOffset,
        long length,
        bool releaseOldSpace) {
      ++this.RunRelinkCount;
      this.ReleaseOldSpaceFlags.Add(releaseOldSpace);
    }

    public void UpdateAllocationScattered(
        Stream image,
        string fileName,
        IReadOnlyList<long> oldBlockOffsets,
        IReadOnlyList<long> newBlockOffsets,
        IReadOnlySet<long>? blocksLiveElsewhere) {
      ++this.ScatteredRelinkCount;
      this.LastOldBlocks = oldBlockOffsets.ToArray();
      this.LastNewBlocks = newBlockOffsets.ToArray();
    }
  }
}
