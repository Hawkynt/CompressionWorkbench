using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Compression.Lib;
using Compression.NativeUI;
using Compression.NativeUI.Controls;
using Compression.NativeUI.Navigation;
using Compression.NativeUI.ViewModels;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>
/// The preview pane beside the file list: what it decides to show for a given entry, and what the
/// shell is willing to read for it.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class PreviewPaneTests {
  // ── is it text? ─────────────────────────────────────────────────────────────────────────────

  [TestCase("plain ascii\r\nwith lines\tand tabs")]
  [TestCase("ünïcödé — 名前")]
  [TestCase("")]
  public void GivenUtf8Text_WhenSniffed_ThenItIsReadBack(string text)
    => Assert.That(PreviewPane.AsText(Encoding.UTF8.GetBytes(text), 1000), Is.EqualTo(text));

  [Test]
  public void GivenUtf16WithAByteOrderMark_WhenSniffed_ThenItIsDecodedAsUtf16() {
    var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("wide text")).ToArray();

    Assert.That(PreviewPane.AsText(bytes, 1000), Is.EqualTo("wide text"), "without the mark the NULs would read as binary");
  }

  [Test]
  public void GivenAUtf8ByteOrderMark_WhenSniffed_ThenTheMarkIsNotShown()
    => Assert.That(PreviewPane.AsText([0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i'], 1000), Is.EqualTo("hi"));

  [Test]
  public void GivenANulByte_WhenSniffed_ThenItIsBinary()
    => Assert.That(PreviewPane.AsText("text\0more"u8.ToArray(), 1000), Is.Null);

  [Test]
  public void GivenRandomBytes_WhenSniffed_ThenTheyAreBinary() {
    var bytes = new byte[4096];
    new Random(7).NextBytes(bytes);
    bytes = [.. bytes.Select(b => b == 0 ? (byte)1 : b)];

    Assert.That(PreviewPane.AsText(bytes, 1000), Is.Null, "invalid UTF-8 decodes to replacement characters, which count against it");
  }

  [TestCase(1, false)]
  [TestCase(2, true)]
  public void GivenControlCharactersAtTheThreshold_WhenSniffed_ThenOneInAHundredIsStillText(int controls, bool binary) {
    var text = new string('a', 100 - controls) + new string('\u0001', controls);

    Assert.That(PreviewPane.AsText(Encoding.ASCII.GetBytes(text), 1000) is null, Is.EqualTo(binary));
  }

  [Test]
  public void GivenLongText_WhenSniffed_ThenOnlyTheExcerptIsReturned()
    => Assert.That(PreviewPane.AsText(Encoding.ASCII.GetBytes(new string('x', 5000)), 100), Has.Length.EqualTo(100));

  // ── what the pane shows ─────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenEachKindOfContent_WhenShown_ThenThePanePicksPictureTextOrCaption() {
    HeadlessBackend.Install();
    var pane = new PreviewPane();

    pane.ShowContent("dot.bmp", Bitmap24(3, 2));
    Assert.That((pane.Kind, pane.Caption), Is.EqualTo((PreviewKind.Image, $"dot.bmp{Environment.NewLine}3 × 2")));

    pane.ShowContent("notes.txt", "hello"u8.ToArray());
    Assert.That((pane.Kind, pane.Excerpt), Is.EqualTo((PreviewKind.Text, "hello")));

    pane.ShowContent("blob.bin", [0, 1, 2, 3, 0, 255]);
    Assert.That((pane.Kind, pane.Caption), Is.EqualTo((PreviewKind.None, $"blob.bin{Environment.NewLine}No preview")));
  }

  /// <summary>A minimal bottom-up 24-bit BMP, which every decoder tier reads.</summary>
  private static byte[] Bitmap24(int width, int height) {
    var stride = (width * 3 + 3) & ~3;
    var pixels = stride * height;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((byte)'B'); w.Write((byte)'M'); w.Write(54 + pixels); w.Write(0); w.Write(54);
    w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24);
    w.Write(0); w.Write(pixels); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
    for (var y = 0; y < height; ++y) {
      for (var x = 0; x < width; ++x) { w.Write((byte)0x20); w.Write((byte)0x80); w.Write((byte)0xE0); }
      for (var pad = width * 3; pad < stride; ++pad) w.Write((byte)0);
    }

    return ms.ToArray();
  }

  // ── what the shell reads for it ─────────────────────────────────────────────────────────────

  private string _root = null!;
  private MainViewModel _model = null!;

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "cwb-pane-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this._root, "sub"));
    File.WriteAllText(Path.Combine(this._root, "note.txt"), "on disk");
    UserSettings.PathOverride = Path.Combine(this._root, "settings.json");
    FormatRegistration.EnsureInitialized();
    this._model = new MainViewModel();
  }

  [TearDown]
  public void TearDown() {
    UserSettings.PathOverride = null;
    try { Directory.Delete(this._root, recursive: true); } catch { }
  }

  [Test]
  public void GivenAFileOnDisk_WhenReadForPreview_ThenItsBytesComeBack() {
    this._model.NavigateTo(Location.Folder(this._root));

    var (data, caption) = this._model.ReadForPreview(this._model.Entries.Single(e => e.Name == "note.txt"));

    Assert.That(Encoding.UTF8.GetString(data!), Is.EqualTo("on disk"));
    Assert.That(caption, Is.EqualTo("note.txt"));
  }

  [Test]
  public void GivenAnArchiveEntry_WhenReadForPreview_ThenItIsExtracted() {
    var zip = Path.Combine(this._root, "a.zip");
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
      using (var w = new StreamWriter(archive.CreateEntry("docs/in.txt").Open())) w.Write("in archive");
    this._model.NavigateTo(Location.InArchive(zip, "docs/"));

    var (data, _) = this._model.ReadForPreview(this._model.Entries.Single(e => e.Name == "in.txt"));

    Assert.That(Encoding.UTF8.GetString(data!), Is.EqualTo("in archive"));
  }

  [Test]
  public void GivenAFolder_WhenReadForPreview_ThenOnlyACaptionComesBack() {
    this._model.NavigateTo(Location.Folder(this._root));

    var (data, caption) = this._model.ReadForPreview(this._model.Entries.Single(e => e.Name == "sub"));

    Assert.That(data, Is.Null);
    Assert.That(caption, Does.EndWith("Folder"));
  }

  [TestCase(MainViewModel.PreviewPaneLimit, false)]
  [TestCase(MainViewModel.PreviewPaneLimit + 1, true)]
  public void GivenAnEntryAtTheSizeLimit_WhenReadForPreview_ThenOnlyALargerOneIsSkipped(long size, bool skipped) {
    var zip = Path.Combine(this._root, "a.zip");
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry("x.bin");
    this._model.NavigateTo(Location.InArchive(zip, ""));
    var claimed = new ArchiveEntryViewModel { Name = "x.bin", Path = "x.bin", OriginalSize = size };

    var (_, caption) = this._model.ReadForPreview(claimed);

    Assert.That(caption.Contains("Too large"), Is.EqualTo(skipped));
  }

  [Test]
  public void GivenAFileThatVanished_WhenReadForPreview_ThenTheCaptionSaysItCannotBeRead() {
    this._model.NavigateTo(Location.Folder(this._root));
    var entry = this._model.Entries.Single(e => e.Name == "note.txt");
    File.Delete(Path.Combine(this._root, "note.txt"));

    var (data, caption) = this._model.ReadForPreview(entry);

    Assert.That(data, Is.Null);
    Assert.That(caption, Does.Contain("Cannot be read"));
  }
}
