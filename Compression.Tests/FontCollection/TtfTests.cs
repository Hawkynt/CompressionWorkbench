#pragma warning disable CS1591
using FileFormat.FontCollection;

namespace Compression.Tests.FontCollection;

[TestFixture]
public class TtfTests {
  private static string? FindSystemTtf() {
    string[] candidates = [
      @"C:\Windows\Fonts\arial.ttf",
      @"C:\Windows\Fonts\calibri.ttf",
      @"C:\Windows\Fonts\consola.ttf",
      @"C:\Windows\Fonts\tahoma.ttf",
    ];
    return candidates.FirstOrDefault(File.Exists);
  }

  [Test]
  public void Arial_ListsManyGlyphs() {
    var path = FindSystemTtf();
    if (path == null) Assert.Ignore("No system .ttf available");

    using var fs = File.OpenRead(path!);
    var desc = new TtfFormatDescriptor();
    var entries = desc.List(fs, null);
    Assert.That(entries.Count, Is.GreaterThan(100),
      "System fonts typically have hundreds to thousands of glyphs");
  }

  [Test]
  public void Arial_GlyphEntriesContainSvgHeader() {
    var path = FindSystemTtf();
    if (path == null) Assert.Ignore("No system .ttf available");

    using var fs = File.OpenRead(path!);
    var outDir = Path.Combine(Path.GetTempPath(), "ttf_test_" + Guid.NewGuid().ToString("N"));
    try {
      // Extract just the first few glyphs by filename.
      var entries = new TtfFormatDescriptor().List(fs, null);
      var sample = entries.Skip(10).Take(5).Select(e => e.Name).ToArray();

      fs.Position = 0;
      new TtfFormatDescriptor().Extract(fs, outDir, null, sample);
      foreach (var e in sample) {
        var full = Path.Combine(outDir, e);
        Assert.That(File.Exists(full), Is.True, $"Expected {e}");
        var text = File.ReadAllText(full);
        Assert.That(text, Does.StartWith("<svg "));
      }
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }
  }

  [Test]
  public void Arial_GlyphNamesIncludeUnicodeCodepoints() {
    var path = FindSystemTtf();
    if (path == null) Assert.Ignore("No system .ttf available");

    using var fs = File.OpenRead(path!);
    var entries = new TtfFormatDescriptor().List(fs, null);
    // Latin 'A' is guaranteed to have a glyph in every Latin-alphabet font
    Assert.That(entries.Any(e => e.Name.Contains("U+0041")),
      $"Expected at least one glyph named for U+0041 ('A'); got e.g. {entries.Take(3).Select(e => e.Name).FirstOrDefault()}");
  }
}
