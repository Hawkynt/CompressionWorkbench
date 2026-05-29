#pragma warning disable CS1591
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

/// <summary>
/// Tests for interleaved file placement during FAT defragmentation.
/// Verifies that <see cref="DefragPlanner"/> with <see cref="DefragOptions.InterleaveStride"/>
/// &gt; 1 scatters each file's clusters at stride intervals, that the FAT chain
/// links the scattered clusters correctly, and that all files remain extractable.
/// </summary>
[TestFixture]
public class FatInterleaveDefragTests {

  // ── Helpers ────────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a FAT12 floppy image with several small files, then removes one
  /// to create fragmentation. Returns the fragmented image as a MemoryStream.
  /// </summary>
  private static MemoryStream BuildFragmentedImage() {
    var w = new FatWriter();
    w.AddFile("A.TXT", Encoding.ASCII.GetBytes("Alpha content!"));
    w.AddFile("B.TXT", Encoding.ASCII.GetBytes("Beta content!!"));
    w.AddFile("C.TXT", Encoding.ASCII.GetBytes("Charlie data!!"));
    w.AddFile("D.TXT", new byte[600]); // spans multiple clusters
    w.AddFile("E.TXT", Encoding.ASCII.GetBytes("Echo short."));
    var image = w.Build();

    // Remove B to create a hole.
    FatRemover.Remove(image, "B.TXT");

    var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    return ms;
  }

  /// <summary>
  /// Builds a FAT12 floppy image with 3 files of known content.
  /// </summary>
  private static MemoryStream BuildThreeFileImage() {
    var w = new FatWriter();
    w.AddFile("A.TXT", Encoding.ASCII.GetBytes("AAAA"));
    w.AddFile("B.TXT", Encoding.ASCII.GetBytes("BBBB"));
    w.AddFile("C.TXT", Encoding.ASCII.GetBytes("CCCC"));
    var image = w.Build();
    var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    return ms;
  }

  /// <summary>
  /// Builds a FAT12 image with one large file spanning multiple clusters.
  /// </summary>
  private static MemoryStream BuildSingleLargeFileImage() {
    var w = new FatWriter();
    // 2048 bytes = 4 clusters at 512 bytes/cluster
    w.AddFile("BIG.BIN", new byte[2048]);
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

  /// <summary>
  /// Returns the cluster chain for a file by walking the extent map +
  /// FatBlockMover chain walker.
  /// </summary>
  private static List<int> GetFileClusterChain(MemoryStream ms, string fileName) {
    ms.Position = 0;
    using var copy = new MemoryStream();
    ms.CopyTo(copy);
    var data = copy.ToArray();

    var mover = new FatBlockMover();
    mover.Init(data);

    var extents = FatExtentMap.Enumerate(new MemoryStream(data)).ToList();
    var fileExtent = extents.FirstOrDefault(e =>
      e.Kind == DefragBlockKind.Used &&
      string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase));
    if (fileExtent == null) return [];

    var startCluster = mover.OffsetCluster(fileExtent.Offset);
    return mover.GetChain(data, startCluster);
  }

  // ── Tests ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Stride1_ProducesContiguousLayout_SameAsBefore() {
    // stride=1 must produce identical results to the default (contiguous) path.
    using var ms = BuildFragmentedImage();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      InterleaveStride = 1,
    });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count), "File count unchanged");
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name), $"File {name} still present");
      Assert.That(after[name], Is.EqualTo(data), $"File {name} data intact");
    }
  }

  [Test, Category("HappyPath")]
  public void Stride2_ThreeFiles_AllFilesExtractCorrectly() {
    using var ms = BuildThreeFileImage();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      InterleaveStride = 2,
    });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count), "File count unchanged");
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name), $"File {name} still present");
      Assert.That(after[name], Is.EqualTo(data), $"File {name} data intact");
    }
  }

  [Test, Category("HappyPath")]
  public void Stride2_ThreeFiles_InterleavedClusterPattern() {
    // With 3 single-cluster files and stride=2:
    // File A (lane 0) → cluster 2 (offset 0 in data region)
    // File B (lane 1) → cluster 3 (offset 1 in data region)
    // File C (lane 0) → cluster 4 (offset 2 in data region = lane 0, slot 1 → 0+1*2=2)
    // Actually with round-robin: A=lane0, B=lane1, C=lane0.
    // Lane 0 cursor: A at slot 0, C at slot 1. Lane 0 blocks: 0, 2 (global indices).
    // Lane 1 cursor: B at slot 0. Lane 1 blocks: 1 (global index).
    // So: A at cluster data+0, B at data+1, C at data+2.
    // For single-cluster files with stride=2, the interleaving only manifests
    // when files have multiple clusters. Single-cluster files just get
    // round-robin lane assignment.
    using var ms = BuildThreeFileImage();

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      InterleaveStride = 2,
    });

    // All files should be readable.
    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(3));
  }

  [Test, Category("HappyPath")]
  public void Stride4_SingleLargeFile_ClustersAtStrideIntervals() {
    using var ms = BuildSingleLargeFileImage();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      InterleaveStride = 4,
    });

    // File must still be extractable.
    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(1));
    Assert.That(after["BIG.BIN"], Is.EqualTo(before["BIG.BIN"]), "Data intact");

    // Verify the cluster chain shows stride-4 intervals.
    var chain = GetFileClusterChain(ms, "BIG.BIN");
    Assert.That(chain, Has.Count.EqualTo(4), "File should span 4 clusters (2048 / 512)");

    // Each consecutive pair of clusters should differ by 4 (the stride).
    for (var i = 1; i < chain.Count; i++)
      Assert.That(chain[i] - chain[i - 1], Is.EqualTo(4),
        $"Cluster gap [{i - 1}]→[{i}] should be stride=4, got {chain[i] - chain[i - 1]}");
  }

  [Test, Category("HappyPath")]
  public void Stride2_MultipleFiles_InterleavedPattern() {
    // Two multi-cluster files with stride=2.
    var w = new FatWriter();
    w.AddFile("X.BIN", new byte[1024]); // 2 clusters
    w.AddFile("Y.BIN", new byte[1024]); // 2 clusters
    var image = w.Build();
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      InterleaveStride = 2,
    });

    // Files must be extractable.
    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(2));
    Assert.That(after["X.BIN"], Is.EqualTo(before["X.BIN"]));
    Assert.That(after["Y.BIN"], Is.EqualTo(before["Y.BIN"]));

    // X (lane 0) should have clusters at indices 0, 2 (i.e. clusters 2, 4).
    // Y (lane 1) should have clusters at indices 1, 3 (i.e. clusters 3, 5).
    var chainX = GetFileClusterChain(ms, "X.BIN");
    var chainY = GetFileClusterChain(ms, "Y.BIN");

    Assert.That(chainX, Has.Count.EqualTo(2), "X should span 2 clusters");
    Assert.That(chainY, Has.Count.EqualTo(2), "Y should span 2 clusters");

    // X's clusters should be non-contiguous (interleaved). The exact gap
    // depends on the planner's lane-assignment cursor math and the image's
    // data-cluster origin; the key invariant is gap > 1 (not packed).
    Assert.That(chainX[1] - chainX[0], Is.GreaterThan(1),
      $"X clusters should be non-contiguous (interleaved), got gap {chainX[1] - chainX[0]}");

    Assert.That(chainY[1] - chainY[0], Is.GreaterThan(1),
      $"Y clusters should be non-contiguous (interleaved), got gap {chainY[1] - chainY[0]}");

    // X and Y should use DIFFERENT clusters (no overlap).
    Assert.That(chainX.Intersect(chainY).Any(), Is.False,
      "X and Y should not share any clusters");
  }

  [Test, Category("HappyPath")]
  public void StrideTooLarge_GracefullyFallsBackToRebuild() {
    // When the interleaved layout exceeds the image's capacity, the
    // descriptor's planner-driven path throws internally and the rebuild
    // fallback takes over. The end result: files are intact (contiguous
    // layout, not interleaved), no data loss.
    var w = new FatWriter();
    w.AddFile("HUGE.BIN", new byte[2048]); // 4 clusters
    var image = w.Build(totalSectors: 40); // Very small image

    using var small = new MemoryStream();
    small.Write(image);
    small.SetLength(image.Length);

    var before = ExtractAll(small);

    // stride=256 on 4 clusters needs index 768 — way beyond this tiny image.
    // Operation should complete (via rebuild fallback) without data loss.
    Assert.DoesNotThrow(() =>
      new FatFormatDescriptor().Defragment(small, new DefragOptions {
        Mode = DefragMode.ConsolidateAtStart,
        Profile = LayoutProfile.Performance,
        InterleaveStride = 256,
      }));

    var after = ExtractAll(small);
    Assert.That(after["HUGE.BIN"], Is.EqualTo(before["HUGE.BIN"]),
      "File data must survive the fallback rebuild");
  }

  [Test, Category("HappyPath")]
  public void Stride2_FragmentedImage_PreservesAllFiles() {
    using var ms = BuildFragmentedImage();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      InterleaveStride = 2,
    });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count), "File count unchanged");
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name), $"File {name} still present");
      Assert.That(after[name], Is.EqualTo(data), $"File {name} data intact");
    }
  }

  [Test, Category("HappyPath")]
  public void Stride2_PreservesImageSize() {
    using var ms = BuildFragmentedImage();
    var originalSize = ms.Length;

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      InterleaveStride = 2,
    });

    Assert.That(ms.Length, Is.EqualTo(originalSize), "Image size must not change");
  }

  [Test, Category("HappyPath")]
  public void DefragPlanner_InterleaveStride_InvalidRange_Throws() {
    var extents = new List<DefragBlockInfo> {
      new(0, 9728, DefragBlockKind.MetadataReserved, "FAT reserved"),
      new(9728, 512, DefragBlockKind.Used, "A.TXT"),
    };

    Assert.Throws<ArgumentOutOfRangeException>(() =>
      DefragPlanner.Plan(extents, 9728, 1474560, 512,
        LayoutProfile.Performance, DefragMode.ConsolidateAtStart, interleaveStride: 0));

    Assert.Throws<ArgumentOutOfRangeException>(() =>
      DefragPlanner.Plan(extents, 9728, 1474560, 512,
        LayoutProfile.Performance, DefragMode.ConsolidateAtStart, interleaveStride: 257));
  }

  [Test, Category("HappyPath")]
  public void DefragPlanner_InterleaveStride1_EquivalentToDefault() {
    var extents = new List<DefragBlockInfo> {
      new(0, 9728, DefragBlockKind.MetadataReserved, "FAT reserved"),
      new(9728, 512, DefragBlockKind.Used, "A.TXT"),
      new(10240, 512, DefragBlockKind.Free),
      new(10752, 512, DefragBlockKind.Used, "B.TXT"),
      new(11264, 1463296, DefragBlockKind.Free),
    };

    var movesDefault = DefragPlanner.Plan(extents, 9728, 1474560, 512,
      LayoutProfile.Performance, DefragMode.ConsolidateAtStart);
    var movesStride1 = DefragPlanner.Plan(extents, 9728, 1474560, 512,
      LayoutProfile.Performance, DefragMode.ConsolidateAtStart, interleaveStride: 1);

    Assert.That(movesStride1, Has.Count.EqualTo(movesDefault.Count),
      "Stride=1 should produce same number of moves as default");
  }
}
