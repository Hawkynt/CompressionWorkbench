using System.Reflection;
using Compression.Lib;
using Compression.NativeUI;
using Compression.NativeUI.Editing;
using Compression.NativeUI.Navigation;
using Compression.NativeUI.ViewModels;

namespace Compression.Tests.NativeUI;

/// <summary>
/// The shell asks for a password when an archive needs one: on opening an archive with encrypted
/// names, again after a wrong one, and not at all once it knows the right one.
/// </summary>
[TestFixture]
public class ShellPasswordTests {
  private string _root = null!;
  private MainViewModel _model = null!;
  private readonly List<(string Archive, bool WasWrong)> _asked = [];

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "cwb-shellpw-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._root);
    UserSettings.PathOverride = Path.Combine(this._root, "settings.json");
    FormatRegistration.EnsureInitialized();
    this._model = new MainViewModel();
    this._asked.Clear();
  }

  [TearDown]
  public void TearDown() {
    UserSettings.PathOverride = null;
    try { Directory.Delete(this._root, recursive: true); } catch { }
  }

  private string Vector(string name) {
    using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("SevenZipConformanceVectors." + name)!;
    var path = Path.Combine(this._root, name);
    using (var file = File.Create(path))
      resource.CopyTo(file);
    return path;
  }

  private void Answer(params string?[] answers) {
    var queue = new Queue<string?>(answers);
    this._model.PasswordPrompt = (archive, wasWrong) => {
      this._asked.Add((archive, wasWrong));
      return queue.Count > 0 ? queue.Dequeue() : null;
    };
  }

  [Test]
  public void Given_AnArchiveWithEncryptedNames_When_Opened_Then_ThePasswordIsAskedForUntilItIsRight() {
    this.Answer("wrong", "S3cret!");

    this._model.Open(this.Vector("aes-mhe.7z"));

    Assert.That(this._asked, Is.EqualTo(new[] { ("aes-mhe.7z", false), ("aes-mhe.7z", true) }));
    Assert.That(this._model.ArchivePassword, Is.EqualTo("S3cret!"));
    Assert.That(this._model.HasArchive, Is.True);
  }

  [Test]
  public void Given_AnArchiveWithEncryptedNames_When_ThePromptIsCancelled_Then_NothingOpensAndTheStatusSaysWhy() {
    this.Answer();

    this._model.Open(this.Vector("aes-mhe.7z"));

    Assert.That(this._asked, Has.Count.EqualTo(1));
    Assert.That(this._model.HasArchive, Is.False);
    Assert.That(this._model.StatusText, Does.Contain("password"));
  }

  [Test]
  public void Given_AContentEncryptedArchive_When_AnEntryIsRead_Then_ThePasswordIsAskedOnceAndKept() {
    this.Answer("S3cret!");
    var archive = this.Vector("aes.7z");
    this._model.Open(archive);
    Assert.That(this._asked, Is.Empty, "the names are not encrypted");

    var first = this._model.WithPassword("aes.7z", pw => ArchiveOperations.ExtractEntry(archive, "text.txt", pw));
    var second = this._model.WithPassword("aes.7z", pw => ArchiveOperations.ExtractEntry(archive, "text.txt", pw));

    Assert.That(first, Has.Length.EqualTo(6000));
    Assert.That(second, Is.EqualTo(first));
    Assert.That(this._asked, Has.Count.EqualTo(1));
  }

  [Test]
  public async Task Given_AnEncryptedEntry_When_CopiedToAFolder_Then_ThePasswordIsAskedAndItIsStagedInsideTheTarget() {
    this.Answer("S3cret!");
    var archive = this.Vector("aes.7z");
    this._model.Open(archive);
    var target = Path.Combine(this._root, "target");
    Directory.CreateDirectory(target);
    var staged = new List<string>();
    void Record(string path) => staged.Add(path);
    Transfer.StagingCreated += Record;
    try {
      var error = await this._model.TransferAsync([new TransferItem(Location.InArchive(archive, "code/"), "x86.bin", false)], Location.Folder(target), move: false);
      Assert.That(error, Is.Null);
    } finally {
      Transfer.StagingCreated -= Record;
    }

    Assert.That(this._asked, Has.Count.EqualTo(1));
    Assert.That(new FileInfo(Path.Combine(target, "x86.bin")).Length, Is.EqualTo(9000));
    Assert.That(staged, Is.Not.Empty);
    Assert.That(staged, Is.All.StartsWith(target), "extraction is staged beside the destination, never in %TEMP%");
    Assert.That(Directory.EnumerateDirectories(target, Transfer.StagingPrefix + "*"), Is.Empty, "the staging folder is gone");
  }

  [Test]
  public async Task Given_AnEncryptedEntry_When_ThePromptIsCancelled_Then_NothingIsCopied() {
    this.Answer();
    var archive = this.Vector("aes.7z");
    this._model.Open(archive);
    var target = Path.Combine(this._root, "target");
    Directory.CreateDirectory(target);

    var error = await this._model.TransferAsync([new TransferItem(Location.InArchive(archive, ""), "text.txt", false)], Location.Folder(target), move: false);

    Assert.That(error, Does.Contain("password"));
    Assert.That(Directory.EnumerateFileSystemEntries(target), Is.Empty);
  }

  [Test]
  public void Given_AnEncryptedEntry_When_DraggedOut_Then_ThePasswordIsSettledFirstAndTheFileReadsBack() {
    this.Answer("wrong", "S3cret!");
    var archive = this.Vector("aes.7z");
    this._model.Open(archive);
    this._model.NavigateTo(Location.InArchive(archive, ""));
    this._model.SelectedEntries.Clear();
    this._model.SelectedEntries.Add(this._model.Entries.Single(e => e.Name == "text.txt"));

    var payload = this._model.DragPayload([.. this._model.SelectedEntries]) as Hawkynt.NativeForms.VirtualFile[];

    Assert.That(this._asked, Is.EqualTo(new[] { ("aes.7z", false), ("aes.7z", true) }));
    Assert.That(payload, Has.Length.EqualTo(1));
    using var stream = payload![0].OpenRead();
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    Assert.That(copy.Length, Is.EqualTo(6000));
  }

  [Test]
  public void Given_AnEncryptedEntry_When_TheDragPromptIsCancelled_Then_NothingIsDragged() {
    this.Answer();
    var archive = this.Vector("aes.7z");
    this._model.Open(archive);
    this._model.NavigateTo(Location.InArchive(archive, ""));
    this._model.SelectedEntries.Clear();
    this._model.SelectedEntries.Add(this._model.Entries.Single(e => e.Name == "text.txt"));

    Assert.That(this._model.DragPayload([.. this._model.SelectedEntries]), Is.Null);
  }

  [Test]
  public void Given_AnOpenEncryptedArchive_When_AnotherArchiveIsOpened_Then_ThePasswordIsForgotten() {
    this.Answer("S3cret!");
    this._model.Open(this.Vector("aes-mhe.7z"));
    Assert.That(this._model.ArchivePassword, Is.Not.Null);

    this._model.Open(this.Vector("bcj.7z"));

    Assert.That(this._model.ArchivePassword, Is.Null);
  }
}
