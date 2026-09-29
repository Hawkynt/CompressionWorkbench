using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Compression.Lib;
using Compression.NativeUI;
using Compression.NativeUI.Navigation;
using Compression.NativeUI.ViewModels;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>
/// Renaming from the shell: files and folders on disk, entries inside an archive, and every name
/// the shell must refuse without touching anything.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ShellRenameTests {
  private string _root = null!;
  private string _zip = null!;
  private MainViewModel _model = null!;

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "cwb-rename-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this._root, "folder"));
    File.WriteAllText(Path.Combine(this._root, "folder", "inside.txt"), "inside");
    File.WriteAllText(Path.Combine(this._root, "file.txt"), "content");
    File.WriteAllText(Path.Combine(this._root, "other.txt"), "other");

    UserSettings.PathOverride = Path.Combine(this._root, "settings.json");
    FormatRegistration.EnsureInitialized();

    this._zip = Path.Combine(this._root, "bundle.zip");
    using (var zip = ZipFile.Open(this._zip, ZipArchiveMode.Create)) {
      Write(zip, "docs/guide/intro.txt", "intro");
      Write(zip, "docs/readme.txt", "readme");
      Write(zip, "docs/notes.txt", "notes");
      Write(zip, "top.txt", "top");
    }

    this._model = new MainViewModel();

    static void Write(ZipArchive zip, string name, string text) {
      using var writer = new StreamWriter(zip.CreateEntry(name).Open());
      writer.Write(text);
    }
  }

  [TearDown]
  public void TearDown() {
    UserSettings.PathOverride = null;
    try { Directory.Delete(this._root, recursive: true); } catch { }
  }

  private ArchiveEntryViewModel Entry(string name) => this._model.Entries.Single(e => e.Name == name);

  private string[] Names() => [.. this._model.Entries.Where(e => !e.IsParentEntry).Select(e => e.Name).Order(StringComparer.Ordinal)];

  private string[] ZipEntries() {
    using var zip = ZipFile.OpenRead(this._zip);
    return [.. zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName).Order(StringComparer.Ordinal)];
  }

  private string ZipText(string name) {
    using var zip = ZipFile.OpenRead(this._zip);
    using var reader = new StreamReader(zip.GetEntry(name)!.Open());
    return reader.ReadToEnd();
  }

  // ── on disk ─────────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAFileOnDisk_WhenRenamed_ThenItMovesAndTheListShowsTheNewName() {
    this._model.NavigateTo(Location.Folder(this._root));

    Assert.That(this._model.Rename(this.Entry("file.txt"), "renamed.txt"), Is.Null);

    Assert.Multiple(() => {
      Assert.That(File.ReadAllText(Path.Combine(this._root, "renamed.txt")), Is.EqualTo("content"));
      Assert.That(File.Exists(Path.Combine(this._root, "file.txt")), Is.False);
      Assert.That(this.Names(), Does.Contain("renamed.txt").And.Not.Contain("file.txt"));
    });
  }

  [Test]
  public void GivenAFolderOnDisk_WhenRenamed_ThenItsContentsGoWithIt() {
    this._model.NavigateTo(Location.Folder(this._root));

    Assert.That(this._model.Rename(this.Entry("folder"), "moved"), Is.Null);

    Assert.That(File.ReadAllText(Path.Combine(this._root, "moved", "inside.txt")), Is.EqualTo("inside"));
  }

  [Test]
  public void GivenAFileOnDisk_WhenOnlyItsCaseChanges_ThenTheNewCaseIsKept() {
    this._model.NavigateTo(Location.Folder(this._root));

    Assert.That(this._model.Rename(this.Entry("file.txt"), "FILE.txt"), Is.Null);

    Assert.That(Directory.GetFiles(this._root).Select(Path.GetFileName), Does.Contain("FILE.txt").And.Not.Contain("file.txt"));
  }

  [Test]
  public void GivenANameTakenBySibling_WhenRenamedOnto_ThenNothingIsOverwrittenAndTheReasonIsShown() {
    this._model.NavigateTo(Location.Folder(this._root));

    var error = this._model.Rename(this.Entry("file.txt"), "OTHER.txt");

    Assert.Multiple(() => {
      Assert.That(error, Does.Contain("already exists"));
      Assert.That(this._model.StatusText, Is.EqualTo(error));
      Assert.That(File.ReadAllText(Path.Combine(this._root, "other.txt")), Is.EqualTo("other"));
      Assert.That(File.ReadAllText(Path.Combine(this._root, "file.txt")), Is.EqualTo("content"));
    });
  }

  [TestCase("")]
  [TestCase("..")]
  [TestCase("sub/dir.txt")]
  public void GivenAnInvalidName_WhenRenamed_ThenItIsRefusedAndTheFileStays(string typed) {
    this._model.NavigateTo(Location.Folder(this._root));

    Assert.That(this._model.Rename(this.Entry("file.txt"), typed), Is.Not.Null);
    Assert.That(File.Exists(Path.Combine(this._root, "file.txt")), Is.True);
  }

  [Test]
  public void GivenTheSameName_WhenRenamed_ThenItSucceedsWithoutTouchingTheFile() {
    this._model.NavigateTo(Location.Folder(this._root));
    var before = File.GetLastWriteTimeUtc(Path.Combine(this._root, "file.txt"));

    Assert.That(this._model.Rename(this.Entry("file.txt"), " file.txt "), Is.Null);
    Assert.That(File.GetLastWriteTimeUtc(Path.Combine(this._root, "file.txt")), Is.EqualTo(before));
  }

  // ── inside an archive ───────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAFileInAnArchiveFolder_WhenRenamed_ThenTheArchiveHoldsItUnderTheNewNameAndTheShellStaysThere() {
    this._model.NavigateTo(Location.InArchive(this._zip, "docs/"));

    Assert.That(this._model.Rename(this.Entry("readme.txt"), "README.md"), Is.Null);

    Assert.Multiple(() => {
      Assert.That(this.ZipEntries(), Is.EqualTo(new[] { "docs/README.md", "docs/guide/intro.txt", "docs/notes.txt", "top.txt" }));
      Assert.That(this.ZipText("docs/README.md"), Is.EqualTo("readme"));
      Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.InArchive(this._zip, "docs/")),
        "reopening the archive must not drop the user back at its root");
      Assert.That(this.Names(), Is.EqualTo(new[] { "README.md", "guide", "notes.txt" }));
    });
  }

  [Test]
  public void GivenAFolderInAnArchive_WhenRenamed_ThenEverythingBeneathItFollows() {
    this._model.NavigateTo(Location.InArchive(this._zip, "docs/"));

    Assert.That(this._model.Rename(this.Entry("guide"), "manual"), Is.Null);

    Assert.That(this.ZipEntries(), Does.Contain("docs/manual/intro.txt").And.Not.Contain("docs/guide/intro.txt"));
  }

  [Test]
  public void GivenAnArchiveRename_WhenGoingBackAfterwards_ThenHistoryHasNoExtraStop() {
    this._model.NavigateTo(Location.Folder(this._root));
    this._model.NavigateTo(Location.InArchive(this._zip, "docs/"));

    this._model.Rename(this.Entry("notes.txt"), "notes.md");
    this._model.BackCommand.Execute(null);

    Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.Folder(this._root)),
      "the reload after the edit is not a visit");
  }

  [Test]
  public void GivenANameTakenInsideTheArchive_WhenRenamedOnto_ThenTheArchiveIsUntouched() {
    this._model.NavigateTo(Location.InArchive(this._zip, "docs/"));
    var before = File.ReadAllBytes(this._zip);

    Assert.That(this._model.Rename(this.Entry("readme.txt"), "notes.txt"), Does.Contain("already exists"));
    Assert.That(File.ReadAllBytes(this._zip), Is.EqualTo(before));
  }

  // ── when it is on offer ─────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenTheParentRow_WhenAskedIfItCanBeRenamed_ThenItCannot() {
    this._model.NavigateTo(Location.InArchive(this._zip, "docs/"));

    Assert.That(this._model.CanRename(this._model.Entries.Single(e => e.IsParentEntry)), Is.False);
  }

  [TestCase(0, false)]
  [TestCase(1, true)]
  [TestCase(2, false)]
  public void GivenASelection_WhenRenameIsOffered_ThenOnlyASingleEntryQualifies(int selected, bool expected) {
    this._model.NavigateTo(Location.Folder(this._root));
    foreach (var entry in this._model.Entries.Where(e => !e.IsParentEntry).Take(selected))
      this._model.SelectedEntries.Add(entry);

    Assert.That(this._model.RenameCommand.CanExecute(null), Is.EqualTo(expected));
  }

  [Test]
  public void GivenOneSelectedEntry_WhenRenameIsInvoked_ThenTheViewIsAskedToEditThatEntry() {
    this._model.NavigateTo(Location.Folder(this._root));
    var target = this.Entry("file.txt");
    this._model.SelectedEntries.Add(target);
    ArchiveEntryViewModel? requested = null;
    this._model.RenameRequested += (_, entry) => requested = entry;

    this._model.RenameCommand.Execute(null);

    Assert.That(requested, Is.SameAs(target));
  }

  // ── new folder ──────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAFolderOnDisk_WhenNewFoldersAreMade_ThenEachTakesTheNextFreeNameAndIsHandedOverForNaming() {
    this._model.NavigateTo(Location.Folder(this._root));
    var named = new System.Collections.Generic.List<string>();
    this._model.RenameRequested += (_, entry) => named.Add(entry.Name);

    Assert.That(this._model.CreateNewFolder(), Is.EqualTo("New folder"));
    Assert.That(this._model.CreateNewFolder(), Is.EqualTo("New folder (2)"));

    Assert.Multiple(() => {
      Assert.That(Directory.Exists(Path.Combine(this._root, "New folder (2)")), Is.True);
      Assert.That(named, Is.EqualTo(new[] { "New folder", "New folder (2)" }), "the user names it straight away");
      Assert.That(this.Names(), Does.Contain("New folder (2)"));
    });
  }

  [Test]
  public void GivenAnArchiveFolder_WhenAskedForANewFolder_ThenItIsNotOffered() {
    this._model.NavigateTo(Location.InArchive(this._zip, "docs/"));

    Assert.That(this._model.NewFolderCommand.CanExecute(null), Is.False,
      "an empty folder would not survive a writer that keeps only files");
    Assert.That(this._model.CreateNewFolder(), Is.Null);
  }
}
