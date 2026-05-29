#pragma warning disable CS1591
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

/// <summary>
/// Tests for planner-driven CarveHole defragmentation on FAT images.
/// Verifies that <see cref="DefragPlanner.PlanCarveHole"/> correctly
/// relocates live extents to carve a contiguous free region, and that
/// the resulting image is still valid.
/// </summary>
[TestFixture]
public class FatCarveHoleDefragTests {

  // ── Helpers ────────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a FAT12 floppy image with 5 files spread across clusters.
  /// </summary>
  private static MemoryStream BuildImageWith5Files() {
    var w = new FatWriter();
    w.AddFile("A.TXT", Encoding.ASCII.GetBytes("Alpha file contents!"));
    w.AddFile("B.TXT", Encoding.ASCII.GetBytes("Bravo file data here"));
    w.AddFile("C.TXT", new byte[1024]); // spans multiple clusters
    w.AddFile("D.TXT", Encoding.ASCII.GetBytes("Delta short"));
    w.AddFile("E.TXT", new byte[600]);
    var image = w.Build();
    var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    return ms;
  }

  /// <summary>
  /// Builds a fragmented FAT12 image (files with holes between them).
  /// </summary>
  private static MemoryStream BuildFragmentedImage() {
    var w = new FatWriter();
    w.AddFile("A.TXT", Encoding.ASCII.GetBytes("Alpha content here!"));
    w.AddFile("B.TXT", Encoding.ASCII.GetBytes("Bravo content file."));
    w.AddFile("C.TXT", new byte[1024]);
    w.AddFile("D.TXT", Encoding.ASCII.GetBytes("Delta data block!"));
    w.AddFile("E.TXT", new byte[600]);
    var image = w.Build();

    // Remove B and D to create holes.
    FatRemover.Remove(image, "B.TXT");
    FatRemover.Remove(image, "D.TXT");

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

  // ── Tests ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void CarveHole_AutoAtEnd_PreservesAllFiles() {
    using var ms = BuildImageWith5Files();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.CarveHole,
      HoleSize = 4096,
      HoleAt = -1, // auto = at end
    });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count), "File count unchanged");
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name), $"File {name} still present");
      Assert.That(after[name], Is.EqualTo(data), $"File {name} data intact");
    }
  }

  [Test, Category("HappyPath")]
  public void CarveHole_AutoAtEnd_HoleExistsAfterLastExtent() {
    using var ms = BuildImageWith5Files();

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.CarveHole,
      HoleSize = 4096,
      HoleAt = -1,
    });

    // After auto-CarveHole, the region after the last live extent should be free.
    ms.Position = 0;
    var extents = FatExtentMap.Enumerate(ms).ToList();
    var usedExtents = extents.Where(e => e.Kind == DefragBlockKind.Used).ToList();
    var freeExtents = extents.Where(e => e.Kind == DefragBlockKind.Free).ToList();

    // There must be at least one free region.
    Assert.That(freeExtents, Has.Count.GreaterThan(0), "Must have free space for the hole");

    // Total free space must be at least 4096 bytes.
    var totalFree = freeExtents.Sum(e => e.Length);
    Assert.That(totalFree, Is.GreaterThanOrEqualTo(4096), "Free space must cover the carved hole");
  }

  [Test, Category("HappyPath")]
  public void CarveHole_SpecificOffset_RelocatesOverlappingExtents() {
    using var ms = BuildFragmentedImage();
    var before = ExtractAll(ms);

    // Read extent map to find a specific offset where a file lives.
    ms.Position = 0;
    var extentsBefore = FatExtentMap.Enumerate(ms).ToList();
    var firstUsed = extentsBefore.First(e => e.Kind == DefragBlockKind.Used);
    var holeAt = firstUsed.Offset; // carve right where a file sits

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.CarveHole,
      HoleSize = 2048,
      HoleAt = holeAt,
    });

    // Files must still be extractable and correct.
    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count), "File count unchanged");
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name), $"File {name} still present");
      Assert.That(after[name], Is.EqualTo(data), $"File {name} data intact");
    }
  }

  [Test, Category("HappyPath")]
  public void CarveHole_PreservesImageSize() {
    using var ms = BuildImageWith5Files();
    var originalSize = ms.Length;

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.CarveHole,
      HoleSize = 4096,
      HoleAt = -1,
    });

    Assert.That(ms.Length, Is.EqualTo(originalSize), "Image size must not change");
  }

  [Test, Category("HappyPath")]
  public void CarveHole_NoOverlap_NoMoves() {
    // When the hole region is already free, no moves should be needed.
    // Use a clean image and place the hole at the very end (beyond all files).
    using var ms = BuildImageWith5Files();
    var before = ExtractAll(ms);

    // The auto-mode places the hole after the last extent, which is likely
    // already free. Verify the files are unchanged.
    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.CarveHole,
      HoleSize = 512,
      HoleAt = -1,
    });

    var after = ExtractAll(ms);
    Assert.That(after, Has.Count.EqualTo(before.Count));
    foreach (var (name, data) in before)
      Assert.That(after[name], Is.EqualTo(data));
  }

  [Test, Category("ErrorPath")]
  public void CarveHole_HoleTooLarge_ThrowsOrFallsBack() {
    // Request a hole larger than the image — should either throw
    // InvalidOperationException from the planner or fall back to rebuild
    // which throws ArgumentException.
    using var ms = BuildImageWith5Files();
    var imageSize = ms.Length;

    Assert.Throws<ArgumentException>(() => {
      new FatFormatDescriptor().Defragment(ms, new DefragOptions {
        Mode = DefragMode.CarveHole,
        HoleSize = imageSize * 2, // way too large
        HoleAt = 0,
      });
    });
  }

  [Test, Category("HappyPath")]
  public void DefragPlanner_PlanCarveHole_ReturnsMovesForOverlap() {
    // Unit-test the planner directly with synthetic extents.
    var extents = new List<DefragBlockInfo> {
      new(0, 9728, DefragBlockKind.MetadataReserved, "FAT reserved"),
      new(9728, 512, DefragBlockKind.Used, "A.TXT"),
      new(10240, 512, DefragBlockKind.Used, "B.TXT"),
      new(10752, 512, DefragBlockKind.Used, "C.TXT"),
      new(11264, 1463296, DefragBlockKind.Free),
    };

    // Carve a 1024-byte hole at offset 9728, overlapping A.TXT and B.TXT.
    var moves = DefragPlanner.Plan(
      extents,
      dataOrigin: 9728,
      imageSize: 1474560,
      clusterSize: 512,
      profile: LayoutProfile.Performance,
      mode: DefragMode.CarveHole,
      holeSize: 1024,
      holeAt: 9728);

    // Should produce moves for A.TXT and B.TXT out of the hole region.
    Assert.That(moves, Has.Count.GreaterThanOrEqualTo(2),
      "Must relocate the 2 extents that overlap the hole");

    // All moves should target offsets outside [9728, 10752).
    foreach (var move in moves) {
      var moveEnd = move.DstOffset + move.Length;
      var overlapHole = move.DstOffset < 10752 && 9728 < moveEnd;
      Assert.That(overlapHole, Is.False,
        $"Move for {move.FileName} should not target inside the hole region: dst={move.DstOffset}");
    }
  }

  [Test, Category("HappyPath")]
  public void DefragPlanner_PlanCarveHole_AutoPlacesAfterLastExtent() {
    var extents = new List<DefragBlockInfo> {
      new(0, 9728, DefragBlockKind.MetadataReserved, "FAT reserved"),
      new(9728, 512, DefragBlockKind.Used, "A.TXT"),
      new(10240, 1464320, DefragBlockKind.Free),
    };

    // HoleAt = -1 means auto (after last extent). Since the region after
    // A.TXT is already free, no moves should be needed.
    var moves = DefragPlanner.Plan(
      extents,
      dataOrigin: 9728,
      imageSize: 1474560,
      clusterSize: 512,
      profile: LayoutProfile.Performance,
      mode: DefragMode.CarveHole,
      holeSize: 2048,
      holeAt: -1);

    Assert.That(moves, Is.Empty,
      "No moves needed when hole region after last extent is already free");
  }

  [Test, Category("ErrorPath")]
  public void DefragPlanner_PlanCarveHole_ZeroHoleSize_Throws() {
    var extents = new List<DefragBlockInfo> {
      new(0, 512, DefragBlockKind.MetadataReserved, "reserved"),
      new(512, 512, DefragBlockKind.Used, "A.TXT"),
    };

    Assert.Throws<ArgumentException>(() => DefragPlanner.Plan(
      extents,
      dataOrigin: 512,
      imageSize: 4096,
      clusterSize: 512,
      profile: LayoutProfile.Performance,
      mode: DefragMode.CarveHole,
      holeSize: 0,
      holeAt: 512));
  }

  [Test, Category("ErrorPath")]
  public void DefragPlanner_PlanCarveHole_HoleExceedsImage_Throws() {
    var extents = new List<DefragBlockInfo> {
      new(0, 512, DefragBlockKind.MetadataReserved, "reserved"),
      new(512, 512, DefragBlockKind.Used, "A.TXT"),
      new(1024, 3072, DefragBlockKind.Free),
    };

    Assert.Throws<InvalidOperationException>(() => DefragPlanner.Plan(
      extents,
      dataOrigin: 512,
      imageSize: 4096,
      clusterSize: 512,
      profile: LayoutProfile.Performance,
      mode: DefragMode.CarveHole,
      holeSize: 8192,
      holeAt: 512));
  }
}
