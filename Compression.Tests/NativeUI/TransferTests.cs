using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Compression.Lib;
using Compression.NativeUI.Editing;
using Compression.NativeUI.Navigation;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>
/// Copying and moving between host folders and archive folders, in every direction, without ever
/// overwriting anything and without losing anything when a move is refused.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class TransferTests {
  private string _root = null!;
  private string _disk = null!;
  private string _a = null!;
  private string _b = null!;

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "cwb-xfer-" + Guid.NewGuid().ToString("N"));
    this._disk = Path.Combine(this._root, "disk");
    Directory.CreateDirectory(Path.Combine(this._disk, "folder", "deep"));
    File.WriteAllText(Path.Combine(this._disk, "file.txt"), "disk file");
    File.WriteAllText(Path.Combine(this._disk, "folder", "one.txt"), "one");
    File.WriteAllText(Path.Combine(this._disk, "folder", "deep", "two.txt"), "two");
    Directory.CreateDirectory(Path.Combine(this._disk, "target"));

    FormatRegistration.EnsureInitialized();
    this._a = Zip("a.zip", ("docs/readme.txt", "a readme"), ("docs/guide/intro.txt", "intro"), ("readme.txt", "a top"));
    this._b = Zip("b.zip", ("existing.txt", "b"));
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._root, recursive: true); } catch { }
  }

  private string Zip(string name, params (string Entry, string Text)[] entries) {
    var path = Path.Combine(this._root, name);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (entry, text) in entries) {
      using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
      writer.Write(text);
    }

    return path;
  }

  private static Dictionary<string, string> ZipContents(string path) {
    using var zip = ZipFile.OpenRead(path);
    return zip.Entries.Where(e => !e.FullName.EndsWith('/'))
      .ToDictionary(e => e.FullName, e => new StreamReader(e.Open()).ReadToEnd());
  }

  private Location Disk(params string[] parts) => Location.Folder(Path.Combine([this._disk, .. parts]));
  private static TransferItem Item(Location from, string name, bool folder = false) => new(from, name, folder);

  // ── disk to disk ────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAFolderOnDisk_WhenCopiedToAnotherFolder_ThenEverythingBeneathItIsCopiedAndTheOriginalStays() {
    Transfer.Run([Item(this.Disk(), "folder", folder: true)], this.Disk("target"), move: false);

    Assert.That(File.ReadAllText(Path.Combine(this._disk, "target", "folder", "deep", "two.txt")), Is.EqualTo("two"));
    Assert.That(File.Exists(Path.Combine(this._disk, "folder", "deep", "two.txt")), Is.True);
  }

  [Test]
  public void GivenAFileOnDisk_WhenMovedToAnotherFolder_ThenOnlyTheNewCopyRemains() {
    Transfer.Run([Item(this.Disk(), "file.txt")], this.Disk("target"), move: true);

    Assert.That(File.ReadAllText(Path.Combine(this._disk, "target", "file.txt")), Is.EqualTo("disk file"));
    Assert.That(File.Exists(Path.Combine(this._disk, "file.txt")), Is.False);
  }

  [Test]
  public void GivenAFileOnDisk_WhenCopiedIntoItsOwnFolder_ThenANumberedCopyAppears() {
    var result = Transfer.Run([Item(this.Disk(), "file.txt")], this.Disk(), move: false);

    Assert.That(result.Created, Is.EqualTo(new[] { "file (2).txt" }));
    Assert.That(File.ReadAllText(Path.Combine(this._disk, "file (2).txt")), Is.EqualTo("disk file"));
  }

  [Test]
  public void GivenAFileMovedOntoTheFolderItIsIn_WhenRun_ThenNothingChanges() {
    var result = Transfer.Run([Item(this.Disk(), "file.txt")], this.Disk(), move: true);

    Assert.That(result.Created, Is.Empty);
    Assert.That(File.ReadAllText(Path.Combine(this._disk, "file.txt")), Is.EqualTo("disk file"));
  }

  // ── archive to disk ─────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAnArchiveFolder_WhenCopiedToDisk_ThenItsContentsArriveUnderTheFolderName() {
    Transfer.Run([Item(Location.InArchive(this._a, ""), "docs", folder: true)], this.Disk("target"), move: false);

    Assert.That(File.ReadAllText(Path.Combine(this._disk, "target", "docs", "guide", "intro.txt")), Is.EqualTo("intro"));
    Assert.That(ZipContents(this._a), Has.Count.EqualTo(3), "a copy leaves the archive alone");
  }

  [Test]
  public void GivenAnArchiveFile_WhenMovedToDisk_ThenItLeavesTheArchiveAndNothingElseDoes() {
    Transfer.Run([Item(Location.InArchive(this._a, ""), "readme.txt")], this.Disk("target"), move: true);

    Assert.That(File.ReadAllText(Path.Combine(this._disk, "target", "readme.txt")), Is.EqualTo("a top"));
    Assert.That(ZipContents(this._a).Keys, Is.EquivalentTo(new[] { "docs/readme.txt", "docs/guide/intro.txt" }),
      "the file of the same name in docs/ is a different file");
  }

  // ── disk to archive ─────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAFolderOnDisk_WhenCopiedIntoAnArchiveFolder_ThenItsTreeLandsBeneathThatFolder() {
    Transfer.Run([Item(this.Disk(), "folder", folder: true)], Location.InArchive(this._a, "docs/"), move: false);

    var contents = ZipContents(this._a);
    Assert.That(contents["docs/folder/one.txt"], Is.EqualTo("one"));
    Assert.That(contents["docs/folder/deep/two.txt"], Is.EqualTo("two"));
  }

  [Test]
  public void GivenAFileWhoseNameIsTakenInTheArchive_WhenCopiedIn_ThenItGetsANumberAndNothingIsReplaced() {
    File.WriteAllText(Path.Combine(this._disk, "existing.txt"), "from disk");

    var result = Transfer.Run([Item(this.Disk(), "existing.txt")], Location.InArchive(this._b, ""), move: false);

    Assert.That(result.Created, Is.EqualTo(new[] { "existing (2).txt" }));
    Assert.That(ZipContents(this._b), Is.EquivalentTo(new Dictionary<string, string> {
      ["existing.txt"] = "b", ["existing (2).txt"] = "from disk",
    }));
  }

  [Test]
  public void GivenAFileOnDisk_WhenMovedIntoAnArchive_ThenItIsGoneFromDisk() {
    Transfer.Run([Item(this.Disk(), "file.txt")], Location.InArchive(this._b, ""), move: true);

    Assert.That(ZipContents(this._b)["file.txt"], Is.EqualTo("disk file"));
    Assert.That(File.Exists(Path.Combine(this._disk, "file.txt")), Is.False);
  }

  // ── archive to archive ──────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAnArchiveFolder_WhenCopiedIntoAnotherArchive_ThenBothHoldIt() {
    Transfer.Run([Item(Location.InArchive(this._a, ""), "docs", folder: true)], Location.InArchive(this._b, ""), move: false);

    Assert.That(ZipContents(this._b)["docs/guide/intro.txt"], Is.EqualTo("intro"));
    Assert.That(ZipContents(this._a)["docs/guide/intro.txt"], Is.EqualTo("intro"));
  }

  [Test]
  public void GivenAnArchiveFolder_WhenMovedIntoAnotherArchive_ThenItLeavesTheFirst() {
    Transfer.Run([Item(Location.InArchive(this._a, ""), "docs", folder: true)], Location.InArchive(this._b, ""), move: true);

    Assert.That(ZipContents(this._b).Keys, Is.EquivalentTo(new[] { "existing.txt", "docs/readme.txt", "docs/guide/intro.txt" }));
    Assert.That(ZipContents(this._a).Keys, Is.EquivalentTo(new[] { "readme.txt" }));
  }

  [Test]
  public void GivenAnEntry_WhenMovedToAnotherFolderOfTheSameArchive_ThenItIsRenamedThere() {
    Transfer.Run([Item(Location.InArchive(this._a, ""), "readme.txt")], Location.InArchive(this._a, "docs/guide/"), move: true);

    Assert.That(ZipContents(this._a), Is.EquivalentTo(new Dictionary<string, string> {
      ["docs/readme.txt"] = "a readme", ["docs/guide/intro.txt"] = "intro", ["docs/guide/readme.txt"] = "a top",
    }));
  }

  // ── where the bytes are staged ──────────────────────────────────────────────────────────────

  private List<string> WatchStaging(Action transfer) {
    var seen = new List<string>();
    void Record(string path) => seen.Add(path);
    Transfer.StagingCreated += Record;
    try {
      transfer();
    } finally {
      Transfer.StagingCreated -= Record;
    }

    return seen;
  }

  [Test]
  public void GivenArchiveEntriesPastedIntoAFolder_WhenExtracted_ThenTheyAreStagedInsideThatFolderAndNothingIsLeftBehind() {
    var target = Path.Combine(this._disk, "target");

    var staged = this.WatchStaging(() =>
      Transfer.Run([Item(Location.InArchive(this._a, ""), "docs", folder: true)], this.Disk("target"), move: false));

    Assert.Multiple(() => {
      Assert.That(staged, Is.Not.Empty);
      Assert.That(staged.Select(Path.GetDirectoryName), Is.All.EqualTo(target),
        "extraction lands on the destination's own volume and reaches its name by a rename");
      Assert.That(Directory.GetDirectories(target, Transfer.StagingPrefix + "*"), Is.Empty);
      Assert.That(File.ReadAllText(Path.Combine(target, "docs", "guide", "intro.txt")), Is.EqualTo("intro"));
    });
  }

  [Test]
  public void GivenAFileCopiedOnDisk_WhenCopied_ThenItIsWrittenUnderAStagingNameBesideTheTargetFirst() {
    var target = Path.Combine(this._disk, "target");

    var staged = this.WatchStaging(() => Transfer.Run([Item(this.Disk(), "file.txt")], this.Disk("target"), move: false));

    Assert.That(staged.Select(Path.GetDirectoryName), Is.EqualTo(new[] { target }));
    Assert.That(File.ReadAllText(Path.Combine(target, "file.txt")), Is.EqualTo("disk file"));
    Assert.That(Directory.GetFileSystemEntries(target).Select(Path.GetFileName), Is.EqualTo(new[] { "file.txt" }));
  }

  [Test]
  public void GivenAFileMovedWithinOneVolume_WhenMoved_ThenNothingIsStaged()
    => Assert.That(this.WatchStaging(() => Transfer.Run([Item(this.Disk(), "file.txt")], this.Disk("target"), move: true)), Is.Empty,
      "a rename needs no copy at all");

  [Test]
  public void GivenFilesCopiedIntoAnArchive_WhenAdded_ThenTheyAreStagedBesideTheArchive() {
    var staged = this.WatchStaging(() =>
      Transfer.Run([Item(Location.InArchive(this._a, ""), "docs", folder: true)], Location.InArchive(this._b, ""), move: false));

    Assert.That(staged.Select(Path.GetDirectoryName), Is.All.EqualTo(Path.GetDirectoryName(this._b)));
    Assert.That(Directory.GetDirectories(this._root, Transfer.StagingPrefix + "*"), Is.Empty);
  }

  // ── files from other applications ───────────────────────────────────────────────────────────

  private static IncomingFile Incoming(string path, string text)
    => new(path, () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)));

  [Test]
  public void GivenAnAttachmentFromAnotherApplication_WhenReceivedIntoAFolder_ThenItArrivesStagedBesideItAndRenamedIntoPlace() {
    var target = Path.Combine(this._disk, "target");

    var staged = this.WatchStaging(() => Transfer.Receive([Incoming("invoice.pdf", "pdf bytes")], this.Disk("target")));

    Assert.Multiple(() => {
      Assert.That(File.ReadAllText(Path.Combine(target, "invoice.pdf")), Is.EqualTo("pdf bytes"));
      Assert.That(staged.Select(Path.GetDirectoryName), Is.All.EqualTo(target));
      Assert.That(Directory.GetFileSystemEntries(target).Select(Path.GetFileName), Is.EqualTo(new[] { "invoice.pdf" }),
        "no staging folder is left behind");
    });
  }

  [Test]
  public void GivenADroppedFolderOfVirtualFiles_WhenReceived_ThenItKeepsItsShapeAndIsRenamedAsAWholeWhenTaken() {
    Directory.CreateDirectory(Path.Combine(this._disk, "target", "mail"));

    var result = Transfer.Receive([
      new IncomingFile("mail", null),
      Incoming("mail/body.txt", "hello"),
      Incoming("mail/att/a.bin", "a"),
    ], this.Disk("target"));

    Assert.That(result.Created, Is.EqualTo(new[] { "mail (2)" }));
    Assert.That(File.ReadAllText(Path.Combine(this._disk, "target", "mail (2)", "att", "a.bin")), Is.EqualTo("a"));
  }

  [Test]
  public void GivenVirtualFiles_WhenReceivedIntoAnArchiveFolder_ThenTheArchiveHoldsThem() {
    Transfer.Receive([Incoming("note.txt", "note"), Incoming("readme.txt", "clash")], Location.InArchive(this._a, "docs/"));

    var contents = ZipContents(this._a);
    Assert.That(contents["docs/note.txt"], Is.EqualTo("note"));
    Assert.That(contents["docs/readme (2).txt"], Is.EqualTo("clash"));
    Assert.That(contents["docs/readme.txt"], Is.EqualTo("a readme"), "nothing is replaced");
  }

  [TestCase("")]
  [TestCase("../escape.txt")]
  [TestCase("a/../../b.txt")]
  [TestCase("a//b.txt")]
  public void GivenAVirtualFileNameThatCannotBeWritten_WhenReceived_ThenTheWholeDropIsRefused(string path) {
    var files = new[] { Incoming("fine.txt", "x"), Incoming(path, "y") };

    Assert.That(Transfer.WhyNot(files, this.Disk("target")), Is.Not.Null);
    Assert.Throws<InvalidOperationException>(() => Transfer.Receive(files, this.Disk("target")));
    Assert.That(Directory.GetFileSystemEntries(Path.Combine(this._disk, "target")), Is.Empty);
  }

  [Test]
  public void GivenAnAbsoluteVirtualFileName_WhenChecked_ThenItIsRefused()
    => Assert.That(Transfer.WhyNot([Incoming(Path.Combine(this._root, "abs.txt"), "x")], this.Disk("target")), Is.Not.Null);

  // ── opening one entry as a stream ───────────────────────────────────────────────────────────

  [Test]
  public void GivenAnArchiveEntry_WhenOpenedAsAStream_ThenItsBytesAreReadAndTheArchiveIsReleasedOnDispose() {
    using (var stream = ArchiveOperations.OpenEntry(this._a, "docs/guide/intro.txt"))
    using (var reader = new StreamReader(stream))
      Assert.That(reader.ReadToEnd(), Is.EqualTo("intro"));

    Assert.DoesNotThrow(() => File.Move(this._a, this._a + ".moved"), "the archive file must not stay locked after the stream is disposed");
  }

  // ── what is refused before anything is touched ──────────────────────────────────────────────

  [Test]
  public void GivenAFolder_WhenPastedInsideItself_ThenItIsRefused() {
    Assert.That(Transfer.WhyNot([Item(this.Disk(), "folder", folder: true)], this.Disk("folder", "deep"), move: false),
      Does.Contain("inside itself"));
    Assert.That(Transfer.WhyNot([Item(Location.InArchive(this._a, ""), "docs", folder: true)], Location.InArchive(this._a, "docs/guide/"), move: true),
      Does.Contain("inside itself"));
  }

  [Test]
  public void GivenAFolderNextToOneWithALongerName_WhenPastedIntoThatOne_ThenItIsAllowed()
    => Assert.That(Transfer.WhyNot([Item(this.Disk(), "folder", folder: true)], this.Disk("target"), move: false), Is.Null);

  [Test]
  public void GivenATargetThatCannotBeModified_WhenPastedInto_ThenItIsRefusedAndNothingChanges() {
    var gz = Path.Combine(this._root, "single.txt.gz");
    using (var gzip = new GZipStream(File.Create(gz), CompressionLevel.Optimal)) gzip.Write("x"u8);

    Assert.That(Transfer.WhyNot([Item(this.Disk(), "file.txt")], Location.InArchive(gz, ""), move: false), Does.Contain("cannot be modified"));
    Assert.Throws<InvalidOperationException>(() => Transfer.Run([Item(this.Disk(), "file.txt")], Location.InArchive(gz, ""), move: true));
    Assert.That(File.Exists(Path.Combine(this._disk, "file.txt")), Is.True);
  }

  [Test]
  public void GivenATargetThatNoLongerExists_WhenPastedInto_ThenItIsRefused()
    => Assert.That(Transfer.WhyNot([Item(this.Disk(), "file.txt")], this.Disk("gone"), move: false), Does.StartWith("No longer exists"));

  [Test]
  public void GivenNothing_WhenPasted_ThenItIsRefused()
    => Assert.That(Transfer.WhyNot([], this.Disk(), move: false), Is.Not.Null);

  // ── what a drag does by default ─────────────────────────────────────────────────────────────

  [Test]
  public void GivenADragWithinOneVolume_WhenAskedWhatItDoes_ThenItMoves()
    => Assert.That(Transfer.MovesByDefault([Item(this.Disk(), "file.txt")], this.Disk("target")), Is.True);

  [Test]
  public void GivenADragWithinOneArchive_WhenAskedWhatItDoes_ThenItMoves()
    => Assert.That(Transfer.MovesByDefault([Item(Location.InArchive(this._a, ""), "readme.txt")], Location.InArchive(this._a, "docs/")), Is.True);

  [Test]
  public void GivenADragBetweenAnArchiveAndTheDisk_WhenAskedWhatItDoes_ThenItCopiesEitherWay() {
    Assert.That(Transfer.MovesByDefault([Item(Location.InArchive(this._a, ""), "readme.txt")], this.Disk("target")), Is.False);
    Assert.That(Transfer.MovesByDefault([Item(this.Disk(), "file.txt")], Location.InArchive(this._a, "")), Is.False);
  }

  [Test]
  public void GivenADragBetweenTwoArchives_WhenAskedWhatItDoes_ThenItCopies()
    => Assert.That(Transfer.MovesByDefault([Item(Location.InArchive(this._a, ""), "readme.txt")], Location.InArchive(this._b, "")), Is.False);

  [Test]
  public void GivenNothingDragged_WhenAskedWhatItDoes_ThenItDoesNotMove()
    => Assert.That(Transfer.MovesByDefault([], this.Disk()), Is.False);

  // ── numbering ───────────────────────────────────────────────────────────────────────────────

  [TestCase("report.txt", new string[0], "report.txt")]
  [TestCase("report.txt", new[] { "report.txt" }, "report (2).txt")]
  [TestCase("report.txt", new[] { "REPORT.TXT", "report (2).txt" }, "report (3).txt")]
  [TestCase("folder", new[] { "folder" }, "folder (2)")]
  [TestCase(".bashrc", new[] { ".bashrc" }, ".bashrc (2)")]
  [TestCase("a.tar.gz", new[] { "a.tar.gz" }, "a.tar (2).gz")]
  public void GivenTakenNames_WhenAFreeOneIsPicked_ThenTheFirstUnusedNumberIsUsed(string name, string[] taken, string expected) {
    var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);

    Assert.That(Transfer.FreeName(name, set), Is.EqualTo(expected));
    Assert.That(set, Does.Contain(expected), "a picked name is taken for the next item in the same paste");
  }
}
