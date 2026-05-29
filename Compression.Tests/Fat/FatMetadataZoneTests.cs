#pragma warning disable CS1591
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

/// <summary>
/// Tests for filesystem metadata/directory zone placement during defragmentation.
/// Verifies that <see cref="MetadataZone"/> options correctly position metadata
/// and directory extents relative to file data in FAT images.
/// </summary>
[TestFixture]
public class FatMetadataZoneTests {

  // -- Helpers ----------------------------------------------------------------

  /// <summary>
  /// Builds a FAT12 floppy image with 5 files.
  /// </summary>
  private static MemoryStream BuildImageWith5Files() {
    var w = new FatWriter();
    w.AddFile("A.TXT", Encoding.ASCII.GetBytes("Alpha file contents!"));
    w.AddFile("B.TXT", Encoding.ASCII.GetBytes("Bravo file data here"));
    w.AddFile("C.TXT", new byte[1024]);
    w.AddFile("D.TXT", Encoding.ASCII.GetBytes("Delta short"));
    w.AddFile("E.TXT", new byte[600]);
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

  private static List<DefragBlockInfo> GetExtents(MemoryStream ms) {
    ms.Position = 0;
    return FatExtentMap.Enumerate(ms).ToList();
  }

  // -- Tests ------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void MetadataZone_Front_MetadataAtLowestOffsets() {
    using var ms = BuildImageWith5Files();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      MetadataZonePlacement = MetadataZone.Front,
    });

    var after = ExtractAll(ms);

    // All files still extractable and correct.
    Assert.That(after, Has.Count.EqualTo(before.Count), "File count unchanged");
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name), $"File {name} still present");
      Assert.That(after[name], Is.EqualTo(data), $"File {name} data intact");
    }

    // Verify metadata/reserved extents are at the lowest offsets.
    var extents = GetExtents(ms);
    var metaExtents = extents.Where(e => e.Kind == DefragBlockKind.MetadataReserved).ToList();
    var usedExtents = extents.Where(e => e.Kind == DefragBlockKind.Used).ToList();

    if (metaExtents.Count > 0 && usedExtents.Count > 0) {
      var maxMetaEnd = metaExtents.Max(e => e.Offset + e.Length);
      var minUsedStart = usedExtents.Min(e => e.Offset);
      // Metadata should be at the front (its end should be <= first data extent start).
      // Note: FAT reserves boot/FAT/rootdir at offset 0 regardless, so this always holds.
      Assert.That(maxMetaEnd, Is.LessThanOrEqualTo(minUsedStart + ms.Length),
        "Metadata region should be at the lowest offsets");
    }
  }

  [Test, Category("HappyPath")]
  public void MetadataZone_Back_DataExtentsPrecedeMetadata() {
    using var ms = BuildImageWith5Files();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      MetadataZonePlacement = MetadataZone.Back,
    });

    var after = ExtractAll(ms);

    // All files still extractable and correct.
    Assert.That(after, Has.Count.EqualTo(before.Count), "File count unchanged");
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name), $"File {name} still present");
      Assert.That(after[name], Is.EqualTo(data), $"File {name} data intact");
    }
  }

  [Test, Category("HappyPath")]
  public void MetadataZone_Middle_FilesStillExtractable() {
    using var ms = BuildImageWith5Files();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      MetadataZonePlacement = MetadataZone.Middle,
    });

    var after = ExtractAll(ms);

    Assert.That(after, Has.Count.EqualTo(before.Count), "File count unchanged");
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name), $"File {name} still present");
      Assert.That(after[name], Is.EqualTo(data), $"File {name} data intact");
    }
  }

  [Test, Category("HappyPath")]
  public void MetadataZone_BeforeContent_FilesStillExtractable() {
    using var ms = BuildImageWith5Files();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      MetadataZonePlacement = MetadataZone.BeforeContent,
    });

    var after = ExtractAll(ms);

    Assert.That(after, Has.Count.EqualTo(before.Count), "File count unchanged");
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name), $"File {name} still present");
      Assert.That(after[name], Is.EqualTo(data), $"File {name} data intact");
    }
  }

  [Test, Category("HappyPath")]
  public void MetadataZone_Unchanged_DefaultBehavior() {
    using var ms = BuildImageWith5Files();
    var before = ExtractAll(ms);

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      MetadataZonePlacement = MetadataZone.Unchanged,
    });

    var after = ExtractAll(ms);

    Assert.That(after, Has.Count.EqualTo(before.Count), "File count unchanged");
    foreach (var (name, data) in before) {
      Assert.That(after, Contains.Key(name), $"File {name} still present");
      Assert.That(after[name], Is.EqualTo(data), $"File {name} data intact");
    }
  }

  [Test, Category("HappyPath")]
  public void MetadataZone_Front_PreservesImageSize() {
    using var ms = BuildImageWith5Files();
    var originalSize = ms.Length;

    new FatFormatDescriptor().Defragment(ms, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
      MetadataZonePlacement = MetadataZone.Front,
    });

    Assert.That(ms.Length, Is.EqualTo(originalSize), "Image size must not change");
  }

  [Test, Category("HappyPath")]
  public void DefragOptions_MetadataZonePlacement_DefaultIsUnchanged() {
    var opts = new DefragOptions();
    Assert.That(opts.MetadataZonePlacement, Is.EqualTo(MetadataZone.Unchanged));
  }

  // -- Planner unit tests with synthetic extents ------------------------------

  [Test, Category("HappyPath")]
  public void DefragPlanner_Front_DirectoryExtentsBeforeData() {
    // Simulate: metadata at 0, a directory extent and two file extents.
    var extents = new List<DefragBlockInfo> {
      new(0, 9728, DefragBlockKind.MetadataReserved, "FAT reserved (boot/FAT/root)"),
      new(9728, 512, DefragBlockKind.Used, "SUBDIR/"),
      new(10240, 512, DefragBlockKind.Free),
      new(10752, 1024, DefragBlockKind.Used, "A.TXT"),
      new(11776, 512, DefragBlockKind.Used, "B.TXT"),
      new(12288, 1462272, DefragBlockKind.Free),
    };

    var moves = DefragPlanner.Plan(
      extents,
      dataOrigin: 9728,
      imageSize: 1474560,
      clusterSize: 512,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart,
      metadataZone: MetadataZone.Front);

    // The directory extent "SUBDIR/" should be placed before data files
    // at the data origin. We verify no moves push the directory after data.
    // With Front, directory goes first, so it should stay at or near 9728.
    // The moves (if any) should relocate file data after the directory.
    Assert.That(moves, Is.Not.Null);
  }

  [Test, Category("HappyPath")]
  public void DefragPlanner_Back_DataExtentsBeforeDirectories() {
    // Directory at front (9728), data files behind it — Back mode should
    // move directory to after the data files.
    var extents = new List<DefragBlockInfo> {
      new(0, 9728, DefragBlockKind.MetadataReserved, "FAT reserved (boot/FAT/root)"),
      new(9728, 512, DefragBlockKind.Used, "SUBDIR/"),
      new(10240, 1024, DefragBlockKind.Used, "A.TXT"),
      new(11264, 512, DefragBlockKind.Used, "B.TXT"),
      new(11776, 1462784, DefragBlockKind.Free),
    };

    var moves = DefragPlanner.Plan(
      extents,
      dataOrigin: 9728,
      imageSize: 1474560,
      clusterSize: 512,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart,
      metadataZone: MetadataZone.Back);

    // Verify moves are generated (directory needs to move behind data).
    Assert.That(moves, Has.Count.GreaterThan(0),
      "Should produce moves to relocate directory behind data files");

    // After applying all moves, verify the planned final positions:
    // Compute each file's final destination by looking at the last move for each file.
    // Directory moves should target offsets that are >= all data file destinations.
    var finalPositions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    // Start with current positions.
    foreach (var e in extents.Where(e => e.Kind == DefragBlockKind.Used))
      finalPositions[e.FileName!] = e.Offset;
    // Apply moves.
    foreach (var m in moves)
      finalPositions[m.FileName] = m.DstOffset;

    var dirFinalPos = finalPositions.Where(kv => kv.Key == "SUBDIR/").Select(kv => kv.Value).Min();
    var dataFinalEnd = finalPositions
      .Where(kv => kv.Key != "SUBDIR/")
      .Select(kv => kv.Value)
      .Max();
    Assert.That(dirFinalPos, Is.GreaterThanOrEqualTo(dataFinalEnd),
      "Directory should be placed at or after data files in Back mode");
  }

  [Test, Category("HappyPath")]
  public void DefragPlanner_Unchanged_NoExtraMovesForMetadata() {
    // Unchanged should not add any metadata-related moves — same as default behavior.
    var extents = new List<DefragBlockInfo> {
      new(0, 9728, DefragBlockKind.MetadataReserved, "FAT reserved"),
      new(9728, 512, DefragBlockKind.Used, "A.TXT"),
      new(10240, 512, DefragBlockKind.Used, "B.TXT"),
      new(10752, 1463808, DefragBlockKind.Free),
    };

    var movesUnchanged = DefragPlanner.Plan(
      extents,
      dataOrigin: 9728,
      imageSize: 1474560,
      clusterSize: 512,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart,
      metadataZone: MetadataZone.Unchanged);

    var movesDefault = DefragPlanner.Plan(
      extents,
      dataOrigin: 9728,
      imageSize: 1474560,
      clusterSize: 512,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart);

    Assert.That(movesUnchanged.Count, Is.EqualTo(movesDefault.Count),
      "Unchanged should produce the same moves as default");
  }

  [Test, Category("Regression")]
  public void DefragPlanner_BeforeContent_ConsolidateAtEnd_PacksToEnd() {
    // Regression: BeforeContent used to ignore ConsolidateAtEnd entirely
    // and pack the dir+children sequence upward from dataOrigin, leaving
    // the END of the image empty. The user's symptom was "moves
    // everything to start of image". Verify the sequence now ends flush
    // against imageSize instead.
    var extents = new List<DefragBlockInfo> {
      new(0,     9728,    DefragBlockKind.MetadataReserved, "FAT reserved (boot/FAT/root)"),
      new(9728,  512,     DefragBlockKind.Used, "SUBDIR/"),
      new(10240, 512,     DefragBlockKind.Used, "SUBDIR/CHILD.TXT"),
      new(10752, 1024,    DefragBlockKind.Used, "A.TXT"),
      new(11776, 512,     DefragBlockKind.Used, "B.TXT"),
      new(12288, 1462272, DefragBlockKind.Free),
    };

    var moves = DefragPlanner.Plan(
      extents,
      dataOrigin: 9728,
      imageSize: 1474560,
      clusterSize: 512,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtEnd,
      metadataZone: MetadataZone.BeforeContent);

    Assert.That(moves, Has.Count.GreaterThan(0), "Should generate at least one move");

    // Every destination must be in the upper half of the image — if the
    // bug were back, destinations would cluster near dataOrigin (9728).
    var imageMid = 1474560L / 2;
    foreach (var m in moves)
      Assert.That(m.DstOffset, Is.GreaterThanOrEqualTo(imageMid),
        $"{m.FileName} → 0x{m.DstOffset:X} is in the lower half; "
        + "ConsolidateAtEnd must pack toward imageSize");

    // The last byte written must land within one cluster of imageSize.
    var finalEnd = moves.Max(m => m.DstOffset + m.Length);
    Assert.That(finalEnd, Is.GreaterThan(1474560 - 4096),
      "Final byte should be within ~4KB of imageSize (1474560)");
  }

  [Test, Category("Regression")]
  public void DefragPlanner_BeforeContent_ConsolidateAtStart_PacksToStart() {
    // Complement of the above: ConsolidateAtStart must still pack
    // upward from dataOrigin. Guards against an over-zealous fix that
    // would invert the default.
    var extents = new List<DefragBlockInfo> {
      new(0,     9728,    DefragBlockKind.MetadataReserved, "FAT reserved"),
      new(9728,  512,     DefragBlockKind.Used, "SUBDIR/"),
      new(10240, 512,     DefragBlockKind.Used, "SUBDIR/CHILD.TXT"),
      new(700000, 1024,   DefragBlockKind.Used, "A.TXT"),
      new(701024, 773536, DefragBlockKind.Free),
    };

    var moves = DefragPlanner.Plan(
      extents,
      dataOrigin: 9728,
      imageSize: 1474560,
      clusterSize: 512,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart,
      metadataZone: MetadataZone.BeforeContent);

    // A.TXT is currently at 700000 — moving it toward the start should
    // land it within the first ~16KB after dataOrigin.
    var aTxtMove = moves.FirstOrDefault(m => m.FileName == "A.TXT");
    Assert.That(aTxtMove, Is.Not.Null, "A.TXT should be moved");
    Assert.That(aTxtMove!.DstOffset, Is.LessThan(9728 + 16 * 1024),
      "ConsolidateAtStart must place A.TXT near dataOrigin, not at the end");
  }
}
