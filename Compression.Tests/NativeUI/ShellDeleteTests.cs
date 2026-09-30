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
/// Deleting from the shell once the user has confirmed: files and folders on disk, entries inside an
/// archive, and a delete that has to wait its turn.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ShellDeleteTests {
  private string _root = null!;
  private string _zip = null!;
  private MainViewModel _model = null!;

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "cwb-delete-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this._root, "folder", "deeper"));
    File.WriteAllText(Path.Combine(this._root, "folder", "deeper", "inside.txt"), "inside");
    File.WriteAllText(Path.Combine(this._root, "file.txt"), "content");
    File.WriteAllText(Path.Combine(this._root, "keep.txt"), "keep");

    UserSettings.PathOverride = Path.Combine(this._root, "settings.json");
    FormatRegistration.EnsureInitialized();

    this._zip = Path.Combine(this._root, "bundle.zip");
    using (var zip = ZipFile.Open(this._zip, ZipArchiveMode.Create)) {
      Write(zip, "docs/guide/intro.txt", "intro");
      Write(zip, "docs/readme.txt", "readme");
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

  private string? Delete(params string[] names)
    => this._model.DeleteAsync([.. names.Select(n => this._model.Entries.Single(e => e.Name == n))]).GetAwaiter().GetResult();

  private Dictionary<string, string> ZipContents() {
    using var zip = ZipFile.OpenRead(this._zip);
    return zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToDictionary(e => e.FullName, e => {
      using var reader = new StreamReader(e.Open());
      return reader.ReadToEnd();
    });
  }

  [Test]
  public void GivenAFileAndAFolderOnDisk_WhenDeleted_ThenBothAreGoneWithEverythingBeneathAndTheRestStays() {
    this._model.NavigateTo(Location.Folder(this._root));

    Assert.That(this.Delete("file.txt", "folder"), Is.Null);

    Assert.Multiple(() => {
      Assert.That(File.Exists(Path.Combine(this._root, "file.txt")), Is.False);
      Assert.That(Directory.Exists(Path.Combine(this._root, "folder")), Is.False);
      Assert.That(File.ReadAllText(Path.Combine(this._root, "keep.txt")), Is.EqualTo("keep"));
      Assert.That(this._model.Entries.Select(e => e.Name), Does.Not.Contain("file.txt").And.Not.Contain("folder"));
    });
  }

  [Test]
  public void GivenAFolderInsideAZip_WhenDeleted_ThenEveryEntryBeneathItGoesAndTheRestReadsBackIntact() {
    this._model.NavigateTo(Location.InArchive(this._zip, ""));

    Assert.That(this.Delete("docs"), Is.Null);

    Assert.That(this.ZipContents(), Is.EqualTo(new Dictionary<string, string> { ["top.txt"] = "top" }));
    Assert.That(this._model.Entries.Where(e => !e.IsParentEntry).Select(e => e.Name), Is.EqualTo(new[] { "top.txt" }));
  }

  [Test]
  public void GivenOnlyTheParentRow_WhenDeleted_ThenNothingHappens() {
    this._model.NavigateTo(Location.Folder(this._root));
    var before = Directory.GetFileSystemEntries(this._root).Length;

    Assert.That(this._model.DeleteAsync([this._model.Entries.Single(e => e.IsParentEntry)]).GetAwaiter().GetResult(), Is.Null);

    Assert.That(Directory.GetFileSystemEntries(this._root), Has.Length.EqualTo(before));
  }

  [Test]
  public void GivenAChangeIsRunning_WhenADeleteIsAsked_ThenItIsRefusedAndNothingIsRemoved() {
    this._model.NavigateTo(Location.Folder(this._root));
    var begin = typeof(MainViewModel).GetMethod("BeginChange", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var end = typeof(MainViewModel).GetMethod("EndChange", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    Assume.That(begin.Invoke(this._model, [TimeSpan.Zero]), Is.Null);

    try {
      Assert.That(this.Delete("file.txt"), Does.Contain("still running"));
      Assert.That(File.Exists(Path.Combine(this._root, "file.txt")), Is.True);
    } finally {
      end.Invoke(this._model, []);
    }
  }

  [Test]
  public void GivenADelete_WhenItFinishes_ThenTheFolderItChangedIsAnnounced() {
    this._model.NavigateTo(Location.InArchive(this._zip, ""));
    IReadOnlyList<Location>? announced = null;
    this._model.Changed += (_, places) => announced = places;

    this.Delete("top.txt");

    Assert.That(announced, Is.EqualTo(new[] { Location.InArchive(this._zip, "") }));
  }
}
