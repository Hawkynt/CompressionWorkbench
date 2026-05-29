#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

/// <summary>
/// Tests for the planner-driven in-place FAT defragmenter. Verifies that the
/// <see cref="DefragPlanner"/> + <see cref="FatBlockMover"/> pipeline correctly
/// moves cluster extents, patches FAT chains and directory entries, and produces
/// a valid, less-fragmented image.
/// </summary>
[TestFixture]
public class FatPlannerDefragTests {

  // ── Helpers ────────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a FAT12 floppy image with several files, then removes some to
  /// create fragmentation. Returns the fragmented image as a MemoryStream.
  /// </summary>
  private static MemoryStream BuildFragmentedImage() {
    // Create image with 6 files.
    var w = new FatWriter();
    w.AddFile("A.TXT", Encoding.ASCII.GetBytes("Alpha content here!"));
    w.AddFile("B.TXT", Encoding.ASCII.GetBytes("Beta content file."));
    w.AddFile("C.TXT", Encoding.ASCII.GetBytes("Charlie data block!"));
    w.AddFile("D.TXT", new byte[600]); // large enough to span multiple clusters
    w.AddFile("E.TXT", Encoding.ASCII.GetBytes("Echo short."));
    w.AddFile("F.TXT", new byte[1200]); // even larger
    var image = w.Build();

    // Remove B and D to create holes in the cluster chain.
    FatRemover.Remove(image, "B.TXT");
    FatRemover.Remove(image, "D.TXT");

    var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    return ms;
  }

  /// <summary>
  /// Builds a FAT12 floppy image with 5+ files, no removals.
  /// Used when we just need a non-fragmented baseline.
  /// </summary>
  private static MemoryStream BuildCleanImage() {
    var w = new FatWriter();
    w.AddFile("ONE.TXT", "First"u8.ToArray());
    w.AddFile("TWO.TXT", "Second"u8.ToArray());
    w.AddFile("THREE.TXT", "Third file data"u8.ToArray());
    w.AddFile("FOUR.TXT", new byte[256]);
    w.AddFile("FIVE.TXT", "Five!"u8.ToArray());
    var image = w.Build();
    var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    return ms;
  }

  private static Dictionary<string, byte[]> ExtractAll(MemoryStream ms) {
    ms.Position = 0;
    var reader = new FatReader(ms);
    return reader.Entries
      .Where(e => !e.IsDirectory)
      .ToDictionary(e => e.Name, e => reader.Extract(e));
  }

  private static int CountFragments(MemoryStream ms) {
    ms.Position = 0;
    var extents = FatExtentMap.Enumerate(ms).ToList();
    // Count Used extents. A perfectly defragmented image has one Used extent per file.
    return extents.Count(e => e.Kind == DefragBlockKind.Used);
  }

  // ── Tests ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void PlannerDefrag_Performance_PreservesAllFiles() {
    using var ms = BuildFragmentedImage();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
    });

    var after = ExtractAll(ms);

    // All surviving files must still be present and correct.
    Assert.That(after, Has.Count.EqualTo(before.Count), "File count unchanged");
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name), $"File {name} still present");
      Assert.That(after[name], Is.EqualTo(data), $"File {name} data intact");
    }
  }

  [Test, Category("HappyPath")]
  public void PlannerDefrag_Quick_PreservesAllFiles() {
    using var ms = BuildFragmentedImage();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Quick,
    });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count));
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name));
      Assert.That(after[name], Is.EqualTo(data));
    }
  }

  [Test, Category("HappyPath")]
  public void PlannerDefrag_ReducesFragmentation() {
    using var ms = BuildFragmentedImage();
    var fragmentsBefore = CountFragments(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
    });

    var fragmentsAfter = CountFragments(ms);
    // Post-defrag should have at most as many fragments as before (often fewer).
    Assert.That(fragmentsAfter, Is.LessThanOrEqualTo(fragmentsBefore),
      "Defrag should not increase fragmentation");
  }

  [Test, Category("HappyPath")]
  public void PlannerDefrag_ConsolidateAtEnd_PreservesFiles() {
    using var ms = BuildFragmentedImage();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtEnd,
      Profile = LayoutProfile.Performance,
    });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count));
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name));
      Assert.That(after[name], Is.EqualTo(data));
    }
  }

  [Test, Category("HappyPath")]
  public void PlannerDefrag_FillHolesLazy_PreservesFiles() {
    using var ms = BuildFragmentedImage();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.FillHolesLazy,
      Profile = LayoutProfile.Quick,
    });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count));
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name));
      Assert.That(after[name], Is.EqualTo(data));
    }
  }

  [Test, Category("HappyPath")]
  public void PlannerDefrag_CarveHole_FallsBackToRebuild() {
    // CarveHole always uses the rebuild path; verify it still works.
    using var ms = BuildCleanImage();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.CarveHole,
      HoleSize = 1024,
    });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count));
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name));
      Assert.That(after[name], Is.EqualTo(data));
    }
  }

  [Test, Category("HappyPath")]
  public void PlannerDefrag_AlreadyDefragmented_NoOp() {
    // A freshly built image is already contiguous — planner should produce zero moves.
    using var ms = BuildCleanImage();
    var before = ExtractAll(ms);

    DefragProgressEvent? completeEvent = null;
    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      OnProgress = e => { if (e.Phase == "complete") completeEvent = e; },
    });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count));
    foreach (var (name, data) in before) {
      Assert.That(after[name], Is.EqualTo(data));
    }

    // Verify we got a completion event.
    Assert.That(completeEvent, Is.Not.Null, "Should emit a 'complete' progress event");
  }

  [Test, Category("HappyPath")]
  public void PlannerDefrag_EmitsProgressEvents() {
    using var ms = BuildFragmentedImage();
    var events = new List<DefragProgressEvent>();

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      OnProgress = e => events.Add(e),
    });

    // Should have at least a scanning event and a complete event.
    Assert.That(events, Has.Count.GreaterThanOrEqualTo(2),
      "Should emit scanning + complete events at minimum");
    Assert.That(events.Any(e => e.Phase == "scanning"), Is.True, "Should emit scanning event");
    Assert.That(events.Any(e => e.Phase == "complete"), Is.True, "Should emit complete event");
  }

  [Test, Category("HappyPath")]
  public void PlannerDefrag_PreservesImageSize() {
    using var ms = BuildFragmentedImage();
    var originalSize = ms.Length;

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
    });

    Assert.That(ms.Length, Is.EqualTo(originalSize), "Image size must not change");
  }

  [Test, Category("HappyPath")]
  public void PlannerDefrag_ConsolidateAtEnd_FilesAtTail() {
    // Create an image with files, defrag with ConsolidateAtEnd, and verify
    // that used extents actually cluster toward the end of the data region.
    using var ms = BuildFragmentedImage();
    var imageSize = ms.Length;

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtEnd,
      Profile = LayoutProfile.Performance,
    });

    ms.Position = 0;
    var extents = FatExtentMap.Enumerate(ms).ToList();
    var usedExtents = extents.Where(e => e.Kind == DefragBlockKind.Used).ToList();
    var leadingFree = extents
      .Where(e => e.Kind == DefragBlockKind.Free)
      .Sum(e => usedExtents.Count > 0 && e.Offset < usedExtents.Min(u => u.Offset) ? e.Length : 0);

    // Files must still extract cleanly.
    var files = ExtractAll(ms);
    Assert.That(files, Has.Count.GreaterThan(0));

    // The earliest Used extent must sit past the midpoint of the data region —
    // otherwise the planner silently fell back to start-pack.
    var earliestUsed = usedExtents.Min(e => e.Offset);
    Assert.That(earliestUsed, Is.GreaterThan(imageSize / 2),
      "ConsolidateAtEnd: file data must land in the upper half of the image");
    // There must be a sizeable leading free region (more than half the image).
    Assert.That(leadingFree, Is.GreaterThan(imageSize / 2),
      "ConsolidateAtEnd: most of the image's leading bytes must be free");
  }

  [Test, Category("HappyPath")]
  public void PlannerDefrag_ConsolidateAtEnd_SingleFile_MovesToTail() {
    // Regression: a single-file image with ConsolidateAtEnd previously was a
    // no-op because zone-reordering with only one zone is meaningless. Now the
    // planner explicitly computes a tail-anchored cursor.
    var w = new FatWriter();
    var payload = Encoding.ASCII.GetBytes("solitary file payload");
    w.AddFile("LONE.TXT", payload);
    var image = w.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    var imageSize = ms.Length;

    ms.Position = 0;
    var beforeOffset = FatExtentMap.Enumerate(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "LONE.TXT").Offset;

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtEnd,
      Profile = LayoutProfile.Performance,
    });

    ms.Position = 0;
    var afterOffset = FatExtentMap.Enumerate(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "LONE.TXT").Offset;

    Assert.That(afterOffset, Is.GreaterThan(beforeOffset),
      "Single-file ConsolidateAtEnd must move the file to a higher offset");
    Assert.That(afterOffset, Is.GreaterThan(imageSize / 2),
      "Single-file ConsolidateAtEnd must land the file in the upper half of the image");

    ms.Position = 0;
    var reader = new FatReader(ms);
    var entry = reader.Entries.First(e => e.Name == "LONE.TXT");
    var extracted = reader.Extract(entry);
    Assert.That(extracted, Is.EqualTo(payload), "File payload intact after tail move");
  }

  [Test, Category("HappyPath")]
  public void DefragPlanner_Plan_NoExtents_ReturnsEmpty() {
    var moves = DefragPlanner.Plan(
      [],
      dataOrigin: 9728,
      imageSize: 1474560,
      clusterSize: 512,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart);
    Assert.That(moves, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void DefragPlanner_Plan_AlreadyPacked_ReturnsEmpty() {
    // Simulate an already-packed image: metadata + 2 contiguous Used blocks.
    var extents = new List<DefragBlockInfo> {
      new(0, 9728, DefragBlockKind.MetadataReserved, "FAT reserved"),
      new(9728, 512, DefragBlockKind.Used, "A.TXT"),
      new(10240, 512, DefragBlockKind.Used, "B.TXT"),
      new(10752, 1463808, DefragBlockKind.Free),
    };
    var moves = DefragPlanner.Plan(
      extents,
      dataOrigin: 9728,
      imageSize: 1474560,
      clusterSize: 512,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart);

    Assert.That(moves, Is.Empty, "Already packed — no moves needed");
  }

  [Test, Category("HappyPath")]
  public void DefragPlanner_Plan_WithHoles_ProducesMoves() {
    // Simulate fragmentation: A at 9728, hole at 10240, B at 10752.
    var extents = new List<DefragBlockInfo> {
      new(0, 9728, DefragBlockKind.MetadataReserved, "FAT reserved"),
      new(9728, 512, DefragBlockKind.Used, "A.TXT"),
      new(10240, 512, DefragBlockKind.Free),
      new(10752, 512, DefragBlockKind.Used, "B.TXT"),
      new(11264, 1463296, DefragBlockKind.Free),
    };
    var moves = DefragPlanner.Plan(
      extents,
      dataOrigin: 9728,
      imageSize: 1474560,
      clusterSize: 512,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart);

    // B should move to fill the hole.
    Assert.That(moves, Has.Count.GreaterThan(0), "Should produce at least one move");
  }

  [Test, Category("HappyPath")]
  public void FatBlockMover_Init_ParsesBpb() {
    var w = new FatWriter();
    w.AddFile("X.TXT", "hello"u8.ToArray());
    var image = w.Build();

    var mover = new FatBlockMover();
    mover.Init(image);

    Assert.That(mover.ClusterSize, Is.GreaterThan(0));
    Assert.That(mover.FatType, Is.EqualTo(12)); // default FAT12 floppy
    Assert.That(mover.FirstDataByte, Is.GreaterThan(0));
    Assert.That(mover.TotalDataClusters, Is.GreaterThan(0));
  }

  [Test, Category("HappyPath")]
  public void FatBlockMover_GetChain_ReturnsExpectedClusters() {
    var w = new FatWriter();
    w.AddFile("X.TXT", new byte[1024]); // spans 2 clusters on FAT12 (512 bytes/cluster)
    var image = w.Build();

    var mover = new FatBlockMover();
    mover.Init(image);

    // Find the file's start cluster via the extent map (which is public).
    var extents = FatExtentMap.Enumerate(new MemoryStream(image)).ToList();
    var fileExtent = extents.FirstOrDefault(e => e.Kind == DefragBlockKind.Used && e.FileName == "X.TXT");
    Assert.That(fileExtent, Is.Not.Null, "File extent must be found");
    var startCluster = mover.OffsetCluster(fileExtent!.Offset);
    var chain = mover.GetChain(image, startCluster);

    // 1024 bytes / 512 bytes per cluster = 2 clusters minimum
    Assert.That(chain, Has.Count.GreaterThanOrEqualTo(2));
    // Clusters should be sequential (no fragmentation in fresh image)
    for (var i = 1; i < chain.Count; i++)
      Assert.That(chain[i], Is.EqualTo(chain[i - 1] + 1));
  }

  [Test, Category("Integration")]
  public void PlannerDefrag_ExistingFatTests_StillPass_ConsolidateAtStart() {
    // Verify that the planner path produces results compatible with existing
    // tests — specifically that the image is still readable after defrag.
    var w = new FatWriter();
    w.AddFile("A.TXT", new byte[] { 1, 2, 3 });
    w.AddFile("B.TXT", new byte[] { 4, 5, 6 });
    var image = w.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    var originalSize = ms.Length;

    new FatFormatDescriptor().Defragment(ms);
    Assert.That(ms.Length, Is.EqualTo(originalSize));

    ms.Position = 0;
    var reader = new FatReader(ms);
    Assert.That(reader.Entries.Count(e => !e.IsDirectory), Is.EqualTo(2));
  }

  [Test, Category("Integration")]
  public void LayoutProfile_DefaultIsPerformance() {
    var opts = new DefragOptions();
    Assert.That(opts.Profile, Is.EqualTo(LayoutProfile.Performance));
  }

  [Test, Category("HappyPath")]
  public void FatFormatDescriptor_ImplementsIFilesystemBlockMover() {
    var desc = new FatFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IFilesystemBlockMover>());
  }
}
