using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Compression.Lib;
using Compression.NativeUI;
using Compression.NativeUI.Navigation;
using Compression.NativeUI.ViewModels;
using Compression.NativeUI.Editing;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>
/// Copy, cut and paste from the shell's point of view: what is on offer when, where a paste lands,
/// and what a cut leaves behind.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ShellClipboardTests {
  private string _root = null!;
  private string _zip = null!;
  private MainViewModel _model = null!;

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "cwb-clip-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this._root, "target"));
    File.WriteAllText(Path.Combine(this._root, "file.txt"), "disk file");

    UserSettings.PathOverride = Path.Combine(this._root, "settings.json");
    FormatRegistration.EnsureInitialized();

    this._zip = Path.Combine(this._root, "bundle.zip");
    using (var zip = ZipFile.Open(this._zip, ZipArchiveMode.Create)) {
      using var writer = new StreamWriter(zip.CreateEntry("docs/readme.txt").Open());
      writer.Write("readme");
    }

    this._model = new MainViewModel();
  }

  [TearDown]
  public void TearDown() {
    UserSettings.PathOverride = null;
    try { Directory.Delete(this._root, recursive: true); } catch { }
  }

  private void Select(params string[] names) {
    this._model.SelectedEntries.Clear();
    foreach (var name in names) this._model.SelectedEntries.Add(this._model.Entries.Single(e => e.Name == name));
  }

  private string[] ZipNames() {
    using var zip = ZipFile.OpenRead(this._zip);
    return [.. zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName).Order(StringComparer.Ordinal)];
  }

  [Test]
  public void GivenNothingSelected_WhenAskedWhatIsOnOffer_ThenNeitherCopyNorCutNorPasteIs() {
    this._model.NavigateTo(Location.Folder(this._root));

    Assert.Multiple(() => {
      Assert.That(this._model.CopyCommand.CanExecute(null), Is.False);
      Assert.That(this._model.CutCommand.CanExecute(null), Is.False);
      Assert.That(this._model.PasteCommand.CanExecute(null), Is.False, "the clipboard is empty");
    });
  }

  [Test]
  public void GivenAnArchiveEntryCopied_WhenPastedIntoAFolderOnDisk_ThenItIsExtractedThereAndStaysInTheArchive() {
    this._model.NavigateTo(Location.InArchive(this._zip, "docs/"));
    this.Select("readme.txt");
    this._model.CopyCommand.Execute(null);

    this._model.NavigateTo(Location.Folder(Path.Combine(this._root, "target")));
    Assert.That(this._model.PasteCommand.CanExecute(null), Is.True);
    var error = this._model.PasteAsync().GetAwaiter().GetResult();

    Assert.Multiple(() => {
      Assert.That(error, Is.Null);
      Assert.That(File.ReadAllText(Path.Combine(this._root, "target", "readme.txt")), Is.EqualTo("readme"));
      Assert.That(this.ZipNames(), Does.Contain("docs/readme.txt"));
      Assert.That(this._model.Entries.Select(e => e.Name), Does.Contain("readme.txt"), "the list shows what arrived");
      Assert.That(this._model.HasClipboard, Is.True, "a copy can be pasted again");
    });
  }

  [Test]
  public void GivenAFileCut_WhenPastedIntoAnArchiveFolder_ThenItMovesInAndTheClipboardEmpties() {
    this._model.NavigateTo(Location.Folder(this._root));
    this.Select("file.txt");
    this._model.CutCommand.Execute(null);

    this._model.NavigateTo(Location.InArchive(this._zip, "docs/"));
    var error = this._model.PasteAsync().GetAwaiter().GetResult();

    Assert.Multiple(() => {
      Assert.That(error, Is.Null);
      Assert.That(this.ZipNames(), Is.EqualTo(new[] { "docs/file.txt", "docs/readme.txt" }));
      Assert.That(File.Exists(Path.Combine(this._root, "file.txt")), Is.False);
      Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.InArchive(this._zip, "docs/")));
      Assert.That(this._model.HasClipboard, Is.False, "what was cut has gone; a second paste has nothing to move");
    });
  }

  [Test]
  public void GivenACopyPastedTwiceIntoTheSameFolder_WhenDone_ThenTheSecondGetsANumber() {
    this._model.NavigateTo(Location.Folder(this._root));
    this.Select("file.txt");
    this._model.CopyCommand.Execute(null);

    this._model.PasteAsync().GetAwaiter().GetResult();
    this._model.PasteAsync().GetAwaiter().GetResult();

    Assert.That(Directory.GetFiles(this._root, "file*.txt").Select(Path.GetFileName).Order(),
      Is.EqualTo(new[] { "file (2).txt", "file (3).txt", "file.txt" }));
  }

  [Test]
  public void GivenAFolderCopied_WhenPastedInsideItself_ThenTheReasonIsShownAndNothingIsCopied() {
    Directory.CreateDirectory(Path.Combine(this._root, "outer", "inner"));
    this._model.NavigateTo(Location.Folder(this._root));
    this.Select("outer");
    this._model.CopyCommand.Execute(null);
    this._model.NavigateTo(Location.Folder(Path.Combine(this._root, "outer", "inner")));

    var error = this._model.PasteAsync().GetAwaiter().GetResult();

    Assert.That(error, Does.Contain("inside itself"));
    Assert.That(this._model.StatusText, Is.EqualTo(error));
    Assert.That(Directory.GetDirectories(Path.Combine(this._root, "outer", "inner")), Is.Empty);
  }

  // ── one change at a time ────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAChangeIsRunning_WhenAnotherTransferIsAsked_ThenItIsRefusedAndNothingIsTouched() {
    this._model.NavigateTo(Location.Folder(this._root));
    var begin = typeof(MainViewModel).GetMethod("BeginChange", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var end = typeof(MainViewModel).GetMethod("EndChange", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    Assert.That(begin.Invoke(this._model, [TimeSpan.Zero]), Is.Null, "the first change is granted");

    try {
      var items = new[] { new TransferItem(Location.Folder(this._root), "file.txt", false) };
      Assert.Multiple(() => {
        Assert.That(this._model.WhyNotTransfer(items, Location.Folder(Path.Combine(this._root, "target")), move: false), Does.Contain("still running"));
        Assert.That(this._model.TryBeginBackgroundRead(), Is.False, "background reads stay away while a change runs");
        Assert.That(this._model.PasteCommand.CanExecute(null), Is.False);
      });
      Assert.That(File.Exists(Path.Combine(this._root, "target", "file.txt")), Is.False);
    } finally {
      end.Invoke(this._model, []);
    }

    Assert.That(this._model.TryBeginBackgroundRead(), Is.True, "reads resume once the change is over");
    this._model.EndBackgroundRead();
  }

  [Test]
  public void GivenACompletedTransfer_WhenItFinishes_ThenTheChangedPlacesAreAnnounced() {
    this._model.NavigateTo(Location.Folder(this._root));
    IReadOnlyList<Location>? announced = null;
    this._model.Changed += (_, places) => announced = places;
    this.Select("file.txt");
    this._model.CutCommand.Execute(null);
    var target = Location.Folder(Path.Combine(this._root, "target"));
    this._model.NavigateTo(target);

    this._model.PasteAsync().GetAwaiter().GetResult();

    Assert.That(announced, Is.EquivalentTo(new[] { target, Location.Folder(this._root) }), "a move changes both ends");
  }

  [Test]
  public void GivenAFolderInsideAnArchiveThatNoLongerExists_WhenNavigatedTo_ThenTheShellSaysSo() {
    Assert.That(this._model.NavigateTo(Location.InArchive(this._zip, "gone/")), Is.False);
    Assert.That(this._model.StatusText, Does.StartWith("No longer exists"));
  }

  [Test]
  public void GivenAListing_WhenItIsShown_ThenListenersHearOneResetRatherThanOneEventPerRow() {
    var events = 0;
    this._model.Entries.CollectionChanged += (_, _) => ++events;

    this._model.NavigateTo(Location.Folder(this._root));

    Assert.That(events, Is.EqualTo(1));
  }

  // ── what a drag carries to other applications ───────────────────────────────────────────────

  [Test]
  public void GivenFilesOnDisk_WhenDraggedOut_ThenTheirOwnPathsTravel() {
    this._model.NavigateTo(Location.Folder(this._root));
    this.Select("file.txt", "target");

    Assert.That(this._model.DragPayload([.. this._model.SelectedEntries]),
      Is.EquivalentTo(new[] { Path.Combine(this._root, "file.txt"), Path.Combine(this._root, "target") }));
  }

  [Test]
  public void GivenAnArchiveEntry_WhenDraggedOut_ThenItTravelsAsAFileReadOnlyWhenAsked() {
    this._model.NavigateTo(Location.InArchive(this._zip, "docs/"));
    this.Select("readme.txt");

    var payload = this._model.DragPayload([.. this._model.SelectedEntries]) as Hawkynt.NativeForms.VirtualFile[];

    Assert.That(payload, Has.Length.EqualTo(1));
    Assert.That(payload![0].RelativePath, Is.EqualTo("readme.txt"));
    Assert.That(payload[0].Length, Is.EqualTo(6));
    using var reader = new StreamReader(payload[0].OpenRead());
    Assert.That(reader.ReadToEnd(), Is.EqualTo("readme"));
  }

  [Test]
  public void GivenAnArchiveFolder_WhenDraggedOut_ThenEverythingBeneathItTravelsUnderTheFolderName() {
    using (var zip = ZipFile.Open(this._zip, ZipArchiveMode.Update)) {
      using var writer = new StreamWriter(zip.CreateEntry("docs/deep/inner.txt").Open());
      writer.Write("inner");
    }

    this._model.NavigateTo(Location.InArchive(this._zip, ""));
    this.Select("docs");

    var payload = (Hawkynt.NativeForms.VirtualFile[])this._model.DragPayload([.. this._model.SelectedEntries])!;

    Assert.That(payload.Where(f => !f.IsDirectory).Select(f => f.RelativePath),
      Is.EquivalentTo(new[] { "docs/readme.txt", "docs/deep/inner.txt" }));
    Assert.That(payload.Any(f => f.IsDirectory && f.RelativePath == "docs"), Is.True);
  }

  [Test]
  public void GivenNothingButTheParentRow_WhenDraggedOut_ThenNothingTravels() {
    this._model.NavigateTo(Location.InArchive(this._zip, "docs/"));

    Assert.That(this._model.DragPayload([this._model.Entries.Single(e => e.IsParentEntry)]), Is.Null);
  }

  // ── files dropped from other applications ───────────────────────────────────────────────────

  [Test]
  public void GivenAMailAttachmentDropped_WhenReceivedIntoAnArchiveFolder_ThenTheArchiveHoldsItAndTheListShowsIt() {
    this._model.NavigateTo(Location.InArchive(this._zip, "docs/"));

    var error = this._model.ReceiveDropped(
      [new IncomingFile("invoice.pdf", () => new MemoryStream("pdf"u8.ToArray()))], Location.InArchive(this._zip, "docs/"));

    Assert.That(error, Is.Null);
    Assert.That(this.ZipNames(), Does.Contain("docs/invoice.pdf"));
    Assert.That(this._model.Entries.Select(e => e.Name), Does.Contain("invoice.pdf"));
    Assert.That(this._model.CurrentLocation, Is.EqualTo(Location.InArchive(this._zip, "docs/")));
  }

  [Test]
  public void GivenADropWithAnUnwritableName_WhenReceived_ThenTheReasonIsShownAndNothingIsWritten() {
    this._model.NavigateTo(Location.Folder(this._root));

    var error = this._model.ReceiveDropped([new IncomingFile("../outside.txt", () => new MemoryStream())], Location.Folder(this._root));

    Assert.That(error, Is.Not.Null);
    Assert.That(this._model.StatusText, Is.EqualTo(error));
    Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(this._root)!, "outside.txt")), Is.False);
  }
}
