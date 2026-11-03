using System.Reflection;
using Compression.Registry;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// The reader against archives older dar releases wrote (ReferenceVectors/README.md, "Older dar
/// releases"): every archive format from 9 up to 11.2, each with tape marks, without them, gzip,
/// bzip2 and as a slice set, must give back the fixture tree byte for byte.
/// </summary>
[TestFixture]
public class DarLegacyFormatTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() => this._tmp = DarFixtures.TempDirectory("legacy");

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  /// <summary>dar release → the archive format it writes, as its version header says.</summary>
  private static readonly (string Release, int Major, int Minor)[] Releases = [
    ("2.4.24", 8, 1),
    ("2.5.22", 9, 0),
    ("2.6.0", 10, 0),
    ("2.6.16", 10, 1),
    ("2.7.0", 11, 0),
    ("2.7.1", 11, 1),
    ("2.7.10", 11, 2),
    ("2.8.6", 11, 3),
  ];

  private static readonly string[] Variants = ["", "-at", "-gzip", "-bzip2"];

  private static IEnumerable<TestCaseData> Archives()
    => Releases.SelectMany(r => Variants.Select(v =>
      new TestCaseData($"dar-{r.Release}{v}.1.dar", r.Major, r.Minor)
        .SetName($"GivenDar{r.Release.Replace(".", "")}Format{r.Major}_{r.Minor}{v.Replace("-", "_")}_WhenExtracted_ThenTheTreeMatches")));

  private static IEnumerable<TestCaseData> SliceSets()
    => Releases.Select(r => new TestCaseData(r.Release)
      .SetName($"GivenADar{r.Release.Replace(".", "")}SliceSet_WhenTheLastSliceIsOpened_ThenTheTreeMatches"));

  [TestCaseSource(nameof(Archives))]
  public void LegacyArchive(string vector, int major, int minor) {
    using (var ms = new MemoryStream(DarFixtures.Vector(vector))) {
      using var set = DarSliceSet.Open(ms);
      var archive = DarArchive.Read(set.Archive);
      Assert.That((archive.Trailer.Major, archive.Trailer.Minor), Is.EqualTo((major, minor)), "the release writes this format");
      var entries = archive.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
      Assert.That(entries["link"].Kind, Is.EqualTo(DarEntryKind.Symlink));
      Assert.That(entries["link"].LinkTarget, Is.EqualTo("hello.txt"));
      Assert.That(entries["fifo"].Kind, Is.EqualTo(DarEntryKind.Fifo));
      Assert.That(entries["dir"].Permissions & 0xFFF, Is.EqualTo(0x1E9)); // 0751
      Assert.That(entries["noise.bin"].Permissions & 0xFFF, Is.EqualTo(0x180)); // 0600
      // Format 8 keeps whole seconds, 9 microseconds, later ones nanoseconds.
      var precision = major < 9 ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(0.001);
      Assert.That(entries["hello.txt"].LastModified, Is.EqualTo(DarFixtures.HelloModified).Within(precision));
    }
    var outDir = Path.Combine(this._tmp, "out");
    using (var ms = new MemoryStream(DarFixtures.Vector(vector)))
      new DarFormatDescriptor().Extract(ms, outDir, null, null);
    foreach (var (path, content) in DarFixtures.TreeFiles)
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, path)), Is.EqualTo(content), path);
  }

  [TestCaseSource(nameof(SliceSets))]
  public void LegacySliceSet(string release) {
    var names = Assembly.GetExecutingAssembly().GetManifestResourceNames()
      .Where(n => n.StartsWith($"DarVectors.dar-{release}-sliced.", StringComparison.Ordinal))
      .Select(n => n["DarVectors.".Length..])
      .OrderBy(n => int.Parse(n.Split('.')[^2], System.Globalization.CultureInfo.InvariantCulture))
      .ToArray();
    Assert.That(names, Has.Length.GreaterThan(2));
    var paths = DarFixtures.Materialise(this._tmp, names);
    var outDir = Path.Combine(this._tmp, "out");
    using (var fs = File.OpenRead(paths[^1]))
      new DarFormatDescriptor().Extract(fs, outDir, null, null);
    foreach (var (path, content) in DarFixtures.TreeFiles)
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, path)), Is.EqualTo(content), path);
  }
}
