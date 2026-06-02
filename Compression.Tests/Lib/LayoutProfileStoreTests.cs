#pragma warning disable CS1591
using Compression.Lib.Layout;
using Compression.Registry;
using Compression.Registry.Layout;

namespace Compression.Tests.Lib;

/// <summary>
/// Exercises <see cref="LayoutProfileStore"/>'s CRUD over two temp
/// directories standing in for the built-in <c>templates/</c> and user
/// <c>%APPDATA%/CompressionWorkbench/profiles</c> roots. The store
/// distinguishes Built-in vs User by directory of origin and refuses
/// to delete or overwrite built-ins.
/// </summary>
[TestFixture]
public class LayoutProfileStoreTests {

  private string _builtinDir = "";
  private string _userDir = "";

  [SetUp]
  public void SetUp() {
    this._builtinDir = Path.Combine(Path.GetTempPath(), "cwb-pstore-builtin-" + Guid.NewGuid().ToString("N"));
    this._userDir = Path.Combine(Path.GetTempPath(), "cwb-pstore-user-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._builtinDir);
    Directory.CreateDirectory(this._userDir);
    LayoutProfileStore.BuiltinDirectoryOverride = this._builtinDir;
    LayoutProfileStore.UserDirectoryOverride = this._userDir;
  }

  [TearDown]
  public void TearDown() {
    LayoutProfileStore.BuiltinDirectoryOverride = null;
    LayoutProfileStore.UserDirectoryOverride = null;
    TryDeleteTree(this._builtinDir);
    TryDeleteTree(this._userDir);
  }

  [Test, Category("HappyPath")]
  public void List_CombinesBuiltinAndUser() {
    SampleTemplate("Built-in A").Save(Path.Combine(this._builtinDir, "a.json"));
    SampleTemplate("User B").Save(Path.Combine(this._userDir, "b.json"));

    var list = LayoutProfileStore.List();

    Assert.That(list, Has.Count.EqualTo(2));
    Assert.That(list.Any(e => e.Name == "Built-in A" && e.Origin == ProfileOrigin.Builtin), Is.True);
    Assert.That(list.Any(e => e.Name == "User B" && e.Origin == ProfileOrigin.User), Is.True);
  }

  [Test, Category("HappyPath")]
  public void List_SkipsMalformedJsonSilently() {
    SampleTemplate("Good").Save(Path.Combine(this._builtinDir, "good.json"));
    File.WriteAllText(Path.Combine(this._builtinDir, "bad.json"), "{ this isn't json");

    var list = LayoutProfileStore.List();

    Assert.That(list, Has.Count.EqualTo(1));
    Assert.That(list[0].Name, Is.EqualTo("Good"));
  }

  [Test, Category("HappyPath")]
  public void List_HandlesMissingDirectoriesWithoutThrowing() {
    LayoutProfileStore.BuiltinDirectoryOverride = Path.Combine(this._builtinDir, "nope");
    LayoutProfileStore.UserDirectoryOverride = Path.Combine(this._userDir, "missing");

    var list = LayoutProfileStore.List();

    Assert.That(list, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Save_ThenList_ThenLoad_RoundTripsAllFields() {
    var template = SampleTemplate("RoundTrip", metadata: MetadataZone.Back, leftover: "append_at_end");
    var entry = LayoutProfileStore.Save(template, "roundtrip.json");

    Assert.That(entry.Origin, Is.EqualTo(ProfileOrigin.User));
    Assert.That(File.Exists(entry.FilePath), Is.True);

    var list = LayoutProfileStore.List();
    var listed = list.Single(e => e.Name == "RoundTrip");

    var loaded = LayoutProfileStore.Load(listed);
    Assert.That(loaded.Name, Is.EqualTo("RoundTrip"));
    Assert.That(loaded.MetadataZone, Is.EqualTo(MetadataZone.Back));
    Assert.That(loaded.LeftoverStrategyText, Is.EqualTo("append_at_end"));
    Assert.That(loaded.Zones, Has.Count.EqualTo(1));
    Assert.That(loaded.Zones[0].Name, Is.EqualTo("everything"));
    Assert.That(loaded.Zones[0].Range, Is.EqualTo("0%-100%"));
  }

  [Test, Category("HappyPath")]
  public void Save_NormalisesMissingJsonExtension() {
    var entry = LayoutProfileStore.Save(SampleTemplate("Ext"), "no-extension");
    Assert.That(entry.FilePath, Does.EndWith(".json"));
  }

  [Test, Category("ErrorHandling")]
  public void Save_RejectsInvalidFilename() {
    Assert.Throws<ArgumentException>(() => LayoutProfileStore.Save(SampleTemplate("X"), "bad\\path.json"));
  }

  [Test, Category("ErrorHandling")]
  public void Save_RejectsBlankFilename() {
    Assert.Throws<ArgumentException>(() => LayoutProfileStore.Save(SampleTemplate("X"), ""));
  }

  // ── Built-in / user distinction ───────────────────────────────────────

  [Test, Category("Boundary")]
  public void BuiltinFromList_IsMarkedReadOnly() {
    SampleTemplate("Read-only one").Save(Path.Combine(this._builtinDir, "ro.json"));

    var entry = LayoutProfileStore.List().Single();

    Assert.That(entry.Origin, Is.EqualTo(ProfileOrigin.Builtin));
  }

  [Test, Category("ErrorHandling")]
  public void Delete_RefusesBuiltin() {
    SampleTemplate("Locked").Save(Path.Combine(this._builtinDir, "locked.json"));
    var builtin = LayoutProfileStore.List().Single();

    Assert.Throws<InvalidOperationException>(() => LayoutProfileStore.Delete(builtin));
    Assert.That(File.Exists(builtin.FilePath), Is.True, "File must still exist after refused delete.");
  }

  [Test, Category("HappyPath")]
  public void Delete_RemovesUserProfile() {
    var entry = LayoutProfileStore.Save(SampleTemplate("Doomed"), "doomed.json");
    Assert.That(File.Exists(entry.FilePath), Is.True);

    LayoutProfileStore.Delete(entry);

    Assert.That(File.Exists(entry.FilePath), Is.False);
    Assert.That(LayoutProfileStore.List(), Is.Empty);
  }

  [Test, Category("Boundary")]
  public void Delete_MissingFileIsNoOp() {
    var entry = new LayoutProfileEntry("Phantom",
      Path.Combine(this._userDir, "phantom.json"), ProfileOrigin.User);
    Assert.DoesNotThrow(() => LayoutProfileStore.Delete(entry));
  }

  // ── Filename suggestion ───────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void SuggestFileName_SanitisesTemplateName() {
    var name = LayoutProfileStore.SuggestFileName("Some Profile / With \\ Bad chars");
    Assert.That(name, Does.EndWith(".json"));
    foreach (var c in name)
      Assert.That(Path.GetInvalidFileNameChars(), Has.No.Member(c));
  }

  [Test, Category("Boundary")]
  public void SuggestFileName_AvoidsExistingFile() {
    LayoutProfileStore.Save(SampleTemplate("Collide"), "collide.json");
    var name = LayoutProfileStore.SuggestFileName("Collide");
    Assert.That(name, Is.Not.EqualTo("collide.json"));
    Assert.That(name, Does.EndWith(".json"));
  }

  // ── Helpers ───────────────────────────────────────────────────────────

  private static LayoutTemplate SampleTemplate(
      string name,
      MetadataZone metadata = MetadataZone.Unchanged,
      string leftover = "fill_gaps")
    => new() {
      Name = name,
      MetadataZone = metadata,
      LeftoverStrategyText = leftover,
      Zones = [
        new LayoutZone {
          Name = "everything",
          Range = "0%-100%",
          SortBy = [ new DefragSortKey(DefragSortField.Name, SortDirection.Ascending) ],
        },
      ],
    };

  private static void TryDeleteTree(string path) {
    try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
  }
}
