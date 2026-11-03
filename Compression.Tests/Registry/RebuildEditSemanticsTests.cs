using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Registry;

/// <summary>
/// What an entry name means to Add and Remove when the edit goes through an extract → edit →
/// re-create rebuild: the full path, never the leaf alone; a folder name covers the folder with
/// everything below it; folders added on their own, empty ones included, survive.
/// </summary>
/// <remarks>
/// Upstreamed from KsmBl/failBrauwser (vendor/compressionworkbench, tests in
/// helper/FbArchive.Tests/RebuildEditSemanticsTests.cs).
/// </remarks>
[TestFixture]
public sealed class RebuildEditSemanticsTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_semantics_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch (IOException) { }
  }

  /// <summary>A .tar.gz has no native editor, so every edit goes through the rebuild fallback.</summary>
  private string TarGzWithSameNameAtTopAndInFolder() {
    var src = Path.Combine(this._dir, "src");
    Directory.CreateDirectory(Path.Combine(src, "sub"));
    File.WriteAllText(Path.Combine(src, "a.txt"), "root");
    File.WriteAllText(Path.Combine(src, "sub", "a.txt"), "nested");
    File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "other");
    var archive = Path.Combine(this._dir, "t.tar.gz");
    ArchiveOperations.Create(archive, [
      new ArchiveInput("", "sub/"),
      new ArchiveInput(Path.Combine(src, "a.txt"), "a.txt"),
      new ArchiveInput(Path.Combine(src, "sub", "a.txt"), "sub/a.txt"),
      new ArchiveInput(Path.Combine(src, "sub", "b.txt"), "sub/b.txt"),
    ], new CompressionOptions());
    return archive;
  }

  private static string[] Names(string archive)
    => [.. ArchiveOperations.List(archive, null).Select(e => e.Name.Replace('\\', '/').Trim('/')).Order(StringComparer.Ordinal)];

  [Test, Category("HappyPath")]
  public void GivenSameNameAtTopAndInFolder_WhenTheTopOneIsRemovedThroughARebuild_ThenTheFolderOneStays() {
    var archive = this.TarGzWithSameNameAtTopAndInFolder();

    ArchiveOperations.Remove(archive, ["a.txt"]);

    Assert.That(Names(archive), Is.EqualTo(new[] { "sub", "sub/a.txt", "sub/b.txt" }));
  }

  [Test, Category("HappyPath")]
  public void GivenAFolder_WhenItIsRemovedThroughARebuild_ThenItGoesWithEverythingBelowIt() {
    var archive = this.TarGzWithSameNameAtTopAndInFolder();

    ArchiveOperations.Remove(archive, ["sub/"]);

    Assert.That(Names(archive), Is.EqualTo(new[] { "a.txt" }));
  }

  [Test, Category("HappyPath")]
  public void GivenAnArchive_WhenAnEmptyFolderIsAddedThroughARebuild_ThenTheFolderIsKept() {
    var archive = this.TarGzWithSameNameAtTopAndInFolder();

    ArchiveOperations.Add(archive, [new ArchiveInput("", "empty/")]);

    Assert.That(Names(archive), Does.Contain("empty"));
  }

  // ── staging ──────────────────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenADirectoryInput_WhenStaged_ThenAnEmptyFolderExists() {
    var stage = Directory.CreateDirectory(Path.Combine(this._dir, "stage")).FullName;

    RebuildVerb.StageInputs(stage, [new ArchiveInputInfo("", "empty/deeper/", true)]);

    Assert.That(Directory.Exists(Path.Combine(stage, "empty", "deeper")), Is.True);
  }

  [Test, Category("HappyPath")]
  public void GivenAFileInput_WhenStaged_ThenItKeepsItsLastWriteTime() {
    var stage = Directory.CreateDirectory(Path.Combine(this._dir, "stage")).FullName;
    var source = Path.Combine(this._dir, "old.txt");
    File.WriteAllText(source, "old");
    var when = new DateTime(2024, 1, 2, 3, 4, 6, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(source, when);

    RebuildVerb.StageInputs(stage, [new ArchiveInputInfo(source, "sub/old.txt", false)]);

    Assert.That(File.GetLastWriteTimeUtc(Path.Combine(stage, "sub", "old.txt")), Is.EqualTo(when));
  }

  [Test, Category("HappyPath")]
  public void GivenAnInMemoryInputWithMetadata_WhenStaged_ThenItCarriesTheStatedTime() {
    var stage = Directory.CreateDirectory(Path.Combine(this._dir, "stage")).FullName;
    var when = new DateTimeOffset(2023, 7, 8, 9, 10, 12, TimeSpan.Zero);

    RebuildVerb.StageInputs(stage, [ArchiveInputInfo.InMemory("m.txt", "m"u8, new ArchiveEntryMetadata(LastWriteTimeUtc: when))]);

    Assert.That(File.GetLastWriteTimeUtc(Path.Combine(stage, "m.txt")), Is.EqualTo(when.UtcDateTime));
  }

  [TestCase("../outside.txt")]
  [TestCase("a/../../outside.txt")]
  [TestCase("")]
  [TestCase("/")]
  [Category("Exception")]
  public void GivenANameThatLeavesTheTree_WhenStaged_ThenNothingIsWrittenOutside(string name) {
    var stage = Directory.CreateDirectory(Path.Combine(this._dir, "stage")).FullName;
    var outside = Path.Combine(this._dir, "outside.txt");
    File.WriteAllText(outside, "keep");

    RebuildVerb.StageInputs(stage, [ArchiveInputInfo.InMemory(name, "overwrite"u8)]);

    Assert.Multiple(() => {
      Assert.That(File.ReadAllText(outside), Is.EqualTo("keep"));
      Assert.That(Directory.GetFileSystemEntries(stage), Is.Empty);
    });
  }

  // ── large-volume rebuild ─────────────────────────────────────────────────────────────────

  private MemoryStream TarWith(params (string Name, string? Text)[] entries) {
    var tar = new FileFormat.Tar.TarFormatDescriptor();
    var stream = new MemoryStream();
    tar.Create(stream, [
      .. entries.Select(e => e.Text is null
        ? new ArchiveInputInfo("", e.Name, true)
        : ArchiveInputInfo.InMemory(e.Name, System.Text.Encoding.UTF8.GetBytes(e.Text))),
    ], new FormatCreateOptions());
    return stream;
  }

  private static string[] TarNames(Stream archive) {
    archive.Position = 0;
    return [.. new FileFormat.Tar.TarFormatDescriptor().List(archive, null).Select(e => e.Name.Trim('/')).Order(StringComparer.Ordinal)];
  }

  [Test, Category("HappyPath")]
  public void GivenSameNameAtTopAndInFolder_WhenTheTopOneIsRemovedFromALargeVolume_ThenTheFolderOneStays() {
    var tar = new FileFormat.Tar.TarFormatDescriptor();
    using var archive = this.TarWith(("sub/", null), ("a.txt", "root"), ("sub/a.txt", "nested"), ("empty/", null));

    ModifyRebuilder.RemoveLargeVolume(archive, ["a.txt"], tar, tar);

    Assert.That(TarNames(archive), Is.EqualTo(new[] { "empty", "sub", "sub/a.txt" }));
  }

  [Test, Category("HappyPath")]
  public void GivenAFolder_WhenItIsRemovedFromALargeVolume_ThenItGoesWithItsContents() {
    var tar = new FileFormat.Tar.TarFormatDescriptor();
    using var archive = this.TarWith(("sub/", null), ("a.txt", "root"), ("sub/a.txt", "nested"));

    ModifyRebuilder.RemoveLargeVolume(archive, ["sub/"], tar, tar);

    Assert.That(TarNames(archive), Is.EqualTo(new[] { "a.txt" }));
  }

  [Test, Category("HappyPath")]
  public void GivenALargeVolume_WhenAnEmptyFolderIsAdded_ThenItAndTheExistingFoldersAreKept() {
    var tar = new FileFormat.Tar.TarFormatDescriptor();
    using var archive = this.TarWith(("kept/", null), ("a.txt", "root"));

    ModifyRebuilder.AddLargeVolume(archive, [new ArchiveInputInfo("", "new/", true)], tar, tar);

    Assert.That(TarNames(archive), Is.EqualTo(new[] { "a.txt", "kept", "new" }));
  }

  // ── a descriptor with its own rebuild path ───────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenAComicArchiveWithSameNames_WhenTheTopOneIsRemoved_ThenTheFolderOneStays() {
    var cb7 = new FileFormat.Cb7.Cb7FormatDescriptor();
    using var archive = new MemoryStream();
    cb7.Create(archive, [
      ArchiveInputInfo.InMemory("01.png", "top"u8),
      ArchiveInputInfo.InMemory("extra/01.png", "nested"u8),
    ], new FormatCreateOptions());

    cb7.Remove(archive, ["01.png"]);

    archive.Position = 0;
    Assert.That(cb7.List(archive, null).Where(e => !e.IsDirectory).Select(e => e.Name.Replace('\\', '/')),
      Is.EquivalentTo(new[] { "extra/01.png" }));
  }
}
