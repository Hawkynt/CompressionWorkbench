using System;
using System.Collections.Generic;
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
/// The shell's view-model moving between host folders and archive folders the way a file manager
/// does: every arrival is announced once, Back and Forward retrace it, and the breadcrumb trail is a
/// chain of places that each really exist.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ShellNavigationTests {
  private string _root = null!;
  private string _zip = null!;
  private MainViewModel _model = null!;
  private List<Location> _arrivals = null!;

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "cwb-shellnav-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this._root, "a"));
    Directory.CreateDirectory(Path.Combine(this._root, "b"));

    // Opening an archive records its folder as the last one used; keep that out of the real profile.
    UserSettings.PathOverride = Path.Combine(this._root, "settings.json");
    FormatRegistration.EnsureInitialized();

    this._zip = Path.Combine(this._root, "a", "bundle.zip");
    using (var zip = ZipFile.Open(this._zip, ZipArchiveMode.Create)) {
      zip.CreateEntry("docs/guide/intro.txt");
      zip.CreateEntry("docs/readme.txt");
      zip.CreateEntry("src/main.c");
      zip.CreateEntry("top.txt");
    }

    this._model = new MainViewModel();
    this._arrivals = [];
    this._model.LocationChanged += (_, where) => this._arrivals.Add(where);
  }

  [TearDown]
  public void TearDown() {
    UserSettings.PathOverride = null;
    try { Directory.Delete(this._root, recursive: true); } catch { }
  }

  private string A => Path.Combine(this._root, "a");
  private string B => Path.Combine(this._root, "b");

  [Test]
  public void GivenTwoFolders_WhenVisitedInTurn_ThenEachArrivalIsAnnouncedOnce() {
    this._model.NavigateTo(Location.Folder(this.A));
    this._model.NavigateTo(Location.Folder(this.B));
    this._model.NavigateTo(Location.Folder(this.B));

    Assert.That(this._arrivals, Is.EqualTo(new[] { Location.Folder(this.A), Location.Folder(this.B) }),
      "arriving where the shell already is is not an arrival");
  }

  [Test]
  public void GivenAFolderThenAnArchiveFolder_WhenGoingBackAndForward_ThenEachStepRetracesTheWay() {
    this._model.NavigateTo(Location.Folder(this.B));
    this._model.NavigateTo(Location.InArchive(this._zip, "docs"));

    this._model.BackCommand.Execute(null);
    Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.Folder(this.B)));
    Assert.That(this._model.ForwardCommand.CanExecute(null), Is.True);

    this._model.ForwardCommand.Execute(null);
    Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.InArchive(this._zip, "docs/")),
      "forward reopens the archive and returns to the folder inside it");
  }

  [Test]
  public void GivenAnArchiveFolderTarget_WhenNavigatedTo_ThenTheArchiveOpensAtThatFolder() {
    Assert.That(this._model.NavigateTo(Location.InArchive(this._zip, "docs/")), Is.True);

    var names = this._model.Entries.Where(e => !e.IsParentEntry).Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "guide", "readme.txt" }));
  }

  [Test]
  public void GivenTheArchiveIsAlreadyOpen_WhenMovingBetweenItsFolders_ThenItIsNotReopened() {
    this._model.NavigateTo(Location.InArchive(this._zip, ""));
    var opened = 0;
    this._model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.ArchivePath)) ++opened; };

    this._model.NavigateTo(Location.InArchive(this._zip, "src/"));

    Assert.That(opened, Is.Zero, "a folder change inside an open archive must not reread the whole listing");
  }

  // ── breadcrumbs ─────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAFolderInsideAnArchive_WhenBreadcrumbsAreBuilt_ThenTheHostPathComesFirst() {
    this._model.NavigateTo(Location.InArchive(this._zip, "docs/guide/"));

    var crumbs = this._model.Breadcrumbs.ToList();
    var labels = crumbs.Select(c => c.Label).ToList();

    Assert.Multiple(() => {
      Assert.That(labels.TakeLast(4), Is.EqualTo(new[] { "a", "bundle.zip", "docs", "guide" }),
        "the folder the archive lives in, then the archive, then the way down inside it");
      Assert.That(crumbs[^4].Target, Is.EqualTo(Location.Folder(this.A)));
      Assert.That(crumbs[^3].Target, Is.EqualTo(Location.InArchive(this._zip, "")));
      Assert.That(crumbs[^1].Target, Is.EqualTo(Location.InArchive(this._zip, "docs/guide/")));
    });
  }

  [Test]
  public void GivenABrowsedHostFolder_WhenAnArchiveInItIsOpened_ThenTheArchiveJoinsTheTrail() {
    this._model.NavigateTo(Location.Folder(this.A));

    this._model.Open(this._zip);

    Assert.That(this._model.Breadcrumbs.Select(c => c.Label).TakeLast(2), Is.EqualTo(new[] { "a", "bundle.zip" }));
  }

  [Test]
  public void GivenAHostFolder_WhenBreadcrumbsAreBuilt_ThenEveryCrumbIsAnAbsolutePathThatExists() {
    this._model.NavigateTo(Location.Folder(this.B));

    Assert.Multiple(() => {
      foreach (var crumb in this._model.Breadcrumbs) {
        Assert.That(Path.IsPathRooted(crumb.FolderPath), Is.True, $"\"{crumb.FolderPath}\" is relative");
        Assert.That(Directory.Exists(crumb.FolderPath), Is.True, $"\"{crumb.FolderPath}\" leads nowhere");
      }
    });
  }

  [Test]
  public void GivenACrumbForAnEarlierFolder_WhenClicked_ThenTheShellGoesThere() {
    this._model.NavigateTo(Location.InArchive(this._zip, "docs/guide/"));
    var hostCrumb = this._model.Breadcrumbs.First(c => c.Target == Location.Folder(this.A));

    this._model.NavigateToBreadcrumbCommand.Execute(hostCrumb.Target);

    Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.Folder(this.A)));
    Assert.That(this._model.IsBrowsingOsFolder, Is.True);
  }

  // ── the tree's view of an archive ───────────────────────────────────────────────────────────

  [Test]
  public void GivenAnArchiveWithNoFolderEntries_WhenAskedForSubfolders_ThenTheImpliedOnesAppear() {
    this._model.NavigateTo(Location.InArchive(this._zip, ""));

    Assert.Multiple(() => {
      Assert.That(this._model.ArchiveSubfolders(""), Is.EqualTo(new[] { "docs", "src" }),
        "the zip records only files; its folders exist because of what lies beneath them");
      Assert.That(this._model.ArchiveSubfolders("docs"), Is.EqualTo(new[] { "guide" }));
      Assert.That(this._model.ArchiveSubfolders("docs/guide/"), Is.Empty);
    });
  }

  // ── what cannot be reached ──────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAFolderThatWasDeleted_WhenGoingBackToIt_ThenTheShellStaysAndSaysSo() {
    var doomed = Path.Combine(this._root, "doomed");
    Directory.CreateDirectory(doomed);
    this._model.NavigateTo(Location.Folder(doomed));
    this._model.NavigateTo(Location.Folder(this.B));
    Directory.Delete(doomed);

    this._model.BackCommand.Execute(null);

    Assert.Multiple(() => {
      Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.Folder(this.B)));
      Assert.That(this._model.StatusText, Does.Contain("No longer exists"));
    });
  }

  [Test]
  public void GivenAnAddressThatLeadsNowhere_WhenTyped_ThenNothingMovesAndTheStatusSaysSo() {
    this._model.NavigateTo(Location.Folder(this.B));

    Assert.That(this._model.NavigateToAddress(Path.Combine(this._root, "nowhere")), Is.False);
    Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.Folder(this.B)));
    Assert.That(this._model.StatusText, Does.StartWith("Not found"));
  }

  [Test]
  public void GivenAnAddressRunningIntoAnArchive_WhenTyped_ThenTheShellLandsInsideIt() {
    Assert.That(this._model.NavigateToAddress(this._zip + Path.DirectorySeparatorChar + "src"), Is.True);
    Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.InArchive(this._zip, "src/")));
  }

  // ── rereading the archive after it changed ──────────────────────────────────────────────────

  [Test]
  public void GivenAFolderInsideAnArchive_WhenTheArchiveIsReread_ThenTheShellStaysThereWithoutANewHistoryStop() {
    this._model.NavigateTo(Location.Folder(this.B));
    this._model.NavigateTo(Location.InArchive(this._zip, "docs/"));
    var arrivals = this._arrivals.Count;

    this._model.ReloadArchiveInPlace();

    Assert.Multiple(() => {
      Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.InArchive(this._zip, "docs/")));
      Assert.That(this._arrivals, Has.Count.EqualTo(arrivals), "a reread is not a visit");
    });
    this._model.BackCommand.Execute(null);
    Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.Folder(this.B)));
  }

  [Test]
  public void GivenTheFolderWasEmptiedFromOutside_WhenTheArchiveIsReread_ThenTheShellFallsBackToTheRoot() {
    this._model.NavigateTo(Location.InArchive(this._zip, "src/"));
    ArchiveOperations.Remove(this._zip, ["src/main.c"]);

    this._model.ReloadArchiveInPlace();

    Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.InArchive(this._zip, "")),
      "a folder with nothing left in it no longer exists in an archive that records only files");
  }

  [Test]
  public void GivenANestedArchive_WhenItIsReread_ThenGoingUpStillLeadsBackToTheArchiveItCameFrom() {
    var outer = Path.Combine(this._root, "outer.zip");
    using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
      zip.CreateEntryFromFile(this._zip, "inner.zip");
    this._model.NavigateTo(Location.InArchive(outer, ""));
    // Activating an archive entry is what descends into it, as a double-click or Enter does.
    this._model.SelectedEntries.Add(this._model.Entries.Single(e => e.Name == "inner.zip"));
    this._model.ViewSelectedAs(hex: false);
    Assert.That(this._model.IsNestedArchive, Is.True, "precondition: the shell descended into the inner archive");

    this._model.ReloadArchiveInPlace();
    Assert.That(this._model.IsNestedArchive, Is.True, "a plain reopen cleared the chain of parent archives");

    this._model.NavigateUpCommand.Execute(null);
    Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.InArchive(outer, "")));
  }
}
