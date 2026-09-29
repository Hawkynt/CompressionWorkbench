using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Compression.Lib;
using Compression.NativeUI;
using Compression.NativeUI.Navigation;
using Compression.NativeUI.ViewModels;
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
}
