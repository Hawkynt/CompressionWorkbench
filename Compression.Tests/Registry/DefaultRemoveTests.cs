using Compression.Registry;
using FileFormat.Cab;

namespace Compression.Tests.Registry;

/// <summary>
/// The extract → drop → re-create removal every modifiable format without a native editor falls
/// back on. It must drop exactly the entries named: a file elsewhere in the tree that merely shares
/// a name with one being removed is a different file.
/// </summary>
[TestFixture]
public sealed class DefaultRemoveTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb-rm-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, recursive: true); } catch { }
  }

  private void Files(params string[] relativePaths) {
    foreach (var rel in relativePaths) {
      var full = Path.Combine(this._dir, rel.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(full)!);
      File.WriteAllText(full, rel);
    }
  }

  private string[] Remaining() => [
    .. Directory.GetFiles(this._dir, "*", SearchOption.AllDirectories)
      .Select(f => Path.GetRelativePath(this._dir, f).Replace('\\', '/'))
      .Order(StringComparer.Ordinal),
  ];

  private string[] RemainingFolders() => [
    .. Directory.GetDirectories(this._dir, "*", SearchOption.AllDirectories)
      .Select(f => Path.GetRelativePath(this._dir, f).Replace('\\', '/'))
      .Order(StringComparer.Ordinal),
  ];

  // ── the matching rule ───────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenTheSameNameAtTheTopAndInAFolder_WhenTheTopOneIsRemoved_ThenTheOtherSurvives() {
    this.Files("readme.txt", "docs/readme.txt");

    RebuildVerb.DeleteNamedEntries(this._dir, ["readme.txt"]);

    Assert.That(this.Remaining(), Is.EqualTo(new[] { "docs/readme.txt" }),
      "matching on the file name alone deleted every readme.txt in the archive");
  }

  [Test]
  public void GivenTheSameNameAtTheTopAndInAFolder_WhenTheFolderOneIsRemoved_ThenTheTopOneSurvives() {
    this.Files("readme.txt", "docs/readme.txt");

    RebuildVerb.DeleteNamedEntries(this._dir, ["docs/readme.txt"]);

    Assert.That(this.Remaining(), Is.EqualTo(new[] { "readme.txt" }));
  }

  [TestCase("DOCS/README.TXT")]
  [TestCase("docs\\readme.txt")]
  [TestCase("/docs/readme.txt")]
  public void GivenANameSpelledDifferently_WhenRemoved_ThenTheSameEntryIsMatched(string spelling) {
    this.Files("docs/readme.txt", "keep.txt");

    RebuildVerb.DeleteNamedEntries(this._dir, [spelling]);

    Assert.That(this.Remaining(), Is.EqualTo(new[] { "keep.txt" }),
      "entry names are matched case-insensitively and with either separator, as before");
  }

  [Test]
  public void GivenAFolderName_WhenRemoved_ThenTheFolderAndEverythingInItGo() {
    this.Files("docs/a.txt", "docs/deep/b.txt", "docsextra/c.txt");

    RebuildVerb.DeleteNamedEntries(this._dir, ["docs/"]);

    Assert.Multiple(() => {
      Assert.That(this.Remaining(), Is.EqualTo(new[] { "docsextra/c.txt" }),
        "a sibling whose name merely starts the same is not inside the folder");
      Assert.That(this.RemainingFolders(), Is.EqualTo(new[] { "docsextra" }),
        "an emptied folder left behind would be re-created as an entry");
    });
  }

  private static readonly TestCaseData[] NamesMatchingNothing = [
    new TestCaseData((object)Array.Empty<string>()).SetName("no names"),
    new TestCaseData((object)new[] { "missing.txt" }).SetName("a name that is not there"),
    new TestCaseData((object)new[] { "" }).SetName("an empty name"),
    new TestCaseData((object)new[] { "../outside.txt" }).SetName("a name that climbs out"),
  ];

  [TestCaseSource(nameof(NamesMatchingNothing))]
  public void GivenNamesThatMatchNothing_WhenRemoved_ThenNothingChanges(string[] names) {
    this.Files("a.txt", "docs/b.txt");
    var outside = Path.Combine(Path.GetDirectoryName(this._dir)!, "outside.txt");

    RebuildVerb.DeleteNamedEntries(this._dir, names);

    Assert.That(this.Remaining(), Is.EqualTo(new[] { "a.txt", "docs/b.txt" }));
    Assert.That(File.Exists(outside), Is.False);
  }

  // ── end to end, through a format that uses the default ─────────────────────────────────────

  [Test]
  public void GivenACabinetWithTwoFilesOfOneName_WhenOneIsRemoved_ThenTheOtherIsStillInIt() {
    this.Files("src/readme.txt", "src/docs/readme.txt");
    var descriptor = new CabFormatDescriptor();
    using var cab = new MemoryStream();
    descriptor.Create(cab, [
      new ArchiveInputInfo(Path.Combine(this._dir, "src", "readme.txt"), "readme.txt", false),
      new ArchiveInputInfo(Path.Combine(this._dir, "src", "docs", "readme.txt"), "docs/readme.txt", false),
    ], new FormatCreateOptions());

    ((IArchiveModifiable)descriptor).Remove(cab, ["readme.txt"]);

    cab.Position = 0;
    var names = descriptor.List(cab, null).Select(e => e.Name.Replace('\\', '/')).ToList();
    Assert.That(names, Is.EqualTo(new[] { "docs/readme.txt" }));
  }
}
