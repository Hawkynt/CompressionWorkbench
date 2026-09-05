#pragma warning disable CS1591
using Compression.Registry.Layout;

namespace Compression.Tests.Lib;

/// <summary>
/// The built-in layout profiles under <c>templates/</c> are discovered by a
/// directory scan, never by name, and <see cref="Compression.Lib.Layout.LayoutProfileStore.List"/>
/// skips a profile that fails to parse <em>silently</em> — a template that
/// stops matching the schema disappears from the editor instead of failing.
/// These tests are the only thing standing between a shipped template and
/// that silent removal, so they parse each one eagerly and assert the parts
/// the editor actually shows.
/// </summary>
[TestFixture]
public class ShippedLayoutTemplatesTests {

  private static string FindRepositoryDirectory(string name) {
    for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent) {
      var path = Path.Combine(current.FullName, name);
      if (Directory.Exists(path)) return path;
    }
    throw new DirectoryNotFoundException($"Could not locate repository directory '{name}' from '{AppContext.BaseDirectory}'.");
  }

  private static IEnumerable<string> ShippedTemplates()
    => Directory.EnumerateFiles(FindRepositoryDirectory("templates"), "*.json").OrderBy(p => p, StringComparer.Ordinal);

  [Test, Category("HappyPath")]
  public void TemplatesDirectory_IsNotEmpty() {
    Assert.That(ShippedTemplates().ToList(), Is.Not.Empty,
      "templates/ ships the built-in layout profiles; an empty directory means the store has nothing to offer.");
  }

  private static LayoutTemplate LoadOrFail(string path) {
    try {
      return LayoutTemplate.Load(path);
    } catch (Exception ex) {
      Assert.Fail($"'{Path.GetFileName(path)}' no longer parses against the current schema, "
        + $"so LayoutProfileStore would drop it silently: {ex.Message}");
      throw;
    }
  }

  [Test, Category("HappyPath")]
  public void EveryShippedTemplate_Parses() {
    foreach (var path in ShippedTemplates()) {
      var template = LoadOrFail(path);

      Assert.Multiple(() => {
        Assert.That(template.Name, Is.Not.Empty, $"'{Path.GetFileName(path)}' has no display name.");
        Assert.That(template.Zones, Is.Not.Empty, $"'{Path.GetFileName(path)}' declares no zones.");
      });
    }
  }

  [Test, Category("HappyPath")]
  public void EveryShippedTemplate_HasParseableZones() {
    foreach (var path in ShippedTemplates()) {
      var template = LoadOrFail(path);
      var file = Path.GetFileName(path);

      foreach (var zone in template.Zones) {
        Assert.DoesNotThrow(() => RangeSpec.Parse(zone.Range),
          $"'{file}' zone '{zone.Name}' has an unparseable range '{zone.Range}'.");

        if (!string.IsNullOrWhiteSpace(zone.Filter))
          Assert.DoesNotThrow(() => FilterExpression.Parse(zone.Filter),
            $"'{file}' zone '{zone.Name}' has an unparseable filter '{zone.Filter}'.");
      }
    }
  }

  [Test, Category("Boundary")]
  public void EveryShippedTemplate_RoundTripsThroughJson() {
    foreach (var path in ShippedTemplates()) {
      var original = LoadOrFail(path);
      var reparsed = LayoutTemplate.FromJson(original.ToJson());

      Assert.Multiple(() => {
        Assert.That(reparsed.Name, Is.EqualTo(original.Name));
        Assert.That(reparsed.MetadataZone, Is.EqualTo(original.MetadataZone));
        Assert.That(reparsed.LeftoverStrategy, Is.EqualTo(original.LeftoverStrategy));
        Assert.That(reparsed.Zones.Select(z => z.Name), Is.EqualTo(original.Zones.Select(z => z.Name)));
      });
    }
  }
}
