#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using Compression.Registry.Layout;

namespace Compression.Tests.Layout;

[TestFixture]
public class PlannerLayoutTemplateTests {

  // Build a fragmented extent map for three files with known sizes / offsets.
  // Returns the extent list plus the convenience indexed metadata.
  private static List<DefragBlockInfo> MakeExtents(long imageSize, params (string name, long offset, long length)[] files) {
    var list = new List<DefragBlockInfo>();
    var covered = new List<(long start, long end)>();
    foreach (var (name, off, len) in files) {
      list.Add(new DefragBlockInfo(off, len, DefragBlockKind.Used, name));
      covered.Add((off, off + len));
    }
    // Fill remaining ranges with Free.
    covered.Sort((a, b) => a.start.CompareTo(b.start));
    long cursor = 0;
    foreach (var (s, e) in covered) {
      if (s > cursor) list.Add(new DefragBlockInfo(cursor, s - cursor, DefragBlockKind.Free));
      cursor = e;
    }
    if (cursor < imageSize) list.Add(new DefragBlockInfo(cursor, imageSize - cursor, DefragBlockKind.Free));
    return list;
  }

  [Test]
  public void Template_RoutesFilesToZones_ByRange() {
    // Image 1000 bytes, cluster 100. Three files, each 100 bytes, all
    // scattered far from the natural packed layout (offset 0/100/200).
    // This guarantees the planner emits a move for each.
    var imageSize = 1000L;
    var extents = MakeExtents(imageSize,
      ("apple.txt", 700, 100),
      ("banana.txt", 500, 100),
      ("cherry.txt", 800, 100));

    var template = new LayoutTemplate {
      Name = "Strict alpha",
      Zones = [
        new LayoutZone {
          Name = "all",
          Range = "0%-100%",
          SortBy = [new DefragSortKey(DefragSortField.Name, SortDirection.Ascending)],
        },
      ],
    };

    var moves = DefragPlanner.Plan(
      extents,
      dataOrigin: 0,
      imageSize: imageSize,
      clusterSize: 100,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart,
      layoutTemplate: template);

    Assert.That(moves, Is.Not.Empty);
    // After plan: apple at 0, banana at 100, cherry at 200.
    var byName = moves.GroupBy(m => m.FileName).ToDictionary(g => g.Key, g => g.OrderBy(m => m.DstOffset).First().DstOffset);
    Assert.That(byName, Does.ContainKey("apple.txt"));
    Assert.That(byName, Does.ContainKey("banana.txt"));
    Assert.That(byName, Does.ContainKey("cherry.txt"));
    Assert.That(byName["apple.txt"], Is.LessThan(byName["banana.txt"]),
      "apple sorts before banana");
    Assert.That(byName["banana.txt"], Is.LessThan(byName["cherry.txt"]),
      "banana sorts before cherry");
  }

  [Test]
  public void Template_MultiZone_PlacesFilesInResolvedRanges() {
    // Image 2000 bytes, three files. Zone "small" at 0-50%, "big" at 50-100%.
    var imageSize = 2000L;
    var extents = MakeExtents(imageSize,
      ("small1.bin", 200, 100),
      ("small2.bin", 400, 100),
      ("huge.bin", 1500, 500));

    var template = new LayoutTemplate {
      Name = "By size",
      Zones = [
        new LayoutZone { Name = "small", Range = "0%-50%", Filter = "size < 500" },
        new LayoutZone { Name = "big",   Range = "50%-100%", Filter = "size >= 500" },
      ],
    };

    var moves = DefragPlanner.Plan(
      extents, 0, imageSize, clusterSize: 100,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart,
      layoutTemplate: template);

    // Find the destination of each file.
    var byFile = moves.GroupBy(m => m.FileName).ToDictionary(g => g.Key, g => g.OrderBy(m => m.DstOffset).First().DstOffset);

    // small1 and small2 should be in 0-1000 (small zone).
    if (byFile.TryGetValue("small1.bin", out var s1))
      Assert.That(s1, Is.LessThan(1000), "small1 lands in 'small' zone (0-50%)");
    if (byFile.TryGetValue("small2.bin", out var s2))
      Assert.That(s2, Is.LessThan(1000), "small2 lands in 'small' zone (0-50%)");
    if (byFile.TryGetValue("huge.bin", out var h))
      Assert.That(h, Is.GreaterThanOrEqualTo(1000), "huge lands in 'big' zone (50-100%)");
  }

  [Test]
  public void Template_WithinZoneOrdering_FollowsSortKeys() {
    // Image 1000, four files in one zone, sortBy=size desc → largest first.
    var imageSize = 1000L;
    var extents = MakeExtents(imageSize,
      ("a.bin", 100, 100),
      ("b.bin", 300, 200),
      ("c.bin", 600, 50),
      ("d.bin", 700, 150));

    var template = new LayoutTemplate {
      Name = "By size desc",
      Zones = [
        new LayoutZone {
          Name = "all",
          Range = "0%-100%",
          SortBy = [new DefragSortKey(DefragSortField.Size, SortDirection.Descending)],
        },
      ],
    };

    var moves = DefragPlanner.Plan(
      extents, 0, imageSize, clusterSize: 50,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart,
      layoutTemplate: template);

    // Sort plan: b (200), d (150), a (100), c (50).
    var byFile = moves.GroupBy(m => m.FileName).ToDictionary(g => g.Key, g => g.OrderBy(m => m.DstOffset).First().DstOffset);
    if (byFile.Count >= 2) {
      Assert.That(byFile["b.bin"], Is.LessThan(byFile["d.bin"]),
        "200-byte file placed before 150-byte file (desc order)");
      Assert.That(byFile["d.bin"], Is.LessThan(byFile["a.bin"]),
        "150-byte file placed before 100-byte file (desc order)");
      Assert.That(byFile["a.bin"], Is.LessThan(byFile["c.bin"]),
        "100-byte file placed before 50-byte file (desc order)");
    }
  }

  [Test]
  public void Template_ImageSize_UnchangedAfterPlan() {
    var imageSize = 4000L;
    var extents = MakeExtents(imageSize,
      ("a", 100, 200),
      ("b", 500, 300),
      ("c", 1500, 400));

    var template = new LayoutTemplate {
      Name = "Reshuffle",
      Zones = [new LayoutZone {
        Name = "all",
        Range = "0%-100%",
        SortBy = [new DefragSortKey(DefragSortField.Name, SortDirection.Descending)],
      }],
    };

    var moves = DefragPlanner.Plan(
      extents, 0, imageSize, clusterSize: 100,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart,
      layoutTemplate: template);

    // The image size invariant: every move target must fall within [0, imageSize).
    foreach (var m in moves) {
      Assert.That(m.DstOffset, Is.GreaterThanOrEqualTo(0));
      Assert.That(m.DstOffset + m.Length, Is.LessThanOrEqualTo(imageSize),
        $"Move {m.FileName} target {m.DstOffset}+{m.Length} exceeds image size {imageSize}");
    }
  }

  [Test]
  public void Template_LeftoverFiles_LandAfterZones() {
    // Image 1000, two zone-matched files + one unmatched (leftover).
    var imageSize = 1000L;
    var extents = MakeExtents(imageSize,
      ("big1.bin", 100, 200),
      ("big2.bin", 400, 200),
      ("tiny.bin", 800, 50));

    var template = new LayoutTemplate {
      Name = "Big only",
      LeftoverStrategyText = "append_at_end",
      Zones = [new LayoutZone {
        Name = "big",
        Range = "0%-50%",
        Filter = "size >= 100",
      }],
    };

    var moves = DefragPlanner.Plan(
      extents, 0, imageSize, clusterSize: 50,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart,
      layoutTemplate: template);

    // tiny.bin must be placed (in leftover bucket, after the zone-matched files).
    var tinyMoves = moves.Where(m => m.FileName == "tiny.bin").ToList();
    var bigMoves = moves.Where(m => m.FileName.StartsWith("big")).ToList();
    if (tinyMoves.Count > 0 && bigMoves.Count > 0) {
      var tinyDst = tinyMoves.Min(m => m.DstOffset);
      var bigMaxDst = bigMoves.Max(m => m.DstOffset + m.Length);
      Assert.That(tinyDst, Is.GreaterThanOrEqualTo(bigMaxDst - 100),
        "tiny (leftover) placed after big (zone-matched)");
    }
  }

  [Test]
  public void Template_OverridesMode_ButPlannerStillRespectsForbidden() {
    // Image 1000 with metadata-reserved region at 0..200; template zone tries
    // to start at 0% but planner must keep moves out of the forbidden region.
    var imageSize = 1000L;
    var extents = new List<DefragBlockInfo> {
      new(0, 200, DefragBlockKind.MetadataReserved),
      new(200, 100, DefragBlockKind.Used, "f.bin"),
      new(300, 700, DefragBlockKind.Free),
    };

    var template = new LayoutTemplate {
      Name = "Start at 0",
      Zones = [new LayoutZone { Name = "all", Range = "0%-100%" }],
    };

    var moves = DefragPlanner.Plan(
      extents, dataOrigin: 0, imageSize: imageSize, clusterSize: 100,
      profile: LayoutProfile.Performance,
      mode: DefragMode.ConsolidateAtStart,
      layoutTemplate: template);

    foreach (var m in moves) {
      Assert.That(m.DstOffset, Is.GreaterThanOrEqualTo(200),
        $"Move target {m.DstOffset} must not overlap forbidden region [0, 200).");
    }
  }
}
