using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Compression.NativeUI.Navigation;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>
/// The shell's navigation rules, independent of any window: where a typed address leads, how a path
/// breaks into breadcrumb segments on each platform, and what Back and Forward do.
/// </summary>
[TestFixture]
public sealed class NavigationTests {
  // ── HostPathSegments: every segment must be an absolute path you can navigate to ────────────

  [Test]
  public void GivenAPosixPath_WhenSegmented_ThenTheRootIsItsOwnSegmentAndEveryPathIsAbsolute() {
    var segments = HostPathSegments.Split("/home/me/downloads", windows: false);

    Assert.That(segments.Select(s => s.Label), Is.EqualTo(new[] { "/", "home", "me", "downloads" }));
    Assert.That(segments.Select(s => s.Path), Is.EqualTo(new[] { "/", "/home", "/home/me", "/home/me/downloads" }),
      "a relative crumb such as \"home/\" navigates nowhere; that was the Linux bug");
  }

  [Test]
  public void GivenThePosixRoot_WhenSegmented_ThenThereIsExactlyTheRoot()
    => Assert.That(HostPathSegments.Split("/", windows: false).Select(s => s.Path), Is.EqualTo(new[] { "/" }));

  [Test]
  public void GivenAWindowsDrivePath_WhenSegmented_ThenTheDriveIsARootWithItsSeparator() {
    var segments = HostPathSegments.Split(@"C:\Users\me", windows: true);

    Assert.That(segments.Select(s => s.Label), Is.EqualTo(new[] { "C:", "Users", "me" }));
    Assert.That(segments.Select(s => s.Path), Is.EqualTo(new[] { @"C:\", @"C:\Users", @"C:\Users\me" }),
      @"""C:"" alone means the drive's current directory, not its root");
  }

  [Test]
  public void GivenAWindowsDriveRoot_WhenSegmented_ThenThereIsExactlyTheDrive()
    => Assert.That(HostPathSegments.Split(@"D:\", windows: true).Select(s => s.Path), Is.EqualTo(new[] { @"D:\" }));

  [Test]
  public void GivenAUncPath_WhenSegmented_ThenServerAndShareFormOneRoot() {
    var segments = HostPathSegments.Split(@"\\server\share\dir", windows: true);

    Assert.That(segments.Select(s => s.Path), Is.EqualTo(new[] { @"\\server\share", @"\\server\share\dir" }),
      "a UNC path has no navigable level above the share");
  }

  [Test]
  public void GivenATrailingSeparator_WhenSegmented_ThenNoEmptySegmentAppears()
    => Assert.That(HostPathSegments.Split("/home/me/", windows: false).Select(s => s.Label),
      Is.EqualTo(new[] { "/", "home", "me" }));

  [Test]
  public void GivenAnEmptyPath_WhenSegmented_ThenThereAreNoSegments()
    => Assert.That(HostPathSegments.Split("", windows: false), Is.Empty);

  // ── NavigationHistory ───────────────────────────────────────────────────────────────────────

  private static readonly Location A = Location.Folder("/a");
  private static readonly Location B = Location.Folder("/b");
  private static readonly Location C = Location.Folder("/c");

  [Test]
  public void GivenAFreshHistory_WhenAsked_ThenThereIsNowhereToGo() {
    var history = new NavigationHistory();

    Assert.Multiple(() => {
      Assert.That(history.CanGoBack, Is.False);
      Assert.That(history.CanGoForward, Is.False);
      Assert.That(history.Back(), Is.Null);
      Assert.That(history.Forward(), Is.Null);
    });
  }

  [Test]
  public void GivenThreeVisits_WhenGoingBackTwiceAndForwardOnce_ThenEachStepLandsWhereExpected() {
    var history = new NavigationHistory();
    history.Visit(A);
    history.Visit(B);
    history.Visit(C);

    Assert.Multiple(() => {
      Assert.That(history.Back(), Is.EqualTo(B));
      Assert.That(history.Back(), Is.EqualTo(A));
      Assert.That(history.CanGoBack, Is.False, "the first visit has nothing behind it");
      Assert.That(history.Forward(), Is.EqualTo(B));
      Assert.That(history.CanGoForward, Is.True);
    });
  }

  [Test]
  public void GivenAStepBack_WhenANewPlaceIsVisited_ThenTheForwardTrailIsDropped() {
    var history = new NavigationHistory();
    history.Visit(A);
    history.Visit(B);
    history.Back();
    history.Visit(C);

    Assert.That(history.CanGoForward, Is.False, "browsers and file managers both discard the old branch");
    Assert.That(history.Back(), Is.EqualTo(A));
  }

  [Test]
  public void GivenTheSamePlaceTwiceInARow_WhenVisited_ThenItIsRecordedOnce() {
    var history = new NavigationHistory();
    history.Visit(A);
    history.Visit(A);

    Assert.That(history.CanGoBack, Is.False, "a refresh is not a step");
  }

  /// <summary>Going Back arrives somewhere; that arrival must not be recorded as a new visit.</summary>
  [Test]
  public void GivenABackStep_WhenTheShellReportsArrivingThere_ThenForwardSurvives() {
    var history = new NavigationHistory();
    history.Visit(A);
    history.Visit(B);
    var target = history.Back();
    history.Visit(target!);

    Assert.That(history.CanGoForward, Is.True);
  }

  [Test]
  public void GivenAVeryLongSession_WhenVisitedPastTheCap_ThenTheOldestStepsAreForgotten() {
    var history = new NavigationHistory(capacity: 3);
    foreach (var name in new[] { "/1", "/2", "/3", "/4", "/5" }) history.Visit(Location.Folder(name));

    Assert.Multiple(() => {
      Assert.That(history.Back(), Is.EqualTo(Location.Folder("/4")));
      Assert.That(history.Back(), Is.EqualTo(Location.Folder("/3")));
      Assert.That(history.CanGoBack, Is.False, "only the cap's worth of places are kept");
    });
  }

  // ── Location equality: archive folders compare by folder regardless of the trailing slash ────

  [Test]
  public void GivenTheSameArchiveFolderWithAndWithoutATrailingSlash_WhenCompared_ThenTheyAreEqual()
    => Assert.That(Location.InArchive("/x/a.zip", "docs"), Is.EqualTo(Location.InArchive("/x/a.zip", "docs/")));

  [Test]
  public void GivenAFolderAndAnArchiveRootAtTheSamePath_WhenCompared_ThenTheyDiffer()
    => Assert.That(Location.Folder("/x/a.zip"), Is.Not.EqualTo(Location.InArchive("/x/a.zip", "")));

  // ── AddressResolver: what a typed address means ─────────────────────────────────────────────

  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb-nav-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this._dir, "sub"));
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, recursive: true); } catch { }
  }

  private string MakeZip(string name) {
    var path = Path.Combine(this._dir, name);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    zip.CreateEntry("docs/readme.txt");
    return path;
  }

  [Test]
  public void GivenAnExistingFolder_WhenTyped_ThenItIsThatFolder()
    => Assert.That(AddressResolver.Resolve(Path.Combine(this._dir, "sub")),
      Is.EqualTo(Location.Folder(Path.Combine(this._dir, "sub"))));

  [Test]
  public void GivenAnArchiveFile_WhenTyped_ThenItIsTheArchiveRoot() {
    var zip = this.MakeZip("a.zip");
    Assert.That(AddressResolver.Resolve(zip), Is.EqualTo(Location.InArchive(zip, "")));
  }

  [Test]
  public void GivenAPathThatRunsIntoAnArchive_WhenTyped_ThenItIsTheFolderInsideIt() {
    var zip = this.MakeZip("a.zip");
    var typed = zip + Path.DirectorySeparatorChar + "docs";

    Assert.That(AddressResolver.Resolve(typed), Is.EqualTo(Location.InArchive(zip, "docs/")));
  }

  [Test]
  public void GivenForwardSlashesInsideTheArchivePart_WhenTyped_ThenTheyAreAcceptedToo() {
    var zip = this.MakeZip("a.zip");
    Assert.That(AddressResolver.Resolve(zip + "/docs/"), Is.EqualTo(Location.InArchive(zip, "docs/")));
  }

  [Test]
  public void GivenAPathThatExistsNowhere_WhenTyped_ThenNothingResolves()
    => Assert.That(AddressResolver.Resolve(Path.Combine(this._dir, "no", "such", "place")), Is.Null);

  [Test]
  public void GivenAPlainFileThatIsNotAnArchive_WhenTyped_ThenNothingResolves() {
    var text = Path.Combine(this._dir, "plain.txt");
    File.WriteAllText(text, "not an archive");

    Assert.That(AddressResolver.Resolve(text), Is.Null, "a text file has no inside to navigate to");
  }

  [TestCase("")]
  [TestCase("   ")]
  public void GivenABlankAddress_WhenTyped_ThenNothingResolves(string typed)
    => Assert.That(AddressResolver.Resolve(typed), Is.Null);

  [Test]
  public void GivenSurroundingQuotesAndWhitespace_WhenTyped_ThenTheyAreIgnored() {
    // Pasting from a shell or from Explorer's "copy as path" wraps the path in quotes.
    var sub = Path.Combine(this._dir, "sub");
    Assert.That(AddressResolver.Resolve($"  \"{sub}\"  "), Is.EqualTo(Location.Folder(sub)));
  }

  // ── FolderSource: what the tree, the crumb drop-downs and completion all see ────────────────

  [Test]
  public void GivenAHostFolder_WhenListed_ThenOnlyItsVisibleSubfoldersAppearInNameOrder() {
    var parent = Path.Combine(Path.GetTempPath(), "cwb-src-" + Guid.NewGuid().ToString("N"));
    foreach (var name in new[] { "zeta", "Alpha", ".hidden", "beta" })
      Directory.CreateDirectory(Path.Combine(parent, name));
    File.WriteAllText(Path.Combine(parent, "file.txt"), "not a folder");

    try {
      var source = new FolderSource(_ => [], () => null);

      Assert.That(source.Children(Location.Folder(parent)).Select(n => n.Label),
        Is.EqualTo(new[] { "Alpha", "beta", "zeta" }), "files and dot-folders are not tree nodes");
    } finally {
      Directory.Delete(parent, recursive: true);
    }
  }

  [Test]
  public void GivenTheOpenArchiveLivesInAFolder_WhenThatFolderIsListed_ThenTheArchiveAppearsAsAFolder() {
    var parent = Path.Combine(Path.GetTempPath(), "cwb-src-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(parent);
    var archive = Path.Combine(parent, "a.zip");
    File.WriteAllBytes(archive, []);

    try {
      var source = new FolderSource(_ => [], () => archive);

      Assert.That(source.Children(Location.Folder(parent)),
        Is.EqualTo(new[] { new FolderNode("a.zip", Location.InArchive(archive, "")) }));
      Assert.That(source.Children(Location.Folder(Path.GetTempPath())).Any(n => n.Location.IsInArchive), Is.False,
        "only the folder it lives in shows it");
    } finally {
      Directory.Delete(parent, recursive: true);
    }
  }

  [Test]
  public void GivenAFolderInsideAnArchive_WhenListed_ThenItsSubfoldersLeadFurtherIn() {
    var source = new FolderSource(folder => folder == "docs/" ? ["guide", "api"] : [], () => null);
    var zip = Path.Combine(Path.GetTempPath(), "x.zip");

    var children = source.Children(Location.InArchive(zip, "docs/"));

    Assert.That(children.Select(c => c.Location), Is.EqualTo(new[] {
      Location.InArchive(zip, "docs/guide/"), Location.InArchive(zip, "docs/api/"),
    }));
  }

  [Test]
  public void GivenAFolderThatCannotBeRead_WhenListed_ThenItSimplyHasNoChildren()
    => Assert.That(new FolderSource(_ => [], () => null)
      .Children(Location.Folder(Path.Combine(Path.GetTempPath(), "cwb-missing-" + Guid.NewGuid().ToString("N")))), Is.Empty);

  [Test]
  public void GivenTheRoots_WhenListed_ThenEachIsAnExistingAbsoluteFolder() {
    var roots = new FolderSource(_ => [], () => null).Roots();

    Assert.That(roots, Is.Not.Empty);
    Assert.That(roots.All(r => Path.IsPathRooted(r.Location.HostPath) && Directory.Exists(r.Location.HostPath)), Is.True);
  }

  [TestCase("")]
  [TestCase("   ")]
  [TestCase("no-separator-at-all")]
  public void GivenTextWithNoFolderToLookIn_WhenCompleted_ThenThereAreNoSuggestions(string typed)
    => Assert.That(new FolderSource(_ => [], () => null).Complete(typed), Is.Empty);

  [Test]
  public void GivenAPartlyTypedName_WhenCompleted_ThenMatchingSubfoldersAreSuggestedCaseInsensitively() {
    var parent = Path.Combine(Path.GetTempPath(), "cwb-src-" + Guid.NewGuid().ToString("N"));
    foreach (var name in new[] { "Music", "movies", "docs" })
      Directory.CreateDirectory(Path.Combine(parent, name));

    try {
      var source = new FolderSource(_ => [], () => null);

      Assert.Multiple(() => {
        Assert.That(source.Complete(Path.Combine(parent, "m")),
          Is.EqualTo(new[] { Path.Combine(parent, "movies"), Path.Combine(parent, "Music") }));
        Assert.That(source.Complete(parent + Path.DirectorySeparatorChar), Has.Count.EqualTo(3),
          "a trailing separator lists everything in the folder");
      });
    } finally {
      Directory.Delete(parent, recursive: true);
    }
  }

  [Test]
  public void GivenAFolderWithManySubfolders_WhenCompleted_ThenTheSuggestionsAreCapped() {
    var parent = Path.Combine(Path.GetTempPath(), "cwb-src-" + Guid.NewGuid().ToString("N"));
    for (var i = 0; i < 31; ++i) Directory.CreateDirectory(Path.Combine(parent, $"d{i:00}"));

    try {
      Assert.That(new FolderSource(_ => [], () => null).Complete(Path.Combine(parent, "d")), Has.Count.EqualTo(30));
    } finally {
      Directory.Delete(parent, recursive: true);
    }
  }
}
