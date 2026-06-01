#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Layout;

namespace Compression.Tests.Layout;

[TestFixture]
public class LayoutTemplateJsonTests {

  [Test]
  public void RoundTrip_PreservesAllFields() {
    var template = new LayoutTemplate {
      Name = "Test template",
      MetadataZone = MetadataZone.Middle,
      LeftoverStrategyText = "append_at_end",
      Zones = [
        new LayoutZone {
          Name = "hot",
          Range = "0%-30%",
          Filter = "lastModified > today() - days(30)",
          SortBy = [
            new DefragSortKey(DefragSortField.LastModified, SortDirection.Descending),
            new DefragSortKey(DefragSortField.Size, SortDirection.Descending),
          ],
        },
        new LayoutZone {
          Name = "cold",
          Range = "70%-100%",
          Filter = null,
          SortBy = [],
        },
      ],
    };

    var json = template.ToJson();
    var parsed = LayoutTemplate.FromJson(json);

    Assert.That(parsed.Name, Is.EqualTo("Test template"));
    Assert.That(parsed.MetadataZone, Is.EqualTo(MetadataZone.Middle));
    Assert.That(parsed.LeftoverStrategy, Is.EqualTo(LeftoverStrategy.AppendAtEnd));
    Assert.That(parsed.Zones, Has.Count.EqualTo(2));
    Assert.That(parsed.Zones[0].Name, Is.EqualTo("hot"));
    Assert.That(parsed.Zones[0].Range, Is.EqualTo("0%-30%"));
    Assert.That(parsed.Zones[0].Filter, Is.EqualTo("lastModified > today() - days(30)"));
    Assert.That(parsed.Zones[0].SortBy, Has.Count.EqualTo(2));
    Assert.That(parsed.Zones[0].SortBy[0].Field, Is.EqualTo(DefragSortField.LastModified));
    Assert.That(parsed.Zones[1].Name, Is.EqualTo("cold"));
    Assert.That(parsed.Zones[1].Filter, Is.Null);
  }

  [Test]
  public void FromJson_MinimalTemplate_ParsesWithDefaults() {
    var json = """
      {
        "name": "Minimal",
        "zones": []
      }
      """;
    var t = LayoutTemplate.FromJson(json);
    Assert.That(t.Name, Is.EqualTo("Minimal"));
    Assert.That(t.MetadataZone, Is.EqualTo(MetadataZone.Unchanged));
    Assert.That(t.LeftoverStrategy, Is.EqualTo(LeftoverStrategy.FillGaps));
    Assert.That(t.Zones, Is.Empty);
  }

  [Test]
  public void FromJson_Example_FromSpec_Parses() {
    var json = """
      {
        "name": "Hot at start, cold at end",
        "metadataZone": "Middle",
        "leftoverStrategy": "fill_gaps",
        "zones": [
          { "name": "boot",   "range": "0%-5%",   "sortBy": ["name asc"] },
          { "name": "hot",    "range": "5%-40%",  "filter": "lastModified >= quartile(0.75)",
            "sortBy": ["lastModified desc", "size desc"] },
          { "name": "frozen", "range": "85%-100%","filter": "lastModified <= quartile(0.25)" }
        ]
      }
      """;
    var t = LayoutTemplate.FromJson(json);
    Assert.That(t.Name, Is.EqualTo("Hot at start, cold at end"));
    Assert.That(t.Zones, Has.Count.EqualTo(3));
    Assert.That(t.Zones[0].SortBy[0].Field, Is.EqualTo(DefragSortField.Name));
    Assert.That(t.Zones[1].SortBy, Has.Count.EqualTo(2));
    Assert.That(t.Zones[1].SortBy[1].Direction, Is.EqualTo(SortDirection.Descending));
  }

  [Test]
  public void FromJson_MissingName_Throws() {
    var ex = Assert.Throws<FormatException>(() => LayoutTemplate.FromJson("""{ "zones": [] }"""));
    Assert.That(ex!.Message, Does.Contain("name"));
  }

  [Test]
  public void FromJson_MissingZoneName_Throws() {
    var json = """
      {
        "name": "Bad",
        "zones": [ { "range": "0%-10%" } ]
      }
      """;
    var ex = Assert.Throws<FormatException>(() => LayoutTemplate.FromJson(json));
    Assert.That(ex!.Message, Does.Contain("name"));
  }

  [Test]
  public void FromJson_MissingZoneRange_Throws() {
    var json = """
      {
        "name": "Bad",
        "zones": [ { "name": "z1" } ]
      }
      """;
    var ex = Assert.Throws<FormatException>(() => LayoutTemplate.FromJson(json));
    Assert.That(ex!.Message, Does.Contain("range"));
  }

  [Test]
  public void FromJson_InvalidRange_Throws() {
    var json = """
      {
        "name": "Bad",
        "zones": [ { "name": "z1", "range": "garbage" } ]
      }
      """;
    Assert.Throws<FormatException>(() => LayoutTemplate.FromJson(json));
  }

  [Test]
  public void FromJson_InvalidFilter_Throws() {
    var json = """
      {
        "name": "Bad",
        "zones": [ { "name": "z1", "range": "0%-10%", "filter": "this is not a filter" } ]
      }
      """;
    Assert.Throws<FormatException>(() => LayoutTemplate.FromJson(json));
  }

  [Test]
  public void FromJson_InvalidSortKey_Throws() {
    var json = """
      {
        "name": "Bad",
        "zones": [ { "name": "z1", "range": "0%-10%", "sortBy": ["nonsense field"] } ]
      }
      """;
    Assert.Throws<FormatException>(() => LayoutTemplate.FromJson(json));
  }

  [Test]
  public void FromJson_UnknownMetadataZone_Throws() {
    var json = """
      {
        "name": "Bad",
        "metadataZone": "Outerspace",
        "zones": []
      }
      """;
    Assert.Throws<FormatException>(() => LayoutTemplate.FromJson(json));
  }

  [Test]
  public void FromJson_MalformedJson_Throws() {
    Assert.Throws<FormatException>(() => LayoutTemplate.FromJson("{ not json"));
  }

  [Test]
  public void SaveLoad_RoundTripsThroughFile() {
    var template = new LayoutTemplate {
      Name = "Persist",
      Zones = [new LayoutZone { Name = "z", Range = "0%-100%" }],
    };
    var tmp = Path.Combine(Path.GetTempPath(), $"layout-test-{Guid.NewGuid():N}.json");
    try {
      template.Save(tmp);
      var loaded = LayoutTemplate.Load(tmp);
      Assert.That(loaded.Name, Is.EqualTo("Persist"));
      Assert.That(loaded.Zones, Has.Count.EqualTo(1));
    } finally {
      if (File.Exists(tmp)) File.Delete(tmp);
    }
  }
}
