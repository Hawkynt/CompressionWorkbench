using System.IO.Compression;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Cab;

namespace Compression.Tests.Registry;

/// <summary>
/// Renaming entries inside an archive: the move rule applied to an extracted tree, the default
/// rebuild every modifiable format inherits, and the library entry point the shell calls.
/// </summary>
[TestFixture]
public sealed class ArchiveRenameTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb-mv-" + Guid.NewGuid().ToString("N"));
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

  /// <summary>Every file as "path=content", so a move that loses or swaps content shows.</summary>
  private string[] Tree() => [
    .. Directory.GetFiles(this._dir, "*", SearchOption.AllDirectories)
      .Select(f => Path.GetRelativePath(this._dir, f).Replace('\\', '/') + "=" + File.ReadAllText(f))
      .Order(StringComparer.Ordinal),
  ];

  private void Move(params (string From, string To)[] renames)
    => RebuildVerb.MoveEntries(this._dir, [.. renames.Select(r => new ArchiveRename(r.From, r.To))]);

  // ── moving within an extracted tree ─────────────────────────────────────────────────────────

  [Test]
  public void GivenAFile_WhenRenamedInItsFolder_ThenItKeepsItsContentUnderTheNewName() {
    this.Files("docs/old.txt", "docs/other.txt");

    this.Move(("docs/old.txt", "docs/new.txt"));

    Assert.That(this.Tree(), Is.EqualTo(new[] { "docs/new.txt=docs/old.txt", "docs/other.txt=docs/other.txt" }));
  }

  [Test]
  public void GivenAFile_WhenMovedIntoAFolderThatDoesNotExistYet_ThenTheFolderIsCreated() {
    this.Files("a.txt");

    this.Move(("a.txt", "new/deeper/a.txt"));

    Assert.That(this.Tree(), Is.EqualTo(new[] { "new/deeper/a.txt=a.txt" }));
  }

  [Test]
  public void GivenAFolder_WhenRenamed_ThenEverythingBeneathItMovesWithIt() {
    this.Files("docs/a.txt", "docs/deep/b.txt", "docsextra/c.txt");

    this.Move(("docs/", "manual"));

    Assert.That(this.Tree(), Is.EqualTo(new[] {
      "docsextra/c.txt=docsextra/c.txt", "manual/a.txt=docs/a.txt", "manual/deep/b.txt=docs/deep/b.txt",
    }), "a sibling whose name merely starts the same stays where it is");
  }

  [TestCase("readme.txt", "README.txt")]
  [TestCase("docs", "Docs")]
  public void GivenAChangeOfCaseOnly_WhenRenamed_ThenTheNewCaseIsKept(string from, string to) {
    this.Files("readme.txt", "docs/a.txt");

    this.Move((from, to));

    var names = Directory.GetFileSystemEntries(this._dir).Select(Path.GetFileName).ToList();
    Assert.That(names, Does.Contain(to), "on a case-insensitive disk the target looks taken; it is the source");
    Assert.That(names, Does.Not.Contain(from));
  }

  [Test]
  public void GivenTheSameName_WhenRenamed_ThenNothingHappens() {
    this.Files("a.txt");

    this.Move(("a.txt", "/a.txt"));

    Assert.That(this.Tree(), Is.EqualTo(new[] { "a.txt=a.txt" }));
  }

  [Test]
  public void GivenRenamesThatChain_WhenApplied_ThenTheyRunInOrder() {
    this.Files("a.txt");

    this.Move(("a.txt", "b.txt"), ("b.txt", "c.txt"));

    Assert.That(this.Tree(), Is.EqualTo(new[] { "c.txt=a.txt" }));
  }

  // ── what is refused ─────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenATakenName_WhenRenamedOnto_ThenItIsRefusedAndNothingIsOverwritten() {
    this.Files("a.txt", "b.txt");

    Assert.Throws<IOException>(() => this.Move(("a.txt", "b.txt")));
    Assert.That(this.Tree(), Is.EqualTo(new[] { "a.txt=a.txt", "b.txt=b.txt" }));
  }

  [Test]
  public void GivenATakenNameInADifferentCase_WhenRenamedOnto_ThenItIsRefused() {
    this.Files("a.txt", "B.txt");

    Assert.Throws<IOException>(() => this.Move(("a.txt", "b.txt")),
      "two names differing only in case cannot both be extracted on Windows or macOS");
  }

  [Test]
  public void GivenAFolder_WhenMovedIntoItself_ThenItIsRefused() {
    this.Files("docs/a.txt");

    Assert.Throws<IOException>(() => this.Move(("docs", "docs/inner")));
    Assert.That(this.Tree(), Is.EqualTo(new[] { "docs/a.txt=docs/a.txt" }));
  }

  [Test]
  public void GivenANameThatIsNotThere_WhenRenamed_ThenItSaysSo()
    => Assert.Throws<FileNotFoundException>(() => this.Move(("missing.txt", "x.txt")));

  [TestCase("", "x.txt")]
  [TestCase("/", "x.txt")]
  [TestCase("a.txt", "")]
  [TestCase("a.txt", "../outside.txt")]
  [TestCase("a.txt", "docs/../../outside.txt")]
  [TestCase("a.txt", "./a2.txt")]
  [TestCase("a.txt", "docs//a2.txt")]
  public void GivenAPathThatIsEmptyOrLeavesTheTree_WhenUsed_ThenItIsRejected(string from, string to) {
    this.Files("a.txt");

    Assert.Throws<ArgumentException>(() => this.Move((from, to)));
    Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(this._dir)!, "outside.txt")), Is.False);
    Assert.That(this.Tree(), Is.EqualTo(new[] { "a.txt=a.txt" }));
  }

  // ── through a format ────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenACabinet_WhenAFolderIsRenamedThroughTheDefault_ThenItsFilesListUnderTheNewName() {
    this.Files("src/docs/a.txt", "src/top.txt");
    var descriptor = new CabFormatDescriptor();
    using var cab = new MemoryStream();
    descriptor.Create(cab, [
      new ArchiveInputInfo(Path.Combine(this._dir, "src", "docs", "a.txt"), "docs/a.txt", false),
      new ArchiveInputInfo(Path.Combine(this._dir, "src", "top.txt"), "top.txt", false),
    ], new FormatCreateOptions());

    ((IArchiveModifiable)descriptor).Rename(cab, [new ArchiveRename("docs", "manual")]);

    cab.Position = 0;
    Assert.That(descriptor.List(cab, null).Select(e => e.Name.Replace('\\', '/')).Order(),
      Is.EqualTo(new[] { "manual/a.txt", "top.txt" }));
  }

  [Test]
  public void GivenAZipOnDisk_WhenAnEntryIsRenamed_ThenTheArchiveHoldsItUnderTheNewNameWithItsBytes() {
    var zip = Path.Combine(this._dir, "a.zip");
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) {
      using (var w = new StreamWriter(archive.CreateEntry("docs/old.txt").Open())) w.Write("payload");
      using (var w = new StreamWriter(archive.CreateEntry("keep.txt").Open())) w.Write("keep");
    }

    ArchiveOperations.Rename(zip, [new ArchiveRename("docs/old.txt", "docs/new.txt")]);

    using var reread = ZipFile.OpenRead(zip);
    Assert.That(reread.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith('/')).Select(e => e.FullName).Order(),
      Is.EqualTo(new[] { "docs/new.txt", "keep.txt" }));
    using var r = new StreamReader(reread.GetEntry("docs/new.txt")!.Open());
    Assert.That(r.ReadToEnd(), Is.EqualTo("payload"));
  }

  [Test]
  public void GivenAFormatThatCannotBeModified_WhenRenamed_ThenItIsRefusedAndTheFileIsUntouched() {
    var gz = Path.Combine(this._dir, "a.txt.gz");
    using (var gzip = new GZipStream(File.Create(gz), CompressionLevel.Optimal))
      gzip.Write("single stream"u8);
    var before = File.ReadAllBytes(gz);

    Assert.Throws<NotSupportedException>(() => ArchiveOperations.Rename(gz, [new ArchiveRename("a.txt", "b.txt")]));
    Assert.That(File.ReadAllBytes(gz), Is.EqualTo(before));
  }
}
