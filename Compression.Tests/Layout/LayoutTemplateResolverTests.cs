#pragma warning disable CS1591
using Compression.Registry.Layout;

namespace Compression.Tests.Layout;

[TestFixture]
public class LayoutTemplateResolverTests {

  private static FilterFileContext MakeFile(
      string name, long size,
      DateTime? lastMod = null,
      IReadOnlyList<long>? allSizes = null,
      IReadOnlyList<DateTime>? allMtimes = null) {
    var ext = name.Contains('.') ? name[name.LastIndexOf('.')..].ToLowerInvariant() : string.Empty;
    return new FilterFileContext {
      Name = name,
      Path = name,
      Extension = ext,
      Size = size,
      LastModified = lastMod,
      AllSizes = allSizes,
      AllLastModifiedTimes = allMtimes,
    };
  }

  [Test]
  public void SingleZone_NoFilter_AllFilesMatch() {
    var template = new LayoutTemplate {
      Name = "Catch-all",
      Zones = [new LayoutZone { Name = "all", Range = "0%-100%" }],
    };
    var files = new IFilterFileContext[] {
      MakeFile("a.txt", 100),
      MakeFile("b.txt", 200),
      MakeFile("c.txt", 300),
    };
    var placed = LayoutTemplateResolver.Resolve(template, files, imageSize: 1000);
    Assert.That(placed, Has.Count.EqualTo(3));
    Assert.That(placed.All(p => p.ZoneName == "all"), Is.True);
    Assert.That(placed.Select(p => p.SortIndex), Is.EquivalentTo(new[] { 0, 1, 2 }));
  }

  [Test]
  public void SortBy_NameAsc_OrdersFilesAlphabetically() {
    var template = new LayoutTemplate {
      Name = "Alpha",
      Zones = [new LayoutZone {
        Name = "all",
        Range = "0%-100%",
        SortBy = [new DefragSortKey(DefragSortField.Name, SortDirection.Ascending)],
      }],
    };
    var files = new IFilterFileContext[] {
      MakeFile("zebra.txt", 100),
      MakeFile("apple.txt", 100),
      MakeFile("mango.txt", 100),
    };
    var placed = LayoutTemplateResolver.Resolve(template, files, 1000);
    // SortIndex 0 should be apple, 1 mango, 2 zebra.
    var byIdx = placed.OrderBy(p => p.SortIndex).Select(p => files[p.FileIndex].Name).ToList();
    Assert.That(byIdx, Is.EqualTo(new[] { "apple.txt", "mango.txt", "zebra.txt" }));
  }

  [Test]
  public void SortBy_SizeDesc_LargestFirst() {
    var template = new LayoutTemplate {
      Name = "BySize",
      Zones = [new LayoutZone {
        Name = "all",
        Range = "0%-100%",
        SortBy = [new DefragSortKey(DefragSortField.Size, SortDirection.Descending)],
      }],
    };
    var files = new IFilterFileContext[] {
      MakeFile("small.txt", 100),
      MakeFile("huge.txt", 10000),
      MakeFile("medium.txt", 500),
    };
    var placed = LayoutTemplateResolver.Resolve(template, files, 100_000);
    var byIdx = placed.OrderBy(p => p.SortIndex).Select(p => files[p.FileIndex].Name).ToList();
    Assert.That(byIdx, Is.EqualTo(new[] { "huge.txt", "medium.txt", "small.txt" }));
  }

  [Test]
  public void MultiZone_RouteByFilter() {
    var allSizes = new long[] { 100, 200, 5000, 10000 };
    var template = new LayoutTemplate {
      Name = "Split",
      Zones = [
        new LayoutZone { Name = "big",   Range = "0%-50%",  Filter = "size >= 5000" },
        new LayoutZone { Name = "small", Range = "50%-100%", Filter = "size < 5000" },
      ],
    };
    var files = new IFilterFileContext[] {
      MakeFile("a", 100, allSizes: allSizes),
      MakeFile("b", 200, allSizes: allSizes),
      MakeFile("c", 5000, allSizes: allSizes),
      MakeFile("d", 10000, allSizes: allSizes),
    };
    var placed = LayoutTemplateResolver.Resolve(template, files, 1000);
    var zoneByName = placed.ToDictionary(p => files[p.FileIndex].Name, p => p.ZoneName);
    Assert.That(zoneByName["a"], Is.EqualTo("small"));
    Assert.That(zoneByName["b"], Is.EqualTo("small"));
    Assert.That(zoneByName["c"], Is.EqualTo("big"));
    Assert.That(zoneByName["d"], Is.EqualTo("big"));
  }

  [Test]
  public void UnmatchedFiles_GoToLeftoverBucket() {
    var template = new LayoutTemplate {
      Name = "Strict",
      Zones = [new LayoutZone {
        Name = "big",
        Range = "0%-50%",
        Filter = "size > 1000",
      }],
    };
    var files = new IFilterFileContext[] {
      MakeFile("tiny.txt", 100),
      MakeFile("huge.txt", 10000),
    };
    var placed = LayoutTemplateResolver.Resolve(template, files, 1000);
    Assert.That(placed.Single(p => files[p.FileIndex].Name == "tiny.txt").ZoneName,
                Is.EqualTo(LayoutTemplateResolver.LeftoverZoneName));
    Assert.That(placed.Single(p => files[p.FileIndex].Name == "huge.txt").ZoneName,
                Is.EqualTo("big"));
  }

  [Test]
  public void ZoneByteBounds_ResolveCorrectly() {
    // Use filters so each file goes to a specific zone.
    var template = new LayoutTemplate {
      Name = "B",
      Zones = [
        new LayoutZone { Name = "front", Range = "0%-25%", Filter = "size < 200" },
        new LayoutZone { Name = "back",  Range = "75%-100%", Filter = "size >= 200" },
      ],
    };
    var files = new IFilterFileContext[] { MakeFile("a", 100), MakeFile("b", 500) };
    var placed = LayoutTemplateResolver.Resolve(template, files, imageSize: 1000);
    var byZone = placed.GroupBy(p => p.ZoneName).ToDictionary(g => g.Key, g => (g.First().ZoneStart, g.First().ZoneEnd));
    Assert.That(byZone["front"], Is.EqualTo((0L, 250L)));
    Assert.That(byZone["back"], Is.EqualTo((750L, 1000L)));
  }

  [Test]
  public void FirstMatchWins_WhenZonesOverlapInFilter() {
    var template = new LayoutTemplate {
      Name = "Overlap",
      Zones = [
        new LayoutZone { Name = "any-big", Range = "0%-50%", Filter = "size > 100" },
        new LayoutZone { Name = "any",     Range = "50%-100%" },
      ],
    };
    var files = new IFilterFileContext[] { MakeFile("a", 200) };
    var placed = LayoutTemplateResolver.Resolve(template, files, 1000);
    Assert.That(placed.Single().ZoneName, Is.EqualTo("any-big"));
  }

  [Test]
  public void QuartileFilter_ResolvesAgainstAllSizes() {
    var sizes = new long[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
    var template = new LayoutTemplate {
      Name = "Quartile",
      Zones = [new LayoutZone {
        Name = "top",
        Range = "0%-100%",
        Filter = "size >= quartile(0.75)",
      }],
    };
    var files = sizes.Select(s => (IFilterFileContext)MakeFile($"f{s}", s, allSizes: sizes)).ToArray();
    var placed = LayoutTemplateResolver.Resolve(template, files, 1000);
    // Files with size >= 70 (75th percentile) should be in zone; others leftover.
    var inZone = placed.Where(p => p.ZoneName == "top").Select(p => files[p.FileIndex].Size).ToList();
    Assert.That(inZone, Has.Member(70L));
    Assert.That(inZone, Has.Member(100L));
    Assert.That(inZone, Does.Not.Contain(10L));
  }

  [Test]
  public void EmptyFileList_ReturnsEmptyResult() {
    var template = new LayoutTemplate { Name = "X", Zones = [] };
    var placed = LayoutTemplateResolver.Resolve(template, Array.Empty<IFilterFileContext>(), 1000);
    Assert.That(placed, Is.Empty);
  }

  [Test]
  public void NullTemplate_Throws() {
    Assert.Throws<ArgumentNullException>(
      () => LayoutTemplateResolver.Resolve(null!, [], 100));
  }

  [Test]
  public void NegativeImageSize_Throws() {
    var template = new LayoutTemplate { Name = "X", Zones = [] };
    Assert.Throws<ArgumentOutOfRangeException>(
      () => LayoutTemplateResolver.Resolve(template, [], -1));
  }
}
